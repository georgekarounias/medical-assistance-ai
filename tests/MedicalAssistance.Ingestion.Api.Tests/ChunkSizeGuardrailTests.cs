using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Ingestions;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The chunk-size guardrails: once the plan is validated, code — no LLM —
/// merges fragments below the floor into a neighbor and splits chunks above the
/// ceiling at a line boundary. The transcripts here are sized against the
/// fixture's deliberately small thresholds.
/// </summary>
public class ChunkSizeGuardrailTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    // Lines 0-2 together are well above the ceiling; line 3 alone is well below
    // the floor. Both breaches are the agent's, and code has to repair them.
    private const string Transcript = """
        Doctor: Let's go through your history in detail before we decide on a treatment plan.
        Patient: I have had these headaches for about three months, mostly in the early morning.
        Doctor: Do they improve after you drink water or eat something once you are awake?
        Patient: Yes.
        Doctor: That pattern points to dehydration rather than anything structural.
        """;

    private const string PlanWithAnOversizedAndAnUndersizedChunk = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 2, "contextBlurb": "History taking around the headaches." },
            { "startLine": 3, "endLine": 3, "contextBlurb": "Confirms the pattern." },
            { "startLine": 4, "endLine": 4, "contextBlurb": "Doctor's interpretation." }
          ],
          "summary": "Morning headaches attributed to dehydration."
        }
        """;

    private const string UnsplittableLine =
        "Doctor: Your results show normal thyroid function, normal iron studies, normal inflammatory markers, "
        + "and a vitamin D level that sits just below the reference range used by this laboratory.";

    [Fact]
    public async Task Oversized_chunks_split_and_undersized_chunks_merge_without_losing_a_line()
    {
        fixture.ChatClient.EnqueueResponse(PlanWithAnOversizedAndAnUndersizedChunk);

        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostTranscriptAsync(client, sequenceNumber: 1, Transcript);
        var (status, detail) = await WaitForTerminalStatusAsync(client, ingestionId);
        Assert.True(status == "Completed", $"Expected Completed but got: {detail}");

        var dialog = (await ReadStoredChunksAsync(ingestionId)).Where(c => c.Kind == "dialog").ToList();

        // The guardrails move boundaries and nothing else: every line survives,
        // in order, in exactly one chunk.
        Assert.Equal(Transcript, string.Join("\n", dialog.Select(c => c.VerbatimText)));
        var ranges = dialog.Select(c => ParseLineRange(c.SourceRef)).ToList();
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(4, ranges[^1].End);
        for (var i = 1; i < ranges.Count; i++)
            Assert.Equal(ranges[i - 1].End + 1, ranges[i].Start);

        // Every stored chunk now sits inside the configured band.
        Assert.All(dialog, chunk => Assert.InRange(
            ChunkTokens.Estimate(chunk.VerbatimText),
            IngestionApiFixture.MinChunkTokens,
            IngestionApiFixture.MaxChunkTokens));

        // The oversized chunk became several, each cut at a line boundary.
        Assert.True(ranges.Count(r => r.End <= 2) > 1, "Lines 0-2 exceed the ceiling and should have been split.");

        // The one-line fragment was absorbed by a neighbor, and the blurbs of
        // both merged chunks survive so retrieval context is not thrown away.
        Assert.DoesNotContain(dialog, c => c.VerbatimText == "Patient: Yes.");
        var merged = Assert.Single(dialog, c => c.VerbatimText.Contains("Patient: Yes."));
        Assert.Contains("Confirms the pattern.", merged.ContextBlurb);
        Assert.Contains("Doctor's interpretation.", merged.ContextBlurb);
    }

    [Fact]
    public async Task A_single_line_above_the_ceiling_is_kept_whole_rather_than_cut_mid_sentence()
    {
        // Verbatim completeness outranks the size ceiling: there is no boundary
        // inside a line to split on, so the chunk stays oversized and intact.
        Assert.True(ChunkTokens.Estimate(UnsplittableLine) > IngestionApiFixture.MaxChunkTokens);

        var transcript = $"{UnsplittableLine}\nPatient: So the only thing that is off is my vitamin D level?";
        fixture.ChatClient.EnqueueResponse("""
            {
              "chunks": [
                { "startLine": 0, "endLine": 0, "contextBlurb": "Lab panel read out in full." },
                { "startLine": 1, "endLine": 1, "contextBlurb": "Patient checks the one abnormal result." }
              ],
              "summary": "Results reviewed; only vitamin D is low."
            }
            """);

        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostTranscriptAsync(client, sequenceNumber: 2, transcript);
        var (status, detail) = await WaitForTerminalStatusAsync(client, ingestionId);
        Assert.True(status == "Completed", $"Expected Completed but got: {detail}");

        var dialog = (await ReadStoredChunksAsync(ingestionId)).Where(c => c.Kind == "dialog").ToList();
        Assert.Equal(2, dialog.Count);
        Assert.Equal(UnsplittableLine, dialog[0].VerbatimText);
    }

    private static (int Start, int End) ParseLineRange(string? sourceRef)
    {
        var json = JsonDocument.Parse(sourceRef!).RootElement;
        return (json.GetProperty("startLine").GetInt32(), json.GetProperty("endLine").GetInt32());
    }

    private static async Task<Guid> PostTranscriptAsync(HttpClient client, int sequenceNumber, string transcript)
    {
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "doc-1",
            patientId = "pat-size",
            sessionId = "sess-size",
            sequenceNumber,
            language = "en",
            transcript,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
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
        throw new TimeoutException($"Ingestion never reached a terminal status. Last: {lastSeen}");
    }

    private async Task<List<StoredChunk>> ReadStoredChunksAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT chunk_kind, verbatim_text, context_blurb, source_ref FROM chunks WHERE ingestion_id = $1 ORDER BY chunk_index",
            connection);
        command.Parameters.AddWithValue(ingestionId);

        var chunks = new List<StoredChunk>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            chunks.Add(new StoredChunk(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return chunks;
    }

    private sealed record StoredChunk(string Kind, string VerbatimText, string? ContextBlurb, string? SourceRef);
}
