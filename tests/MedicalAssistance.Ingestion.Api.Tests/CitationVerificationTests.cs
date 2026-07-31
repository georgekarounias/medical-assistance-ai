using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Chat;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// Citation verification and fail-fast (T46). Every [E#] the answer cites must have
/// been supplied this turn; a fabricated reference fails the turn (5xx) with no
/// corrective retry and never emits the unverified text (ADR-0012). The citations
/// returned are reconciled to exactly those the answer references.
/// </summary>
public class CitationVerificationTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private static readonly float[] Query = [1f, 0f];
    private static readonly float[] Near = [0.98f, 0.02f];
    private static readonly float[] Middling = [0.6f, 0.8f];
    private static readonly float[] Orthogonal = [0f, 1f];

    private const string InsulinLine = "The patient reports taking insulin injections every single day for their diabetes.";
    private const string PressureLine = "Blood pressure this morning measured one twenty over eighty which is entirely normal.";

    [Fact]
    public async Task An_answer_citing_an_unsupplied_label_fails_the_turn_without_emitting_it_and_never_retries()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "Does the patient take insulin?";
        emb.Pin(question, Query);
        // One document → two evidence items, E1 (body) and E2 (summary).
        await IngestTranscriptAsync(client, chat, emb, "verify-bad", "vb-s1", InsulinLine, "Diabetes.", "Summary.", Near);

        const string fabricated = "The patient is on insulin [E1] and also on statins [E3].";
        chat.EnqueueResponse(fabricated);

        var response = await client.PostAsJsonAsync(
            "/patients/verify-bad/chat/answer", new { doctorId = "dr-a", question });

        // Fail-fast: a 5xx, and the unverified answer is nowhere in the response.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(fabricated, content);
        Assert.DoesNotContain("statins", content);

        // No corrective retry — the model was called exactly once (unlike the chunker).
        Assert.Equal(1, chat.ReceivedPrompts.Count(p => p.Contains("Evidence Items:")));
    }

    [Fact]
    public async Task Citations_are_reconciled_to_the_labels_the_answer_actually_cites()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "Summarise the record.";
        emb.Pin(question, Query);
        // Two documents → E1 (near body), E2 (middling body), E3/E4 (orthogonal summaries).
        await IngestTranscriptAsync(client, chat, emb, "verify-recon", "vr-s1", InsulinLine, "Diabetes.", "Summary one.", Near);
        await IngestTranscriptAsync(client, chat, emb, "verify-recon", "vr-s2", PressureLine, "Vitals.", "Summary two.", Middling);

        // The answer cites only E1 and E3.
        chat.EnqueueResponse("Insulin daily [E1]; see also the earlier note [E3].");

        var response = await client.PostAsJsonAsync(
            "/patients/verify-recon/chat/answer", new { doctorId = "dr-a", question, topK = 8 });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("refused").GetBoolean());

        var labels = body.GetProperty("citations").EnumerateArray()
            .Select(c => c.GetProperty("label").GetString()).ToList();
        // Only the cited labels survive — the uncited E2 and E4 are gone.
        Assert.Equal(["E1", "E3"], labels);
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

/// <summary>
/// Direct unit tests of the verifier's contract — the parsing and reconciliation
/// rules, without a database or the pipeline around them.
/// </summary>
public class CitationVerificationUnitTests
{
    private static ChatCitation Citation(string label) => new()
    {
        Label = label,
        ChunkId = Guid.NewGuid(),
        DocumentId = "doc",
        DocumentType = "SessionTranscript",
        Quote = $"quote for {label}",
        Score = 0.9,
    };

    [Fact]
    public void It_returns_only_the_cited_supplied_citations_in_supplied_order()
    {
        var supplied = new[] { Citation("E1"), Citation("E2"), Citation("E3") };

        var verified = CitationVerification.Verify("Point one [E3] and point two [E1].", supplied);

        Assert.Equal(["E1", "E3"], verified.Select(c => c.Label));
    }

    [Fact]
    public void It_throws_when_the_answer_cites_a_label_that_was_not_supplied()
    {
        var supplied = new[] { Citation("E1"), Citation("E2") };

        var ex = Assert.Throws<CitationVerificationException>(
            () => CitationVerification.Verify("Grounded [E1], invented [E5].", supplied));
        Assert.Contains("E5", ex.UnsuppliedLabels);
    }

    [Fact]
    public void An_answer_that_cites_nothing_verifies_with_no_citations()
    {
        var supplied = new[] { Citation("E1") };

        var verified = CitationVerification.Verify("A plain answer with no labels.", supplied);

        Assert.Empty(verified);
    }
}
