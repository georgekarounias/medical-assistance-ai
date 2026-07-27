using System.Net.Http.Json;
using System.Text.Json;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The per-document summary the prose pipeline produces is not only embedded as a
/// chunk — it is stored on the ingestion and handed back directly, so a caller can
/// read what a document is about from its status or the patient document list
/// without running a vector search.
/// </summary>
public class DocumentSummaryTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string Transcript = """
        Doctor: What brings you in today?
        Patient: I keep waking up with headaches.
        Doctor: How long has that been going on?
        Patient: About three months now.
        """;

    private const string SummaryText = "The patient reports waking with headaches for about three months.";

    private static readonly string PlanWithSummary = $$"""
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Opening of the visit." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Duration of the symptom." }
          ],
          "summary": "{{SummaryText}}"
        }
        """;

    [Fact]
    public async Task A_completed_prose_ingestion_exposes_its_summary_on_status_and_the_patient_list()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "sum-alice";

        fixture.ChatClient.EnqueueResponse(PlanWithSummary);
        var ingestionId = await SubmitAsync(client, patientId);
        await WaitForStatusAsync(client, ingestionId, "Completed");

        // Directly on the ingestion status — no vector search needed.
        var status = await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");
        Assert.Equal(SummaryText, status.GetProperty("summary").GetString());

        // And on the patient document list, beside the document it describes.
        var documents = await client.GetFromJsonAsync<JsonElement>($"/patients/{patientId}/documents");
        var document = documents.EnumerateArray().Single();
        Assert.Equal(SummaryText, document.GetProperty("summary").GetString());
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string patientId)
    {
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "sum-doc",
            patientId,
            sessionId = $"sess-{patientId}",
            sequenceNumber = 1,
            language = "en",
            transcript = Transcript,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
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
