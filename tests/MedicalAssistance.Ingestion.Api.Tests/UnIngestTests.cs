using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// Un-ingest: DELETE /documents/{documentId} takes a document out of the record.
/// The canonical case is a wrong-patient upload — so removal has to be complete
/// (chunks and the raw payload both gone) and accountable (the ingestion row
/// stays as a Deleted tombstone naming who removed it and when). Silent
/// disappearance would be worse than the mistake it corrects.
/// </summary>
public class UnIngestTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string Transcript = """
        Doctor: Good morning, what brings you in today?
        Patient: I keep waking up with headaches.
        Doctor: How much water do you usually drink?
        Patient: Maybe one glass a day.
        """;

    // A sibling in the same session, deliberately different content — identical
    // content across sequence numbers deduplicates by design (the hash excludes
    // sessionId/sequenceNumber), so a genuine continuation has to say something
    // new to be a second document at all.
    private const string SiblingTranscript = """
        Doctor: The session dropped earlier, let's pick it back up.
        Patient: My headaches are worse in the afternoon lately.
        Doctor: Any change in how much you're drinking?
        Patient: About the same, one glass.
        """;

    private const string TwoChunkPlan = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Complaint discussed." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Hydration advice." }
          ],
          "summary": "Session summary."
        }
        """;

    [Fact]
    public async Task Un_ingesting_a_document_removes_its_chunks_and_its_payload_and_leaves_a_tombstone()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-uningest";
        var ingestionId = await IngestAsync(client, patientId, sequenceNumber: 1, Transcript);
        var documentId = await DocumentIdOfAsync(client, patientId, ingestionId);

        var response = await client.DeleteAsync(DeleteUri(documentId, removedBy: "doc-remover"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The clinical content is gone: no chunks, and the raw transcript no
        // longer sits in the payload where a wrong-patient upload would have
        // left it.
        Assert.Equal(0, await CountChunksAsync(ingestionId));
        Assert.Null(await ReadPayloadAsync(ingestionId));

        // But the fact of the document, and of its removal, is not gone. The row
        // survives as a Deleted tombstone naming who removed it and when.
        var (status, deletedBy, deletedAt) = await ReadTombstoneAsync(ingestionId);
        Assert.Equal("Deleted", status);
        Assert.Equal("doc-remover", deletedBy);
        Assert.NotNull(deletedAt);

        // And it is no longer part of what the record shows about the patient: a
        // removed document is not a document the doctor should still see listed.
        var documents = await client.GetFromJsonAsync<JsonElement>($"/patients/{patientId}/documents");
        Assert.Empty(documents.EnumerateArray());
    }

    [Fact]
    public async Task Un_ingesting_a_document_leaves_a_sibling_in_the_same_session_untouched()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-uningest-siblings";
        var firstId = await IngestAsync(client, patientId, sequenceNumber: 1, Transcript);
        var secondId = await IngestAsync(client, patientId, sequenceNumber: 2, SiblingTranscript);
        var firstDocumentId = await DocumentIdOfAsync(client, patientId, firstId);

        var response = await client.DeleteAsync(DeleteUri(firstDocumentId, removedBy: "doc-remover"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The metadata spine is shared across a session's transcripts, so a
        // delete that filtered too broadly would take the sibling with it.
        Assert.Equal(0, await CountChunksAsync(firstId));
        Assert.Equal(3, await CountChunksAsync(secondId));

        var documents = await client.GetFromJsonAsync<JsonElement>($"/patients/{patientId}/documents");
        var listed = documents.EnumerateArray().ToList();
        Assert.Single(listed);
        Assert.Equal(2, listed[0].GetProperty("sequenceNumber").GetInt32());
    }

    [Fact]
    public async Task Un_ingesting_a_document_that_is_still_processing_is_refused()
    {
        var patientId = "pat-uningest-inflight";

        // Hold the ingestion inside the chat call so it stays Processing while the
        // delete is attempted.
        var blockingChat = new ScriptedChatClient();
        var release = blockingChat.EnqueueBlockingResponse(TwoChunkPlan);
        await using var busy = fixture.CreateFactory(blockingChat);

        var client = busy.CreateClient();
        var submit = await client.PostAsJsonAsync("/ingestions", Payload(patientId, sequenceNumber: 1));
        var ingestionId = (await submit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
        await WaitForStatusAsync(client, ingestionId, "Processing");

        // A document mid-ingest cannot be removed: its chunk set does not exist
        // yet, and the run that is writing it must reach a terminal state first.
        var documentId = DocumentIdOf(patientId, sequenceNumber: 1);
        var response = await client.DeleteAsync(DeleteUri(documentId, removedBy: "doc-remover"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The run is untouched by the refused delete and still finishes normally.
        release();
        await WaitForStatusAsync(client, ingestionId, "Completed");
        Assert.Equal(3, await CountChunksAsync(ingestionId));
    }

    [Fact]
    public async Task Un_ingesting_an_unknown_document_is_not_found()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.DeleteAsync(
            DeleteUri("doc-nobody#pat-nobody#sess-nobody#1", removedBy: "doc-remover"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Un_ingesting_without_saying_who_is_removing_it_is_rejected()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-uningest-anon";
        var ingestionId = await IngestAsync(client, patientId, sequenceNumber: 1, Transcript);
        var documentId = await DocumentIdOfAsync(client, patientId, ingestionId);

        // The tombstone has to name someone; a removal with no actor cannot be
        // accountable, so it is refused before anything is deleted.
        var response = await client.DeleteAsync($"/documents/{Uri.EscapeDataString(documentId)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Nothing was removed.
        Assert.Equal(3, await CountChunksAsync(ingestionId));
        Assert.Equal("Completed", (await ReadTombstoneAsync(ingestionId)).Status);
    }

    [Fact]
    public async Task Un_ingesting_a_document_that_is_already_removed_is_not_found()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-uningest-twice";
        var ingestionId = await IngestAsync(client, patientId, sequenceNumber: 1, Transcript);
        var documentId = await DocumentIdOfAsync(client, patientId, ingestionId);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(DeleteUri(documentId, "doc-remover"))).StatusCode);

        // Removing it again is not a fresh removal — there is no live document to
        // take out — so it reads as not found rather than succeeding twice.
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(DeleteUri(documentId, "doc-remover"))).StatusCode);
    }

    private static string DeleteUri(string documentId, string removedBy) =>
        $"/documents/{Uri.EscapeDataString(documentId)}?removedBy={Uri.EscapeDataString(removedBy)}";

    private static string DocumentIdOf(string patientId, int sequenceNumber) =>
        $"doc-1#{patientId}#sess-{patientId}#{sequenceNumber}";

    private async Task<Guid> IngestAsync(HttpClient client, string patientId, int sequenceNumber, string transcript)
    {
        fixture.ChatClient.EnqueueResponse(TwoChunkPlan);
        var response = await client.PostAsJsonAsync("/ingestions", Payload(patientId, sequenceNumber, transcript));
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
        await WaitForStatusAsync(client, ingestionId, "Completed");
        return ingestionId;
    }

    private static object Payload(string patientId, int sequenceNumber, string transcript = Transcript) => new
    {
        documentType = "SessionTranscript",
        doctorId = "doc-1",
        patientId,
        sessionId = $"sess-{patientId}",
        sequenceNumber,
        language = "en",
        transcript,
    };

    private static async Task<string> DocumentIdOfAsync(HttpClient client, string patientId, Guid ingestionId)
    {
        var documents = await client.GetFromJsonAsync<JsonElement>($"/patients/{patientId}/documents");
        return documents.EnumerateArray()
            .First(d => d.GetProperty("ingestionId").GetGuid() == ingestionId)
            .GetProperty("documentId").GetString()!;
    }

    private static async Task WaitForStatusAsync(HttpClient client, Guid ingestionId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");
            if (status.GetProperty("status").GetString() == expected)
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Ingestion {ingestionId} never reached {expected}.");
    }

    private async Task<long> CountChunksAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM chunks WHERE ingestion_id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string?> ReadPayloadAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT payload FROM ingestions WHERE id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (string)value;
    }

    private async Task<(string Status, string? DeletedBy, DateTimeOffset? DeletedAt)> ReadTombstoneAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, deleted_by, deleted_at FROM ingestions WHERE id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (
            reader.GetString(0),
            await reader.IsDBNullAsync(1) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }
}
