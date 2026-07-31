using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Security;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The one public surface of the feature (T42): POST /patients/{patientId}/chat/answer.
/// Secret-authenticated, stateless — it maps the request onto a patient-scoped
/// retrieval, generates an answer over the evidence, and returns it with citations.
/// The patient in the route is the hard boundary; the body carries the question,
/// the asking doctor, and optional narrowing.
///
/// Generation is a seam (T43 makes it the DB-seeded grounded agent), so here the
/// answer text is scripted through the chat client and these tests assert the
/// endpoint contract: the response shape, citations packaged from the retrieved
/// evidence, and the route patient as the scope. Threshold/refusal (T45) and
/// citation verification (T46) are not yet wired.
/// </summary>
public class ChatAnswerEndpointTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private static readonly float[] Query = [1f, 0f];
    private static readonly float[] Near = [0.98f, 0.02f];
    private static readonly float[] Middling = [0.6f, 0.8f];
    private static readonly float[] Orthogonal = [0f, 1f];

    [Fact]
    public async Task It_answers_over_the_patients_evidence_and_returns_ordered_citations()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "Does the patient take insulin?";
        emb.Pin(question, Query);

        const string nearLine = "The patient reports taking insulin injections every single day for their diabetes.";
        const string farLine = "Blood pressure this morning measured one twenty over eighty which is entirely normal.";
        await IngestTranscriptAsync(client, chat, emb, "chat-alice", "dr-a", "alice-s1",
            nearLine, "Diabetes.", "Alice one.", Near, Orthogonal);
        await IngestTranscriptAsync(client, chat, emb, "chat-alice", "dr-a", "alice-s2",
            farLine, "Vitals.", "Alice two.", Middling, Orthogonal);

        // The scripted answer the (seam) generator returns for this turn.
        chat.EnqueueResponse("The patient takes insulin daily [E1].");

        var response = await client.PostAsJsonAsync("/patients/chat-alice/chat/answer", new
        {
            doctorId = "dr-a",
            question,
            topK = 8,
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("The patient takes insulin daily [E1].", body.GetProperty("answer").GetString());
        Assert.False(body.GetProperty("refused").GetBoolean());
        Assert.True(body.GetProperty("retrievalUsed").GetBoolean());
        Assert.Equal("en", body.GetProperty("language").GetString());

        var citations = body.GetProperty("citations").EnumerateArray().ToList();
        Assert.Equal(4, citations.Count); // two documents × (body + summary)

        // Labels run E1.. in retrieval order; the near dialog chunk leads.
        Assert.Equal("E1", citations[0].GetProperty("label").GetString());
        Assert.Equal("E2", citations[1].GetProperty("label").GetString());
        Assert.Equal(nearLine, citations[0].GetProperty("quote").GetString());
        Assert.Equal("SessionTranscript", citations[0].GetProperty("documentType").GetString());
        Assert.Equal("alice-s1", citations[0].GetProperty("sessionId").GetString());
        Assert.NotEqual(Guid.Empty, citations[0].GetProperty("chunkId").GetGuid());
        Assert.False(string.IsNullOrEmpty(citations[0].GetProperty("documentId").GetString()));
        Assert.True(
            citations[0].GetProperty("score").GetDouble() > citations[1].GetProperty("score").GetDouble(),
            "citations should carry the retrieval score, near ahead of middling");
    }

    [Fact]
    public async Task Citations_are_scoped_to_the_route_patient()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "What do we know about this patient?";
        emb.Pin(question, Query);

        const string aliceLine = "Alice is being treated for a chronic condition affecting her daily routine now.";
        const string bobLine = "Bob came in about an unrelated acute problem that resolved within a few days.";
        await IngestTranscriptAsync(client, chat, emb, "scope-alice", "dr-a", "sa-s1",
            aliceLine, "A.", "Alice summary.", Near, Orthogonal);
        await IngestTranscriptAsync(client, chat, emb, "scope-bob", "dr-a", "sb-s1",
            bobLine, "B.", "Bob summary.", Query, Orthogonal); // Bob pinned closest of all

        chat.EnqueueResponse("Here is what the record shows [E1].");

        var response = await client.PostAsJsonAsync("/patients/scope-alice/chat/answer", new { doctorId = "dr-a", question });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var quotes = body.GetProperty("citations").EnumerateArray()
            .Select(c => c.GetProperty("quote").GetString()).ToList();

        Assert.Contains(quotes, q => q == aliceLine);
        Assert.DoesNotContain(quotes, q => q == bobLine);
    }

    [Fact]
    public async Task A_caller_with_no_secret_is_refused()
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ApiKeyAuthentication.HeaderName);

        var response = await client.PostAsJsonAsync(
            "/patients/anyone/chat/answer", new { doctorId = "dr", question = "anything?" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_blank_question_is_rejected_before_anything_runs()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/patients/anyone/chat/answer", new { doctorId = "dr", question = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task IngestTranscriptAsync(
        HttpClient client, ScriptedChatClient chat, ControllableEmbeddingGenerator emb,
        string patientId, string doctorId, string sessionId, string line, string blurb, string summary,
        float[] bodyVector, float[] summaryVector)
    {
        emb.Pin($"{blurb}\n\n{line}", bodyVector);
        emb.Pin(summary, summaryVector);
        chat.EnqueueResponse(
            $$"""
            { "chunks": [ { "startLine": 0, "endLine": 0, "contextBlurb": {{JsonSerializer.Serialize(blurb)}} } ],
              "summary": {{JsonSerializer.Serialize(summary)}} }
            """);
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId,
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
