using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Retrieval;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The confidence threshold and insufficient-evidence refusal (T45). Hits below the
/// threshold are dropped by the Package step; when nothing clears it — a below-
/// threshold record or a patient with no chunks at all — the answer path returns a
/// deterministic, language-localized refusal (200, refused:true, no citations) and
/// makes NO model call. "The search returned something" is not "the record supports
/// this" (ADR-0012).
/// </summary>
public class RefusalAndThresholdTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private static readonly float[] Query = [1f, 0f];
    private static readonly float[] Near = [0.98f, 0.02f];       // similarity ~1
    private static readonly float[] Middling = [0.6f, 0.8f];     // similarity ~0.6
    private static readonly float[] Orthogonal = [0f, 1f];       // similarity ~0

    private const string Line = "The patient reports taking insulin injections every single day for their diabetes.";

    [Fact]
    public async Task When_the_best_hit_is_below_the_threshold_the_turn_is_refused_with_no_model_call()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        // A high threshold nothing in this record reaches.
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb)
            .WithWebHostBuilder(b => b.UseSetting(PackageRetrievalStep.ConfidenceThresholdConfigurationKey, "0.9"));
        var client = factory.CreateClient();

        const string question = "Does the patient take insulin?";
        emb.Pin(question, Query);
        // The one chunk sits at ~0.6 similarity, its summary at ~0 — both below 0.9.
        await IngestTranscriptAsync(client, chat, emb, "refuse-below", "b-s1", Line, "Diabetes.", "Summary.", Middling);

        // Deliberately script NO answer: if the refusal path called the model, the
        // scripted client would throw and this would not be a 200.
        var response = await client.PostAsJsonAsync(
            "/patients/refuse-below/chat/answer", new { doctorId = "dr-a", question });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("refused").GetBoolean());
        Assert.Empty(body.GetProperty("citations").EnumerateArray());
        Assert.Equal("en", body.GetProperty("language").GetString());
        Assert.Contains("evidence", body.GetProperty("answer").GetString()!);

        // The generator is never invoked on the refusal path.
        Assert.DoesNotContain(chat.ReceivedPrompts, p => p.Contains("Evidence Items:"));
    }

    [Fact]
    public async Task A_patient_with_no_chunks_is_refused_not_answered_and_not_404()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "What do we know about this patient?";
        emb.Pin(question, Query);

        // No ingestion for this patient, and no scripted answer.
        var response = await client.PostAsJsonAsync(
            "/patients/nobody-at-all/chat/answer", new { doctorId = "dr-a", question });

        // 200 refusal, never 404 — patient existence is not leaked (ADR-0010/0012).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("refused").GetBoolean());
        Assert.Empty(body.GetProperty("citations").EnumerateArray());
        Assert.Contains("evidence", body.GetProperty("answer").GetString()!);
        Assert.DoesNotContain(chat.ReceivedPrompts, p => p.Contains("Evidence Items:"));
    }

    [Fact]
    public async Task Above_threshold_evidence_answers_and_below_threshold_hits_are_dropped()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb)
            .WithWebHostBuilder(b => b.UseSetting(PackageRetrievalStep.ConfidenceThresholdConfigurationKey, "0.5"));
        var client = factory.CreateClient();

        const string question = "Does the patient take insulin?";
        emb.Pin(question, Query);
        // Body ~1 (clears 0.5); its summary ~0 (dropped).
        await IngestTranscriptAsync(client, chat, emb, "refuse-above", "a-s1", Line, "Diabetes.", "Summary.", Near);

        chat.EnqueueResponse("The patient takes insulin daily [E1].");
        var response = await client.PostAsJsonAsync(
            "/patients/refuse-above/chat/answer", new { doctorId = "dr-a", question });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("refused").GetBoolean());
        Assert.Equal("The patient takes insulin daily [E1].", body.GetProperty("answer").GetString());

        // Only the above-threshold body survives; the orthogonal summary was dropped.
        var citations = body.GetProperty("citations").EnumerateArray().ToList();
        Assert.Single(citations);
        Assert.Equal(Line, citations[0].GetProperty("quote").GetString());
    }

    [Fact]
    public async Task The_refusal_is_localized_to_the_questions_language()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string greekQuestion = "Τι γνωρίζουμε για αυτόν τον ασθενή;";
        emb.Pin(greekQuestion, Query);

        var response = await client.PostAsJsonAsync(
            "/patients/nobody-greek/chat/answer", new { doctorId = "dr-a", question = greekQuestion });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("refused").GetBoolean());
        Assert.Equal("el", body.GetProperty("language").GetString());
        // The Greek refusal, not the English one.
        Assert.Contains("στοιχεία", body.GetProperty("answer").GetString()!);
    }

    private async Task IngestTranscriptAsync(
        HttpClient client, ScriptedChatClient chat, ControllableEmbeddingGenerator emb,
        string patientId, string sessionId, string line, string blurb, string summary, float[] bodyVector)
    {
        emb.Pin($"{blurb}\n\n{line}", bodyVector);
        emb.Pin(summary, Orthogonal);
        chat.EnqueueResponse(
            $$"""
            { "chunks": [ { "startLine": 0, "endLine": 0, "contextBlurb": {{JsonSerializer.Serialize(blurb)}} } ],
              "summary": {{JsonSerializer.Serialize(summary)}} }
            """);
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "dr-a",
            patientId,
            sessionId,
            sequenceNumber = 1,
            language = "en",
            transcript = line,
        });
        response.EnsureSuccessStatusCode();
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var status = (await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}"))
                .GetProperty("status").GetString()!;
            if (status == "Completed")
                return;
            Assert.NotEqual("Failed", status);
            await Task.Delay(50);
        }
        Assert.Fail($"Ingestion {ingestionId} never completed.");
    }
}
