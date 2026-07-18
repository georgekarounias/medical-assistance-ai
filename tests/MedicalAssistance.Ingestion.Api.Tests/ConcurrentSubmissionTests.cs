using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The concurrency guard: while an ingestion for a document identity is still
/// Queued or Processing, a second submission for it is refused with 409. Two
/// workers running the same document at once would race to write its chunk set.
/// Unrelated documents are unaffected and keep running in parallel.
/// </summary>
public class ConcurrentSubmissionTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string Transcript = """
        Doctor: Good morning, what brings you in today?
        Patient: I keep waking up with headaches.
        Doctor: How much water do you usually drink?
        Patient: Maybe one glass a day.
        """;

    private const string ValidPlan = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Complaint discussed." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Hydration advice." }
          ],
          "summary": "Session summary."
        }
        """;

    [Fact]
    public async Task A_second_submission_while_the_first_is_still_queued_is_refused()
    {
        // A instance with no workers parks whatever it accepts in Queued, which
        // is the state a real service is in between accepting and picking up.
        await using var parked = fixture.CreateFactory(new ScriptedChatClient(), workerCount: 0);
        var client = parked.CreateClient();
        var payload = TranscriptPayload("pat-queued", Transcript);

        var accepted = await client.PostAsJsonAsync("/ingestions", payload);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var queuedId = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
        var ingestionsAfterFirst = await CountAsync("ingestions");

        var conflict = await client.PostAsJsonAsync("/ingestions", payload);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();

        // The caller is told which ingestion to watch rather than just refused.
        Assert.Equal(queuedId, problem.GetProperty("ingestionId").GetGuid());
        Assert.Equal(ingestionsAfterFirst, await CountAsync("ingestions"));
    }

    [Fact]
    public async Task A_second_submission_while_the_first_is_being_processed_is_refused()
    {
        var client = fixture.Factory.CreateClient();
        var payload = TranscriptPayload("pat-processing", Transcript);

        // Hold the chunking call open so the ingestion is genuinely Processing —
        // the window in which a second worker could corrupt the chunk set.
        var release = fixture.ChatClient.EnqueueBlockingResponse(ValidPlan);
        var processingId = await PostAsync(client, payload);
        await WaitForStatusAsync(client, processingId, "Processing");

        var conflict = await client.PostAsJsonAsync("/ingestions", payload);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(processingId, problem.GetProperty("ingestionId").GetGuid());

        // Once it lands, the document is ingested exactly once.
        release();
        await WaitForStatusAsync(client, processingId, "Completed");
        Assert.Equal(3, await CountChunksAsync(processingId));
    }

    [Fact]
    public async Task An_unrelated_document_is_not_blocked_by_one_in_flight()
    {
        var client = fixture.Factory.CreateClient();

        var promptsBefore = fixture.ChatClient.ReceivedPrompts.Count;
        var release = fixture.ChatClient.EnqueueBlockingResponse(ValidPlan);
        var blockedId = await PostAsync(client, TranscriptPayload("pat-holds-worker", Transcript));

        // Wait for the chunking call itself, not merely for Processing: the
        // status flips before the call, and the next response queued must go to
        // the second document rather than being taken by this one.
        await WaitForChatCallsAsync(promptsBefore + 1);

        // Different patient, so a different document: the guard is about one
        // document at a time, not one document at a time service-wide.
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var otherId = await PostAsync(client, TranscriptPayload("pat-unrelated", Transcript));
        await WaitForStatusAsync(client, otherId, "Completed");

        release();
        await WaitForStatusAsync(client, blockedId, "Completed");
    }

    // A session belongs to one encounter with one patient, so each test gets its
    // own — and a parked ingestion from one test cannot block the next.
    private static object TranscriptPayload(string patientId, string transcript) => new
    {
        documentType = "SessionTranscript",
        doctorId = "doc-1",
        patientId,
        sessionId = $"sess-{patientId}",
        sequenceNumber = 1,
        language = "en",
        transcript,
    };

    private static async Task<Guid> PostAsync(HttpClient client, object payload)
    {
        var response = await client.PostAsJsonAsync("/ingestions", payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
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

    private async Task<long> CountChunksAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM chunks WHERE ingestion_id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
