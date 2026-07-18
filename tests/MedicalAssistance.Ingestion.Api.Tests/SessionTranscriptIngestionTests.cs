using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

public class SessionTranscriptIngestionTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    [Fact]
    public async Task Valid_transcript_reaches_completed_with_verbatim_chunks_and_a_summary_chunk()
    {
        // The chunking agent proposes line boundaries, blurbs, and a summary —
        // it never re-emits transcript text (boundaries-only chunking).
        fixture.ChatClient.EnqueueResponse("""
            {
              "chunks": [
                { "startLine": 0, "endLine": 1, "contextBlurb": "Patient reports recurring morning headaches." },
                { "startLine": 2, "endLine": 3, "contextBlurb": "Doctor explores hydration as a likely cause." }
              ],
              "summary": "Session about recurring morning headaches; low water intake identified as likely cause."
            }
            """);

        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "doc-1",
            patientId = "pat-1",
            sessionId = "sess-1",
            sequenceNumber = 1,
            sessionDate = "2026-07-10T14:30:00Z",
            language = "en",
            transcript = """
                Doctor: Good morning, what brings you in today?
                Patient: I keep waking up with headaches.
                Doctor: How much water do you usually drink?
                Patient: Maybe one glass a day.
                """,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("ingestionId").GetGuid();

        var (status, detail) = await WaitForTerminalStatusAsync(client, ingestionId);
        Assert.True(status == "Completed", $"Expected Completed but ingestion ended as: {detail}");

        var chunks = await ReadStoredChunksAsync(ingestionId);
        Assert.Equal(3, chunks.Count);

        var dialog = chunks.Where(c => c.Kind == "dialog").ToList();
        Assert.Equal(2, dialog.Count);
        Assert.Equal(
            "Doctor: Good morning, what brings you in today?\nPatient: I keep waking up with headaches.",
            dialog[0].VerbatimText);
        Assert.Equal("Patient reports recurring morning headaches.", dialog[0].ContextBlurb);

        var summary = Assert.Single(chunks, c => c.Kind == "summary");
        Assert.Equal(
            "Session about recurring morning headaches; low water intake identified as likely cause.",
            summary.VerbatimText);
    }

    private static async Task<(string Status, string Detail)> WaitForTerminalStatusAsync(HttpClient client, Guid ingestionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var lastSeen = "<never fetched>";
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");
            lastSeen = status.GetRawText();
            var state = status.GetProperty("status").GetString()!;
            if (state is "Completed" or "Failed")
                return (state, lastSeen);
            await Task.Delay(100);
        }
        throw new TimeoutException($"Ingestion never reached a terminal status. Last response: {lastSeen}");
    }

    private async Task<List<(string Kind, string VerbatimText, string? ContextBlurb)>> ReadStoredChunksAsync(Guid ingestionId)
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
}
