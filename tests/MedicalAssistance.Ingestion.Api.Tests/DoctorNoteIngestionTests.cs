using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The DoctorNote strategy: a typed clinical note runs through the same shared
/// prose pipeline as a transcript, but as monologue — its chunks are stamped
/// <c>note</c> — and its identity is the backend-assigned <c>noteId</c>, so a
/// re-POST of the same noteId is a Correction. Because a note's identity does not
/// depend on a session, two session-less notes must never be mistaken for one
/// document (bug B09).
/// </summary>
public class DoctorNoteIngestionTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string NoteText = """
        Patient reports a persistent dry cough.
        No fever or shortness of breath today.
        Chest is clear on auscultation today.
        Plan: antihistamine, review in two weeks.
        """;

    private const string TwoChunkPlan = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Cough history and negatives." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Exam findings and plan." }
          ],
          "summary": "Note on a three-week dry cough; exam clear; antihistamine trial planned."
        }
        """;

    [Fact]
    public async Task A_valid_note_reaches_completed_with_verbatim_note_chunks_and_a_summary_chunk()
    {
        var client = fixture.Factory.CreateClient();

        var ingestionId = await IngestAsync(client, patientId: "pat-note-1", noteId: "note-1", NoteText);

        var chunks = await ReadChunksAsync(ingestionId);
        Assert.Equal(3, chunks.Count);

        var noteChunks = chunks.Where(c => c.Kind == "note").ToList();
        Assert.Equal(2, noteChunks.Count);
        Assert.Equal(
            "Patient reports a persistent dry cough.\nNo fever or shortness of breath today.",
            noteChunks[0].VerbatimText);
        Assert.Equal("Cough history and negatives.", noteChunks[0].ContextBlurb);

        // A note gets a summary chunk like any prose document, and it is 'summary',
        // never 'note' — the summary is generated, not the doctor's own words.
        var summary = Assert.Single(chunks, c => c.Kind == "summary");
        Assert.Equal(
            "Note on a three-week dry cough; exam clear; antihistamine trial planned.",
            summary.VerbatimText);
    }

    [Fact]
    public async Task Re_posting_a_note_id_with_different_text_supersedes_the_previous_version()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-note-corrected";
        const string noteId = "note-correctable";

        var originalId = await IngestAsync(client, patientId, noteId, NoteText);

        // A real correction re-chunks, so its summary reflects the new text too.
        var correctedText = NoteText.Replace("dry cough", "productive cough");
        var correctedPlan = TwoChunkPlan.Replace("three-week dry cough", "productive cough");
        var corrected = await PostAsync(client, patientId, noteId, correctedText, plan: correctedPlan);
        Assert.False(corrected.Duplicate);
        await WaitForStatusAsync(client, corrected.Id, "Completed");

        // Editing a note is a Correction keyed on noteId: the document holds the
        // new text, the old ingestion is Superseded, and its chunks are gone.
        var live = await ReadDocumentTextsAsync(patientId, noteId);
        Assert.Contains(live, text => text.Contains("productive cough"));
        Assert.DoesNotContain(live, text => text.Contains("dry cough"));
        Assert.Equal("Superseded", await ReadStatusAsync(client, originalId));
        Assert.Equal(0, await CountChunksOfAsync(originalId));
    }

    [Fact]
    public async Task Two_session_less_notes_for_a_patient_are_kept_as_separate_documents()
    {
        // The B09 proof: neither note carries a session id. If identity were matched
        // on the raw (session, sequence) columns, EF's both-null equality would make
        // these look like the same document and the second would supersede the first.
        // They are distinct because their noteIds are, so both survive.
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-two-notes";

        var firstId = await IngestAsync(client, patientId, noteId: "note-a", NoteText, sessionId: null);
        var secondId = await IngestAsync(
            client, patientId, noteId: "note-b", NoteText.Replace("dry cough", "wet cough"), sessionId: null);

        // Both ingestions are still Completed — neither superseded the other.
        Assert.Equal("Completed", await ReadStatusAsync(client, firstId));
        Assert.Equal("Completed", await ReadStatusAsync(client, secondId));

        // And the patient's record shows two distinct note documents.
        var documents = await client.GetFromJsonAsync<List<PatientDocumentRow>>(
            $"/patients/{patientId}/documents");
        var notes = documents!.Where(d => d.DocumentType == "DoctorNote").ToList();
        Assert.Equal(2, notes.Count);
        Assert.Equal(
            [$"doc-1#{patientId}#note-a", $"doc-1#{patientId}#note-b"],
            notes.Select(n => n.DocumentId).Order().ToArray());
    }

    [Fact]
    public async Task A_note_missing_its_identity_or_body_is_rejected_field_by_field()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "DoctorNote",
            doctorId = "doc-1",
            patientId = "pat-note-invalid",
            // no noteId, no text
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("noteId", out _), $"Expected a noteId error in: {errors}");
        Assert.True(errors.TryGetProperty("text", out _), $"Expected a text error in: {errors}");
        // A note is not asked for a transcript's fields.
        Assert.False(errors.TryGetProperty("sessionId", out _), $"Unexpected sessionId error in: {errors}");
        Assert.False(errors.TryGetProperty("transcript", out _), $"Unexpected transcript error in: {errors}");
    }

    private async Task<Guid> IngestAsync(
        HttpClient client, string patientId, string noteId, string text, string? sessionId = null)
    {
        var accepted = await PostAsync(client, patientId, noteId, text, sessionId);
        await WaitForStatusAsync(client, accepted.Id, "Completed");
        return accepted.Id;
    }

    private async Task<(Guid Id, bool Duplicate)> PostAsync(
        HttpClient client, string patientId, string noteId, string text, string? sessionId = null, string? plan = null)
    {
        fixture.ChatClient.EnqueueResponse(plan ?? TwoChunkPlan);
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "DoctorNote",
            doctorId = "doc-1",
            patientId,
            noteId,
            sessionId,
            language = "en",
            text,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("ingestionId").GetGuid(), body.GetProperty("duplicate").GetBoolean());
    }

    private static async Task<string> ReadStatusAsync(HttpClient client, Guid ingestionId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}")).GetProperty("status").GetString()!;

    private static async Task WaitForStatusAsync(HttpClient client, Guid ingestionId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var lastSeen = "<never fetched>";
        while (DateTime.UtcNow < deadline)
        {
            lastSeen = await ReadStatusAsync(client, ingestionId);
            if (lastSeen == expected)
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Ingestion {ingestionId} never reached {expected}. Last: {lastSeen}");
    }

    private async Task<List<(string Kind, string VerbatimText, string? ContextBlurb)>> ReadChunksAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT chunk_kind, verbatim_text, context_blurb FROM chunks WHERE ingestion_id = $1 ORDER BY chunk_index",
            connection);
        command.Parameters.AddWithValue(ingestionId);

        var chunks = new List<(string, string, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            chunks.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return chunks;
    }

    private async Task<List<string>> ReadDocumentTextsAsync(string patientId, string noteId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT verbatim_text FROM chunks WHERE document_id = $1 ORDER BY chunk_index", connection);
        command.Parameters.AddWithValue($"doc-1#{patientId}#{noteId}");

        var texts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            texts.Add(reader.GetString(0));
        return texts;
    }

    private async Task<long> CountChunksOfAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM chunks WHERE ingestion_id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record PatientDocumentRow(string DocumentId, string DocumentType, string Status);
}
