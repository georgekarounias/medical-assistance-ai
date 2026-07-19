using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Security;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// GDPR Erasure: DELETE /patients/{patientId}/data removes everything the service
/// holds about a patient — chunks, ingestion rows, and the Deleted tombstones
/// un-ingest leaves behind — and records the erasure itself in a log that the
/// erasure does not touch. It is guarded by a separate admin secret (ADR-0007):
/// a leaked everyday key can read and un-ingest, but it cannot erase a patient.
/// </summary>
public class PatientDataErasureTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string Transcript = """
        Doctor: Good morning, what brings you in today?
        Patient: I keep waking up with headaches.
        Doctor: How much water do you usually drink?
        Patient: Maybe one glass a day.
        """;

    private const string OtherTranscript = """
        Doctor: Afternoon — how has the knee been since the last visit?
        Patient: Still stiff first thing, but the swelling is down.
        Doctor: Keep icing it after the exercises.
        Patient: Will do.
        """;

    private const string TwoChunkPlan = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Complaint discussed." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Advice given." }
          ],
          "summary": "Session summary."
        }
        """;

    [Fact]
    public async Task Erasing_a_patient_removes_every_trace_including_tombstones_and_logs_the_act()
    {
        var admin = AdminClient();
        const string patientId = "pat-erase-me";

        // A live document, and a second that has been un-ingested — so the
        // patient has both chunks and a Deleted tombstone for erasure to remove.
        var liveId = await IngestAsync(admin, patientId, sequenceNumber: 1, Transcript);
        var removedId = await IngestAsync(admin, patientId, sequenceNumber: 2, OtherTranscript);
        var removedDocumentId = await DocumentIdOfAsync(admin, patientId, removedId);
        await admin.DeleteAsync($"/documents/{Uri.EscapeDataString(removedDocumentId)}?removedBy=doc-1");

        Assert.True(await IngestionRowCountAsync(patientId) >= 2);

        var response = await admin.DeleteAsync($"/patients/{patientId}/data?erasedBy=admin-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Nothing about the patient is left: no chunks, no ingestion rows at all
        // — the live one, the tombstone, every version.
        Assert.Equal(0, await ChunkRowCountAsync(patientId));
        Assert.Equal(0, await IngestionRowCountAsync(patientId));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/ingestions/{liveId}")).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>($"/patients/{patientId}/documents")).EnumerateArray());

        // The one thing erasure leaves is proof it happened: an erasure-log row
        // naming the patient, who erased it, and how much was removed.
        var (erasedBy, ingestionsErased, chunksErased) = await ReadErasureLogAsync(patientId);
        Assert.Equal("admin-1", erasedBy);
        Assert.True(ingestionsErased >= 2, $"expected >= 2 ingestions erased, got {ingestionsErased}");
        Assert.True(chunksErased >= 3, $"expected >= 3 chunks erased, got {chunksErased}");
        Assert.Equal(ingestionsErased, body.GetProperty("ingestionsErased").GetInt32());
        Assert.Equal(chunksErased, body.GetProperty("chunksErased").GetInt32());
    }

    [Fact]
    public async Task An_everyday_secret_cannot_erase_a_patient()
    {
        var admin = AdminClient();
        const string patientId = "pat-erase-forbidden";
        var ingestionId = await IngestAsync(admin, patientId, sequenceNumber: 1, Transcript);

        // The ordinary key authenticates the backend for everything else, but the
        // separate admin key is what erasure demands — so this is refused as
        // forbidden, not unauthenticated.
        var everyday = fixture.Factory.CreateClient();
        var response = await everyday.DeleteAsync($"/patients/{patientId}/data?erasedBy=admin-1");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // And the patient's data is untouched by the refused attempt.
        Assert.Equal(3, await ChunkRowCountAsync(patientId));
        Assert.Equal("Completed", (await admin.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}"))
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task Erasure_with_no_secret_at_all_is_unauthorized()
    {
        var anonymous = fixture.Factory.CreateClient();
        anonymous.DefaultRequestHeaders.Remove(ApiKeyAuthentication.HeaderName);

        var response = await anonymous.DeleteAsync("/patients/pat-erase-anon/data?erasedBy=admin-1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Erasing_one_patient_leaves_another_patients_data_intact()
    {
        var admin = AdminClient();
        var keptId = await IngestAsync(admin, "pat-erase-neighbour-kept", sequenceNumber: 1, Transcript);
        var erasedId = await IngestAsync(admin, "pat-erase-neighbour-gone", sequenceNumber: 1, OtherTranscript);

        var response = await admin.DeleteAsync("/patients/pat-erase-neighbour-gone/data?erasedBy=admin-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Erasure is scoped to its patient and no wider.
        Assert.Equal(0, await ChunkRowCountAsync("pat-erase-neighbour-gone"));
        Assert.Equal(3, await ChunkRowCountAsync("pat-erase-neighbour-kept"));
        Assert.Equal("Completed", (await admin.GetFromJsonAsync<JsonElement>($"/ingestions/{keptId}"))
            .GetProperty("status").GetString());
        _ = erasedId;
    }

    [Fact]
    public async Task Erasing_without_saying_who_is_erasing_is_rejected()
    {
        var admin = AdminClient();
        const string patientId = "pat-erase-anon-actor";
        var ingestionId = await IngestAsync(admin, patientId, sequenceNumber: 1, Transcript);

        // Erasure is a logged administrative act; it has to name who performed it,
        // so a request that does not is refused before anything is removed.
        var response = await admin.DeleteAsync($"/patients/{patientId}/data");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(3, await ChunkRowCountAsync(patientId));
        _ = ingestionId;
    }

    [Fact]
    public async Task Erasing_a_patient_with_nothing_held_still_succeeds_and_is_logged()
    {
        var admin = AdminClient();
        const string patientId = "pat-erase-unknown";

        // Erasure guarantees a state — no data for this patient — rather than
        // acting on a precondition, so a patient the service holds nothing about
        // is a valid, and still auditable, erasure of zero rows.
        var response = await admin.DeleteAsync($"/patients/{patientId}/data?erasedBy=admin-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (erasedBy, ingestionsErased, chunksErased) = await ReadErasureLogAsync(patientId);
        Assert.Equal("admin-1", erasedBy);
        Assert.Equal(0, ingestionsErased);
        Assert.Equal(0, chunksErased);
    }

    private HttpClient AdminClient()
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ApiKeyAuthentication.HeaderName);
        client.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, IngestionApiFixture.AdminApiKey);
        return client;
    }

    private async Task<Guid> IngestAsync(HttpClient client, string patientId, int sequenceNumber, string transcript)
    {
        fixture.ChatClient.EnqueueResponse(TwoChunkPlan);
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "doc-1",
            patientId,
            sessionId = $"sess-{patientId}",
            sequenceNumber,
            language = "en",
            transcript,
        });
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
        await WaitForStatusAsync(client, ingestionId, "Completed");
        return ingestionId;
    }

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

    private async Task<long> ChunkRowCountAsync(string patientId) =>
        await ScalarAsync("SELECT COUNT(*) FROM chunks WHERE patient_id = $1", patientId);

    private async Task<long> IngestionRowCountAsync(string patientId) =>
        await ScalarAsync("SELECT COUNT(*) FROM ingestions WHERE patient_id = $1", patientId);

    private async Task<long> ScalarAsync(string sql, string patientId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(patientId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(string ErasedBy, int IngestionsErased, int ChunksErased)> ReadErasureLogAsync(string patientId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT erased_by, ingestions_erased, chunks_erased FROM erasure_log " +
            "WHERE patient_id = $1 ORDER BY erased_at DESC LIMIT 1",
            connection);
        command.Parameters.AddWithValue(patientId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"no erasure_log row for {patientId}");
        return (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2));
    }
}
