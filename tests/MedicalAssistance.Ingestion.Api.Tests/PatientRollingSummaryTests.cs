using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// After every ingestion the service refreshes one rolling overview per patient,
/// folding the patient's per-document summaries into a single evolving summary. It
/// is created on the first document and regenerated — from the full current set —
/// on each one after, and it is derived rather than part of the ingestion, so it
/// never fails an ingestion.
/// </summary>
public class PatientRollingSummaryTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string TranscriptOne = """
        Doctor: What brings you in today?
        Patient: I keep waking up with headaches.
        Doctor: How long has that been going on?
        Patient: About three months now.
        """;

    private const string TranscriptTwo = """
        Doctor: How are the headaches since the new medication?
        Patient: Much better, only once last week.
        Doctor: Any side effects?
        Patient: A little drowsy in the mornings.
        """;

    private static string PlanWithSummary(string summary) => $$"""
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Opening." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Detail." }
          ],
          "summary": "{{summary}}"
        }
        """;

    [Fact]
    public async Task No_overview_exists_before_a_patient_has_any_documents()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync("/patients/roll-nobody/summary");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_overview_is_created_after_the_first_document_and_regenerated_after_the_next()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "roll-alice";

        // First document: its per-document summary is what the overview folds in.
        fixture.ChatClient.EnqueueResponse(PlanWithSummary("Headaches for three months."));
        fixture.ChatClient.EnqueueSummaryResponse("Overview after one visit: new headaches.");
        await IngestAsync(client, patientId, sequenceNumber: 1, TranscriptOne);

        var afterOne = await WaitForSummaryAsync(client, patientId, expectedDocumentCount: 1);
        Assert.Equal("Overview after one visit: new headaches.", afterOne.GetProperty("summary").GetString());

        // Second document: the overview regenerates from both documents.
        fixture.ChatClient.EnqueueResponse(PlanWithSummary("Headaches improving on medication."));
        fixture.ChatClient.EnqueueSummaryResponse("Overview after two visits: headaches improving.");
        await IngestAsync(client, patientId, sequenceNumber: 2, TranscriptTwo);

        var afterTwo = await WaitForSummaryAsync(client, patientId, expectedDocumentCount: 2);
        Assert.Equal("Overview after two visits: headaches improving.", afterTwo.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task The_overview_prompt_folds_in_the_per_document_summaries()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "roll-carol";
        const string perDocumentSummary = "Carol reports a persistent cough after a chest infection.";

        fixture.ChatClient.EnqueueResponse(PlanWithSummary(perDocumentSummary));
        await IngestAsync(client, patientId, sequenceNumber: 1, TranscriptOne);
        await WaitForSummaryAsync(client, patientId, expectedDocumentCount: 1);

        // The summariser was handed the document's own summary to fold in.
        Assert.Contains(fixture.ChatClient.ReceivedSummaryPrompts, prompt => prompt.Contains(perDocumentSummary));
    }

    private async Task IngestAsync(HttpClient client, string patientId, int sequenceNumber, string transcript)
    {
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "roll-doc",
            patientId,
            sessionId = $"sess-{patientId}",
            sequenceNumber,
            language = "en",
            transcript,
        });
        response.EnsureSuccessStatusCode();
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
        await WaitForStatusAsync(client, ingestionId, "Completed");
    }

    // The overview is regenerated after the ingestion reaches Completed, so poll the
    // endpoint until it reflects the expected number of documents.
    private static async Task<JsonElement> WaitForSummaryAsync(
        HttpClient client, string patientId, int expectedDocumentCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/patients/{patientId}/summary");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (body.GetProperty("documentCount").GetInt32() == expectedDocumentCount)
                    return body;
            }
            await Task.Delay(50);
        }

        Assert.Fail($"Patient {patientId} never reached a summary of {expectedDocumentCount} document(s).");
        throw new InvalidOperationException("unreachable");
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
