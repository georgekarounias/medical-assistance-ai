using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// A Correction: the same document identity re-submitted with different content
/// replaces what was there. The replacement is one transaction — retrieval never
/// sees both versions of a transcript, and never sees neither — because a chat
/// that can quote a retracted sentence is worse than one that knows nothing.
/// </summary>
public class CorrectionTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string OriginalTranscript = """
        Doctor: Good morning, what brings you in today?
        Patient: I keep waking up with headaches.
        Doctor: How much water do you usually drink?
        Patient: Maybe one glass a day.
        """;

    private const string CorrectedTranscript = """
        Doctor: Good morning, what brings you in today?
        Patient: I keep waking up with migraines, not headaches.
        Doctor: How much water do you usually drink?
        Patient: Maybe two glasses a day.
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
    public async Task A_correction_replaces_the_previous_chunks_and_supersedes_its_ingestion()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-corrected";

        var originalId = await IngestAsync(client, patientId, sequenceNumber: 1, OriginalTranscript);
        var corrected = await PostAsync(client, patientId, sequenceNumber: 1, CorrectedTranscript);
        Assert.False(corrected.Duplicate);
        await WaitForStatusAsync(client, corrected.Id, "Completed");

        // The document holds one version's worth of chunks, not two.
        var chunks = await ReadDocumentChunksAsync(patientId, sequenceNumber: 1);
        Assert.Equal(3, chunks.Count);
        Assert.Contains(chunks, text => text.Contains("migraines"));
        Assert.DoesNotContain(chunks, text => text.Contains("one glass a day"));

        // The replaced ingestion says so, rather than claiming a success whose
        // chunks no longer exist.
        Assert.Equal("Superseded", await ReadStatusAsync(client, originalId));
        Assert.Equal(0, await CountChunksOfAsync(originalId));
    }

    [Fact]
    public async Task Re_posting_the_original_content_after_a_correction_ingests_it_again()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-reverted";

        await IngestAsync(client, patientId, sequenceNumber: 1, OriginalTranscript);
        var correctedId = await IngestAsync(client, patientId, sequenceNumber: 1, CorrectedTranscript);

        // The original content is no longer in the store, so re-submitting it is
        // real work — treating it as an already-ingested duplicate would answer
        // "you have this" about text that was deleted.
        var reverted = await PostAsync(client, patientId, sequenceNumber: 1, OriginalTranscript);
        Assert.False(reverted.Duplicate);
        await WaitForStatusAsync(client, reverted.Id, "Completed");

        var chunks = await ReadDocumentChunksAsync(patientId, sequenceNumber: 1);
        Assert.Equal(3, chunks.Count);
        Assert.Contains(chunks, text => text.Contains("one glass a day"));
        Assert.DoesNotContain(chunks, text => text.Contains("migraines"));
        Assert.Equal("Superseded", await ReadStatusAsync(client, correctedId));
    }

    [Fact]
    public async Task A_correction_leaves_the_other_transcripts_of_the_session_untouched()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-siblings";

        await IngestAsync(client, patientId, sequenceNumber: 1, OriginalTranscript);
        var siblingId = await IngestAsync(client, patientId, sequenceNumber: 2, CorrectedTranscript);

        // Correcting one transcript must not touch its siblings: an interrupted
        // session's parts are separate documents that happen to share a session.
        var correction = await PostAsync(client, patientId, sequenceNumber: 1, """
            Doctor: Good morning, what brings you in today?
            Patient: I keep waking up with cluster headaches.
            Doctor: How much water do you usually drink?
            Patient: Maybe half a glass a day.
            """);
        await WaitForStatusAsync(client, correction.Id, "Completed");

        Assert.Equal("Completed", await ReadStatusAsync(client, siblingId));
        Assert.Equal(3, await CountChunksOfAsync(siblingId));
        var sibling = await ReadDocumentChunksAsync(patientId, sequenceNumber: 2);
        Assert.Contains(sibling, text => text.Contains("migraines"));
    }

    private async Task<Guid> IngestAsync(HttpClient client, string patientId, int sequenceNumber, string transcript)
    {
        var accepted = await PostAsync(client, patientId, sequenceNumber, transcript);
        await WaitForStatusAsync(client, accepted.Id, "Completed");
        return accepted.Id;
    }

    private async Task<(Guid Id, bool Duplicate)> PostAsync(
        HttpClient client, string patientId, int sequenceNumber, string transcript)
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

    private async Task<List<string>> ReadDocumentChunksAsync(string patientId, int sequenceNumber)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT verbatim_text FROM chunks WHERE document_id = $1 ORDER BY chunk_index", connection);
        command.Parameters.AddWithValue($"sess-{patientId}#{sequenceNumber}");

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
}
