using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The retry endpoint: recovery costs one call and never a re-upload, because
/// the original payload was stored the moment it was accepted. A rerun is always
/// the whole pipeline from the beginning — there are no stage checkpoints to
/// resume from (ADR-0003), which is what keeps the failure model small enough to
/// hold in your head.
/// </summary>
public class RetryEndpointTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string Transcript = """
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
    public async Task A_failed_ingestion_is_rerun_from_its_stored_payload()
    {
        var client = fixture.Factory.CreateClient();

        // No scripted response, so the first run fails.
        var ingestionId = await PostAsync(client, "pat-retried");
        await WaitForStatusAsync(client, ingestionId, "Failed");

        // The caller never sends the transcript again — one call, no payload.
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var retry = await client.PostAsync($"/ingestions/{ingestionId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ingestionId, body.GetProperty("ingestionId").GetGuid());

        var completed = await WaitForStatusAsync(client, ingestionId, "Completed");
        Assert.True(completed.GetProperty("errorMessage").ValueKind is JsonValueKind.Null);
        Assert.Equal(3, await CountChunksAsync(ingestionId));
    }

    [Fact]
    public async Task Retrying_an_ingestion_that_never_existed_is_not_found()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsync($"/ingestions/{Guid.NewGuid()}/retry", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retrying_an_ingestion_that_did_not_fail_is_refused()
    {
        var client = fixture.Factory.CreateClient();

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var ingestionId = await PostAsync(client, "pat-already-done");
        await WaitForStatusAsync(client, ingestionId, "Completed");
        var promptsBefore = fixture.ChatClient.ReceivedPrompts.Count;

        // Rerunning a success would spend tokens to reproduce what is already
        // stored, so the endpoint says what state it is actually in instead.
        var retry = await client.PostAsync($"/ingestions/{ingestionId}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var problem = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Completed", problem.GetProperty("detail").GetString()!);

        await Task.Delay(300);
        Assert.Equal(promptsBefore, fixture.ChatClient.ReceivedPrompts.Count);
        Assert.Equal("Completed", (await ReadStatusAsync(client, ingestionId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_ingestion_that_used_up_its_attempts_can_still_be_retried()
    {
        // Burn the attempt budget the way a poisonous document would: claimed,
        // hung, and killed, three times over.
        Guid ingestionId;
        await using (var accepting = fixture.CreateFactory(new ScriptedChatClient(), workerCount: 0))
        {
            var response = await accepting.CreateClient().PostAsJsonAsync("/ingestions", Payload("pat-exhausted"));
            ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("ingestionId").GetGuid();
        }

        for (var crash = 1; crash <= 3; crash++)
        {
            var crashingChat = new ScriptedChatClient();
            crashingChat.EnqueueBlockingResponse(ValidPlan);
            await using var crashing = fixture.CreateFactory(crashingChat);
            _ = crashing.Server;
            await WaitForAttemptsAsync(ingestionId, crash);
        }

        var healthyChat = new ScriptedChatClient();
        await using var restarted = fixture.CreateFactory(healthyChat);
        var client = restarted.CreateClient();
        var failed = await WaitForStatusAsync(client, ingestionId, "Failed");
        Assert.Contains("3 attempt", failed.GetProperty("errorMessage").GetString()!);

        // This is exactly the document that most needs recovering: the cap stops
        // the service retrying by itself, not a person deciding to try again
        // once whatever broke has been fixed.
        healthyChat.EnqueueResponse(ValidPlan);
        var retry = await client.PostAsync($"/ingestions/{ingestionId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        await WaitForStatusAsync(client, ingestionId, "Completed");
        Assert.Equal(3, await CountChunksAsync(ingestionId));
    }

    [Fact]
    public async Task Rerunning_a_failed_ingestion_a_correction_has_replaced_is_refused()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-overtaken";

        // The first upload fails, and the doctor does not wait around: they fix
        // the transcript and send the corrected one, which lands.
        var failedId = await PostAsync(client, patientId);
        await WaitForStatusAsync(client, failedId, "Failed");

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var correctedId = await PostAsync(client, patientId, CorrectedTranscript);
        await WaitForStatusAsync(client, correctedId, "Completed");

        // Rerunning the failed one now would republish the text the correction
        // replaced — the record would silently revert to a version a doctor had
        // already rejected. It is stale work, so it is refused.
        var retry = await client.PostAsync($"/ingestions/{failedId}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var problem = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("newer", problem.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);

        await Task.Delay(300);
        var chunks = await ReadDocumentChunksAsync(patientId);
        Assert.Contains(chunks, text => text.Contains("migraines"));
        Assert.DoesNotContain(chunks, text => text.Contains("one glass a day"));
        Assert.Equal("Completed", (await ReadStatusAsync(client, correctedId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_correction_that_failed_is_still_rerun_over_the_version_it_replaces()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-late-correction";

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var originalId = await PostAsync(client, patientId);
        await WaitForStatusAsync(client, originalId, "Completed");

        // The correction is the newer intent, so its failure is exactly the kind
        // worth recovering — the guard against stale reruns must not swallow it.
        var correctionId = await PostAsync(client, patientId, CorrectedTranscript);
        await WaitForStatusAsync(client, correctionId, "Failed");

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var retry = await client.PostAsync($"/ingestions/{correctionId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        await WaitForStatusAsync(client, correctionId, "Completed");

        var chunks = await ReadDocumentChunksAsync(patientId);
        Assert.Contains(chunks, text => text.Contains("migraines"));
        Assert.Equal("Superseded", (await ReadStatusAsync(client, originalId)).GetProperty("status").GetString());
    }

    private static async Task<Guid> PostAsync(HttpClient client, string patientId, string? transcript = null)
    {
        var response = await client.PostAsJsonAsync("/ingestions", Payload(patientId, transcript));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
    }

    private static object Payload(string patientId, string? transcript = null) => new
    {
        documentType = "SessionTranscript",
        doctorId = "doc-1",
        patientId,
        sessionId = $"sess-{patientId}",
        sequenceNumber = 1,
        language = "en",
        transcript = transcript ?? Transcript,
    };

    private async Task<List<string>> ReadDocumentChunksAsync(string patientId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT verbatim_text FROM chunks WHERE document_id = $1 ORDER BY chunk_index", connection);
        command.Parameters.AddWithValue($"sess-{patientId}#1");

        var texts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            texts.Add(reader.GetString(0));
        return texts;
    }

    private static async Task<JsonElement> ReadStatusAsync(HttpClient client, Guid ingestionId) =>
        await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");

    private static async Task<JsonElement> WaitForStatusAsync(HttpClient client, Guid ingestionId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var lastSeen = "<never fetched>";
        while (DateTime.UtcNow < deadline)
        {
            var status = await ReadStatusAsync(client, ingestionId);
            lastSeen = status.GetRawText();
            if (status.GetProperty("status").GetString() == expected)
                return status;
            await Task.Delay(50);
        }
        Assert.Fail($"Ingestion {ingestionId} never reached {expected}. Last: {lastSeen}");
        throw new UnreachableException();
    }

    private async Task WaitForAttemptsAsync(Guid ingestionId, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT attempts FROM ingestions WHERE id = $1", connection);
            command.Parameters.AddWithValue(ingestionId);
            if ((int)(await command.ExecuteScalarAsync())! >= expected)
                return;
            await Task.Delay(25);
        }
        Assert.Fail($"Ingestion {ingestionId} never reached attempt {expected}.");
    }

    private async Task<long> CountChunksAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM chunks WHERE ingestion_id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
