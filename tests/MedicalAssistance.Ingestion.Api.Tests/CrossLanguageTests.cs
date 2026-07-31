using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Retrieval;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// Cross-language retrieval and answer language (T47). A bilingual clinician and a
/// mixed Greek/English record are first-class: a question in one language retrieves
/// relevant chunks written in another (multilingual embeddings), the answer is
/// written in the question's language, and each citation quotes its evidence in
/// whatever language it is in. <c>language</c> stays an optional filter, never a
/// barrier.
///
/// The controllable embedding fake stands in for a multilingual model by pinning a
/// question and a semantically-equivalent chunk in another language to the same
/// vector — which is exactly what such a model does.
/// </summary>
public class CrossLanguageTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private static readonly float[] Relevant = [1f, 0f];
    private static readonly float[] Summary = [0f, 1f];

    private const string EnglishLine = "The patient reports taking insulin injections every single day for their diabetes.";
    private const string GreekLine = "Ο ασθενής λαμβάνει ινσουλίνη καθημερινά εδώ και αρκετά χρόνια για τον διαβήτη.";

    [Fact]
    public async Task A_greek_question_retrieves_an_english_chunk_and_the_answer_is_greek()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string greekQuestion = "Παίρνει ο ασθενής ινσουλίνη για τον διαβήτη του;";
        emb.Pin(greekQuestion, Relevant);
        // The record is in English; the Greek question lands on it via the shared vector.
        await IngestTranscriptAsync(client, chat, emb, "xl-en-record", "en", "en-s1", EnglishLine, "Diabetes.", "Summary.");

        chat.EnqueueResponse("Ναι, ο ασθενής λαμβάνει ινσουλίνη καθημερινά [E1].");
        var response = await client.PostAsJsonAsync(
            "/patients/xl-en-record/chat/answer", new { doctorId = "dr-a", question = greekQuestion });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("refused").GetBoolean());
        Assert.Equal("el", body.GetProperty("language").GetString());
        // The English chunk was retrieved by a Greek question and cited in its own language.
        var citations = body.GetProperty("citations").EnumerateArray().ToList();
        Assert.Equal(EnglishLine, citations.Single().GetProperty("quote").GetString());
    }

    [Fact]
    public async Task An_english_question_retrieves_a_greek_chunk_and_the_answer_is_english()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string englishQuestion = "Does the patient take insulin for their diabetes?";
        emb.Pin(englishQuestion, Relevant);
        await IngestTranscriptAsync(client, chat, emb, "xl-el-record", "el", "el-s1", GreekLine, "Διαβήτης.", "Περίληψη.");

        chat.EnqueueResponse("Yes, the patient takes insulin daily [E1].");
        var response = await client.PostAsJsonAsync(
            "/patients/xl-el-record/chat/answer", new { doctorId = "dr-a", question = englishQuestion });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("refused").GetBoolean());
        Assert.Equal("en", body.GetProperty("language").GetString());
        var citations = body.GetProperty("citations").EnumerateArray().ToList();
        Assert.Equal(GreekLine, citations.Single().GetProperty("quote").GetString());
    }

    [Fact]
    public async Task Language_is_an_optional_filter_not_a_default_barrier()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "What is the patient's insulin regimen?";
        emb.Pin(question, Relevant);
        // Same patient, one English document and one Greek document, both relevant.
        await IngestTranscriptAsync(client, chat, emb, "xl-mixed", "en", "mix-en", EnglishLine, "Diabetes.", "Summary.");
        await IngestTranscriptAsync(client, chat, emb, "xl-mixed", "el", "mix-el", GreekLine, "Διαβήτης.", "Περίληψη.");

        // No language filter: the whole record, both languages.
        var whole = await SearchAsync(factory, new RetrievalRequest { PatientId = "xl-mixed", Question = question, TopK = 8 });
        Assert.Contains(whole, e => e.VerbatimText == EnglishLine);
        Assert.Contains(whole, e => e.VerbatimText == GreekLine);

        // language:el narrows to the Greek document — proof it can filter, and that
        // its absence above was the reason both languages came back.
        var greekOnly = await SearchAsync(factory, new RetrievalRequest
        {
            PatientId = "xl-mixed", Question = question, TopK = 8,
            Filters = new RetrievalFilters { Language = "el" },
        });
        Assert.Contains(greekOnly, e => e.VerbatimText == GreekLine);
        Assert.DoesNotContain(greekOnly, e => e.VerbatimText == EnglishLine);
    }

    private static async Task<IReadOnlyList<EvidenceItem>> SearchAsync(
        WebApplicationFactory<Program> factory, RetrievalRequest request)
    {
        using var scope = factory.Services.CreateScope();
        var retrieval = scope.ServiceProvider.GetRequiredService<IRetrievalService>();
        return (await retrieval.SearchAsync(request)).Evidence;
    }

    private async Task IngestTranscriptAsync(
        HttpClient client, ScriptedChatClient chat, ControllableEmbeddingGenerator emb,
        string patientId, string language, string sessionId, string line, string blurb, string summary)
    {
        emb.Pin($"{blurb}\n\n{line}", Relevant);
        emb.Pin(summary, Summary);
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
            language,
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
