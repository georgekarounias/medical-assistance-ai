using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The per-ingestion quality report (T35): written in the same transaction as the
/// chunks and read at <c>GET /ingestions/{id}/quality</c>. It is the measured
/// record a golden set (T36) baselines against, so these prove the numbers a
/// regression would move — chunk count and token distribution, the guardrail merge
/// and split counts, and whether the corrective chunking retry fired — are the ones
/// actually persisted.
/// </summary>
public class IngestionQualityReportTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string Transcript = """
        Doctor: What brings you in today?
        Patient: I keep waking up with headaches.
        Doctor: How long has that been going on?
        Patient: About three months now.
        """;

    // Two in-band chunks plus a summary: nothing for the guardrails to repair and
    // no retry, so this is the clean baseline every field is read against.
    private const string CleanPlan = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Opening of the visit." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Duration of the symptom." }
          ],
          "summary": "The patient reports waking with headaches for about three months."
        }
        """;

    // Lines 0-2 together exceed the ceiling; line 3 alone is below the floor — so
    // code must split the first and merge the fragment, exactly the two repairs the
    // report counts. Mirrors ChunkSizeGuardrailTests against the fixture's small band.
    private const string GuardrailTranscript = """
        Doctor: Let's go through your history in detail before we decide on a treatment plan.
        Patient: I have had these headaches for about three months, mostly in the early morning.
        Doctor: Do they improve after you drink water or eat something once you are awake?
        Patient: Yes.
        Doctor: That pattern points to dehydration rather than anything structural.
        """;

    private const string GuardrailPlan = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 2, "contextBlurb": "History taking around the headaches." },
            { "startLine": 3, "endLine": 3, "contextBlurb": "Confirms the pattern." },
            { "startLine": 4, "endLine": 4, "contextBlurb": "Doctor's interpretation." }
          ],
          "summary": "Morning headaches attributed to dehydration."
        }
        """;

    [Fact]
    public async Task A_completed_prose_ingestion_persists_and_exposes_its_quality_report()
    {
        var client = fixture.Factory.CreateClient();

        fixture.ChatClient.EnqueueResponse(CleanPlan);
        var ingestionId = await SubmitAsync(client, "quality-clean", "sess-clean", Transcript);
        await WaitForStatusAsync(client, ingestionId, "Completed");

        var report = await GetQualityAsync(client, ingestionId);

        // Two dialog chunks plus the summary chunk, and a token count for each — the
        // distribution covers exactly the chunks that were stored.
        Assert.Equal(3, report.GetProperty("chunkCount").GetInt32());
        Assert.Equal(3, report.GetProperty("tokenCounts").GetArrayLength());

        // A clean plan needed no repair and no retry.
        Assert.Equal(0, report.GetProperty("guardrailMerges").GetInt32());
        Assert.Equal(0, report.GetProperty("guardrailSplits").GetInt32());
        Assert.False(report.GetProperty("correctiveRetryFired").GetBoolean());

        // The summary statistics agree with the distribution they summarise.
        var tokens = report.GetProperty("tokenCounts").EnumerateArray().Select(t => t.GetInt32()).ToList();
        Assert.Equal(tokens.Sum(), report.GetProperty("totalTokens").GetInt32());
        Assert.Equal(tokens.Min(), report.GetProperty("minTokens").GetInt32());
        Assert.Equal(tokens.Max(), report.GetProperty("maxTokens").GetInt32());
        Assert.Equal(tokens.Sum() / tokens.Count, report.GetProperty("meanTokens").GetInt32());
    }

    [Fact]
    public async Task The_report_records_that_the_corrective_chunking_retry_fired()
    {
        var client = fixture.Factory.CreateClient();

        // The first plan is unparseable, so the pipeline rejects it and issues the one
        // corrective retry; the second is valid and the ingestion completes.
        fixture.ChatClient.EnqueueResponse("this is not a chunk plan at all");
        fixture.ChatClient.EnqueueResponse(CleanPlan);
        var ingestionId = await SubmitAsync(client, "quality-retry", "sess-retry", Transcript);
        await WaitForStatusAsync(client, ingestionId, "Completed");

        var report = await GetQualityAsync(client, ingestionId);
        Assert.True(report.GetProperty("correctiveRetryFired").GetBoolean());
    }

    [Fact]
    public async Task The_report_counts_the_guardrail_merges_and_splits()
    {
        var client = fixture.Factory.CreateClient();

        fixture.ChatClient.EnqueueResponse(GuardrailPlan);
        var ingestionId = await SubmitAsync(client, "quality-guardrails", "sess-guardrails", GuardrailTranscript);
        await WaitForStatusAsync(client, ingestionId, "Completed");

        var report = await GetQualityAsync(client, ingestionId);

        // The undersized fragment was merged and the oversized chunk was split, so
        // both repairs are recorded — the signals a golden-set regression would move.
        Assert.True(report.GetProperty("guardrailMerges").GetInt32() > 0, "Expected at least one merge.");
        Assert.True(report.GetProperty("guardrailSplits").GetInt32() > 0, "Expected at least one split.");
    }

    [Fact]
    public async Task Quality_report_is_not_found_for_an_unknown_ingestion()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync($"/ingestions/{Guid.NewGuid()}/quality");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string patientId, string sessionId, string transcript)
    {
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "quality-doc",
            patientId,
            sessionId,
            sequenceNumber = 1,
            language = "en",
            transcript,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
    }

    private static async Task<JsonElement> GetQualityAsync(HttpClient client, Guid ingestionId)
    {
        var response = await client.GetAsync($"/ingestions/{ingestionId}/quality");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task WaitForStatusAsync(HttpClient client, Guid ingestionId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var lastSeen = "<never fetched>";
        while (DateTime.UtcNow < deadline)
        {
            lastSeen = (await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}"))
                .GetProperty("status").GetString()!;
            if (lastSeen == expected)
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Ingestion {ingestionId} never reached {expected}. Last: {lastSeen}");
    }
}
