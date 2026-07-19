using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// Continuations: a session interrupted by a dead battery or a pause arrives as
/// several transcripts sharing one sessionId. They are siblings, not versions of
/// each other — every part stays in the record, and a question about the session
/// can reach all of them.
/// </summary>
public class ContinuationTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
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
            { "startLine": 0, "endLine": 1, "contextBlurb": "Opening of this part." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Detail gathered." }
          ],
          "summary": "Part of a session about morning headaches."
        }
        """;

    [Fact]
    public async Task Sibling_transcripts_of_one_session_coexist_and_are_both_reachable()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-continued";

        var partOneId = await IngestAsync(client, patientId, sequenceNumber: 1, PartOne);
        var partTwoId = await IngestAsync(client, patientId, sequenceNumber: 2, PartTwo);

        // A continuation adds to the session; it never replaces what came before.
        Assert.Equal("Completed", await ReadStatusAsync(client, partOneId));
        Assert.Equal("Completed", await ReadStatusAsync(client, partTwoId));

        // Another patient's session, so the scoping below is proving something.
        await IngestAsync(client, "pat-elsewhere", sequenceNumber: 1, PartOne);

        // The query a RAG chat will run: nearest chunks within one patient.
        var hits = await SearchAsync(patientId, "how much water does the patient drink");

        Assert.Equal(6, hits.Count); // both parts, three chunks each
        Assert.Equal(
            [$"doc-1#{patientId}#sess-{patientId}#1", $"doc-1#{patientId}#sess-{patientId}#2"],
            hits.Select(hit => hit.DocumentId).Distinct().Order().ToArray());
        Assert.Contains(hits, hit => hit.Text.Contains("three months"));
        Assert.Contains(hits, hit => hit.Text.Contains("one glass"));

        // Both parts answer to the same session, and no one else's text is here.
        Assert.All(hits, hit => Assert.Equal($"sess-{patientId}", hit.SessionId));
        Assert.All(hits, hit => Assert.Equal(patientId, hit.PatientId));
    }

    [Fact]
    public async Task A_continuation_can_be_submitted_while_its_earlier_part_is_still_processing()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-back-to-back";

        // The app uploads both halves the moment the session ends, so the second
        // arrives while the first is still being chunked. Different documents,
        // so the concurrency guard must not stand in the way.
        var promptsBefore = fixture.ChatClient.ReceivedPrompts.Count;
        var release = fixture.ChatClient.EnqueueBlockingResponse(TwoChunkPlan);
        var partOneId = await PostAsync(client, patientId, sequenceNumber: 1, PartOne, scriptResponse: false);
        await WaitForChatCallsAsync(promptsBefore + 1);

        fixture.ChatClient.EnqueueResponse(TwoChunkPlan);
        var partTwo = await client.PostAsJsonAsync(
            "/ingestions", Payload(patientId, sequenceNumber: 2, PartTwo));

        Assert.Equal(HttpStatusCode.Accepted, partTwo.StatusCode);
        var partTwoId = (await partTwo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
        await WaitForStatusAsync(client, partTwoId, "Completed");

        release();
        await WaitForStatusAsync(client, partOneId, "Completed");

        var hits = await SearchAsync(patientId, "what did the patient say");
        Assert.Equal(6, hits.Count);
        Assert.Equal(2, hits.Select(hit => hit.DocumentId).Distinct().Count());
    }

    /// <summary>
    /// Runs the retrieval query this store exists to serve: chunks for one
    /// patient, ordered by distance from the question's embedding. The test
    /// embeddings are arbitrary, so this proves reachability and scoping — that
    /// every sibling's chunks are present, filterable, and ranked — rather than
    /// anything about relevance.
    /// </summary>
    private async Task<List<SearchHit>> SearchAsync(string patientId, string question)
    {
        var embedding = await new DeterministicEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions)
            .GenerateAsync([question]);
        var queryVector = "[" + string.Join(
            ",", embedding[0].Vector.ToArray().Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT document_id, session_id, patient_id, verbatim_text
            FROM chunks
            WHERE patient_id = $1
            ORDER BY embedding <-> $2::vector
            LIMIT 20
            """,
            connection);
        command.Parameters.AddWithValue(patientId);
        command.Parameters.AddWithValue(queryVector);

        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            hits.Add(new SearchHit(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return hits;
    }

    private async Task<Guid> IngestAsync(HttpClient client, string patientId, int sequenceNumber, string transcript)
    {
        var ingestionId = await PostAsync(client, patientId, sequenceNumber, transcript);
        await WaitForStatusAsync(client, ingestionId, "Completed");
        return ingestionId;
    }

    private async Task<Guid> PostAsync(
        HttpClient client, string patientId, int sequenceNumber, string transcript, bool scriptResponse = true)
    {
        if (scriptResponse)
            fixture.ChatClient.EnqueueResponse(TwoChunkPlan);

        var response = await client.PostAsJsonAsync("/ingestions", Payload(patientId, sequenceNumber, transcript));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
    }

    private static object Payload(string patientId, int sequenceNumber, string transcript) => new
    {
        documentType = "SessionTranscript",
        doctorId = "doc-1",
        patientId,
        sessionId = $"sess-{patientId}",
        sequenceNumber,
        language = "en",
        transcript,
    };

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

    private async Task WaitForChatCallsAsync(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (fixture.ChatClient.ReceivedPrompts.Count >= expected)
                return;
            await Task.Delay(25);
        }
        Assert.Fail($"Expected {expected} chunking calls, saw {fixture.ChatClient.ReceivedPrompts.Count}.");
    }

    private sealed record SearchHit(string DocumentId, string SessionId, string PatientId, string Text);
}
