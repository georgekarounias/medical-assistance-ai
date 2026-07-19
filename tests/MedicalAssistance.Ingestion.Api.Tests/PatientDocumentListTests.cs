using System.Net.Http.Json;
using System.Text.Json;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// What the system knows about one patient, one row per document. This is the
/// doctor's view of the record — and the place they start from when something
/// is in it that should not be, so it has to show the truth including uploads
/// that failed.
/// </summary>
public class PatientDocumentListTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string PartOne = """
        Doctor: Good morning, what brings you in today?
        Patient: I keep waking up with headaches.
        Doctor: How long has that been going on?
        Patient: About three months now.
        """;

    private const string PartTwo = """
        Doctor: Sorry, the recorder stopped. Let's carry on.
        Patient: You were asking about my water intake.
        Doctor: Right. How much do you drink on a normal day?
        Patient: Maybe one glass, sometimes less.
        """;

    private const string TwoChunkPlan = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Opening." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Detail." }
          ],
          "summary": "Session summary."
        }
        """;

    [Fact]
    public async Task Each_document_of_a_patient_is_listed_with_what_the_doctor_needs_to_recognise_it()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-listed";

        await IngestAsync(client, patientId, sequenceNumber: 1, PartOne, sessionDate: "2026-07-10T14:30:00Z");
        await IngestAsync(client, patientId, sequenceNumber: 2, PartTwo, sessionDate: "2026-07-10T15:05:00Z");
        await IngestAsync(client, "pat-someone-else", sequenceNumber: 1, PartOne, sessionDate: "2026-07-11T09:00:00Z");

        var documents = await ListAsync(client, patientId);

        Assert.Equal(2, documents.Count);
        Assert.Equal(
            [$"doc-1#{patientId}#sess-{patientId}#1", $"doc-1#{patientId}#sess-{patientId}#2"],
            documents.Select(d => d.DocumentId).Order().ToArray());

        var first = documents.Single(d => d.SequenceNumber == 1);
        Assert.Equal("SessionTranscript", first.DocumentType);
        Assert.Equal($"sess-{patientId}", first.SessionId);
        Assert.Equal("Completed", first.Status);

        // The clinical date, not the upload time — this is how a doctor finds
        // the session they are thinking of.
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T14:30:00Z"), first.DocumentDate);
    }

    [Fact]
    public async Task A_corrected_document_appears_once_in_its_latest_state()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-listed-correction";

        await IngestAsync(client, patientId, sequenceNumber: 1, PartOne, sessionDate: "2026-07-12T10:00:00Z");
        var correctionId = await IngestAsync(
            client, patientId, sequenceNumber: 1, PartTwo, sessionDate: "2026-07-12T10:00:00Z");

        var documents = await ListAsync(client, patientId);

        // One document, not one row per attempt at it: the superseded version is
        // history, and showing it would suggest the patient has two transcripts.
        var document = Assert.Single(documents);
        Assert.Equal($"doc-1#{patientId}#sess-{patientId}#1", document.DocumentId);
        Assert.Equal("Completed", document.Status);
        Assert.Equal(correctionId, document.IngestionId);
    }

    [Fact]
    public async Task An_upload_that_failed_is_still_listed_so_the_doctor_knows_it_is_missing()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-listed-failure";

        // No scripted response, so this one fails.
        var failedId = await PostAsync(client, patientId, sequenceNumber: 1, PartOne, "2026-07-13T08:00:00Z");
        await WaitForStatusAsync(client, failedId, "Failed");

        var documents = await ListAsync(client, patientId);

        // Silence here would be the worst outcome: the doctor would believe the
        // session was recorded and later ask questions about nothing.
        var document = Assert.Single(documents);
        Assert.Equal("Failed", document.Status);
        Assert.False(string.IsNullOrWhiteSpace(document.ErrorMessage));
    }

    [Fact]
    public async Task Two_patients_filed_under_the_same_session_get_different_document_ids()
    {
        var client = fixture.Factory.CreateClient();
        const string sharedSession = "sess-shared-across-patients";

        // The same session id and sequence number for two different patients.
        // Harmless if session ids turn out to be globally unique, and a mix-up
        // of two people's records if they turn out to be unique only within a
        // patient — which nobody has confirmed.
        await IngestAsync(client, "pat-collides-a", 1, PartOne, "2026-07-14T09:00:00Z", sharedSession);
        await IngestAsync(client, "pat-collides-b", 1, PartTwo, "2026-07-14T09:00:00Z", sharedSession);

        var first = Assert.Single(await ListAsync(client, "pat-collides-a"));
        var second = Assert.Single(await ListAsync(client, "pat-collides-b"));

        // The identifier answers "whose document is this?" by itself. That is
        // what lets un-ingest take a document id alone and still be safe.
        Assert.NotEqual(first.DocumentId, second.DocumentId);
        Assert.StartsWith("doc-1#pat-collides-a#", first.DocumentId);
        Assert.StartsWith("doc-1#pat-collides-b#", second.DocumentId);
    }

    [Fact]
    public async Task Two_doctors_filing_the_same_session_hold_two_separate_documents()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-two-doctors";
        const string sharedSession = "sess-two-doctors";

        // Same patient, same session, same sequence number — filed by two
        // different doctors. The doctor is part of the document key, so these
        // are two documents rather than one correcting the other.
        await IngestAsync(client, patientId, 1, PartOne, "2026-07-15T09:00:00Z", sharedSession, doctorId: "doc-a");
        await IngestAsync(client, patientId, 1, PartTwo, "2026-07-15T09:00:00Z", sharedSession, doctorId: "doc-b");

        var documents = await ListAsync(client, patientId);

        Assert.Equal(2, documents.Count);
        Assert.Equal(
            [$"doc-a#{patientId}#{sharedSession}#1", $"doc-b#{patientId}#{sharedSession}#1"],
            documents.Select(d => d.DocumentId).Order().ToArray());

        // Both survive: neither superseded the other, so the patient's record
        // holds both doctors' accounts of the encounter.
        Assert.All(documents, document => Assert.Equal("Completed", document.Status));
    }

    [Fact]
    public async Task A_patient_the_system_knows_nothing_about_has_an_empty_list()
    {
        var client = fixture.Factory.CreateClient();

        var documents = await ListAsync(client, "pat-never-seen");

        Assert.Empty(documents);
    }

    private static async Task<List<PatientDocumentDto>> ListAsync(HttpClient client, string patientId)
    {
        var response = await client.GetAsync($"/patients/{patientId}/documents");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<PatientDocumentDto>>())!;
    }

    private async Task<Guid> IngestAsync(
        HttpClient client, string patientId, int sequenceNumber, string transcript, string sessionDate,
        string? sessionId = null, string doctorId = "doc-1")
    {
        fixture.ChatClient.EnqueueResponse(TwoChunkPlan);
        var ingestionId = await PostAsync(
            client, patientId, sequenceNumber, transcript, sessionDate, sessionId, doctorId);
        await WaitForStatusAsync(client, ingestionId, "Completed");
        return ingestionId;
    }

    private static async Task<Guid> PostAsync(
        HttpClient client, string patientId, int sequenceNumber, string transcript, string sessionDate,
        string? sessionId = null, string doctorId = "doc-1")
    {
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId,
            patientId,
            sessionId = sessionId ?? $"sess-{patientId}",
            sequenceNumber,
            sessionDate,
            language = "en",
            transcript,
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
            var status = await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");
            lastSeen = status.GetRawText();
            if (status.GetProperty("status").GetString() == expected)
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Ingestion {ingestionId} never reached {expected}. Last: {lastSeen}");
    }

    private sealed record PatientDocumentDto(
        string DocumentId,
        string DocumentType,
        string? SessionId,
        int? SequenceNumber,
        DateTimeOffset? DocumentDate,
        string Status,
        string? ErrorMessage,
        Guid IngestionId);
}
