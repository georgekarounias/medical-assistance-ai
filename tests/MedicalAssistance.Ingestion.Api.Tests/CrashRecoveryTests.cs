using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// What happens when the service dies mid-flight. An accepted upload is a
/// promise: a deploy or a crash must never quietly abandon it, so recovery
/// picks up whatever was left unfinished — at startup, and on a timer
/// thereafter, because the process that abandons work is not always the process
/// that has to notice. The counterpart is an attempt cap: a document that kills
/// the process must not be able to kill it forever.
/// </summary>
public class CrashRecoveryTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
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
    public async Task Work_abandoned_by_a_crash_is_picked_up_by_the_next_startup()
    {
        // Accepted, then the service dies before any worker touches it.
        Guid ingestionId;
        await using (var accepting = fixture.CreateFactory(new ScriptedChatClient(), workerCount: 0))
            ingestionId = await PostAsync(accepting, "pat-abandoned");

        // The next deployment finds it and finishes the job — with no second
        // upload, and without the caller doing anything at all.
        var chat = new ScriptedChatClient();
        chat.EnqueueResponse(ValidPlan);
        await using var restarted = fixture.CreateFactory(chat);

        var client = restarted.CreateClient();
        await WaitForStatusAsync(client, ingestionId, "Completed");
        Assert.Equal(3, await CountChunksAsync(ingestionId));
    }

    [Fact]
    public async Task A_document_that_keeps_killing_the_service_is_given_up_on()
    {
        Guid ingestionId;
        await using (var accepting = fixture.CreateFactory(new ScriptedChatClient(), workerCount: 0))
            ingestionId = await PostAsync(accepting, "pat-poison");

        // Three crashes: each startup claims the work, hangs inside chunking,
        // and dies before reaching any terminal state — exactly what a document
        // that exhausts memory or wedges a worker looks like from the outside.
        for (var crash = 1; crash <= 3; crash++)
        {
            var crashingChat = new ScriptedChatClient();
            crashingChat.EnqueueBlockingResponse(ValidPlan); // never released
            await using var crashing = fixture.CreateFactory(crashingChat);
            _ = crashing.Server;
            await WaitForAttemptsAsync(ingestionId, crash);
        }

        // The fourth startup could process it — the model is answering fine now —
        // and refuses anyway. One poisonous document cannot occupy the service
        // for the rest of its life.
        var healthyChat = new ScriptedChatClient();
        healthyChat.EnqueueResponse(ValidPlan);
        await using var restarted = fixture.CreateFactory(healthyChat);

        var client = restarted.CreateClient();
        var status = await WaitForStatusAsync(client, ingestionId, "Failed");
        Assert.Contains("3 attempt", status.GetProperty("errorMessage").GetString()!);
        Assert.Empty(healthyChat.ReceivedPrompts);
        Assert.Equal(0, await CountChunksAsync(ingestionId));
    }

    [Fact]
    public async Task Work_another_instance_is_already_running_is_left_alone()
    {
        Guid ingestionId;
        await using (var accepting = fixture.CreateFactory(new ScriptedChatClient(), workerCount: 0))
            ingestionId = await PostAsync(accepting, "pat-two-instances");

        // One instance has it and is inside the chat call, holding it there.
        var busyChat = new ScriptedChatClient();
        var release = busyChat.EnqueueBlockingResponse(ValidPlan);
        await using var busy = fixture.CreateFactory(busyChat);
        _ = busy.Server;
        await WaitForAttemptsAsync(ingestionId, 1);

        // A second instance starts — a rolling deploy, or a scaled-out replica —
        // and its startup recovery sees the very same unfinished row. It must
        // leave it alone: two workers on one document race to write its chunk
        // set, and the loser's chunks are already committed by then.
        var idleChat = new ScriptedChatClient();
        await using var second = fixture.CreateFactory(idleChat);
        _ = second.Server;

        await Task.Delay(750); // long enough for a second claim to show itself
        Assert.Empty(idleChat.ReceivedPrompts);
        Assert.Equal(1, await ReadAttemptsAsync(ingestionId));

        // And the instance that owns it still finishes normally.
        release();
        await WaitForStatusAsync(busy.CreateClient(), ingestionId, "Completed");
        Assert.Equal(3, await CountChunksAsync(ingestionId));
    }

    [Fact]
    public async Task Work_orphaned_while_the_fleet_stays_up_is_taken_over_without_a_restart()
    {
        Guid ingestionId;
        await using (var accepting = fixture.CreateFactory(new ScriptedChatClient(), workerCount: 0))
            ingestionId = await PostAsync(accepting, "pat-orphaned");

        // Another instance owns it. The claim is the same advisory lock a worker
        // holds for the length of a run, taken here on a connection of the
        // test's own so that the owner can be killed on cue — and never
        // released politely, because an owner that tidies up is not an orphan.
        //
        // Unpooled on purpose. Closing a pooled connection hands the session
        // back to Npgsql rather than ending it, and the DISCARD ALL that would
        // drop its advisory locks is deferred until something reuses it — so a
        // pooled "death" here would keep holding the lock and prove nothing. A
        // process that actually dies drops its socket, which is what this does.
        var owner = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString);
        await owner.OpenAsync();
        Assert.NotNull(await PostgresAdvisoryLock.TryAcquireAsync(
            owner, PostgresAdvisoryLock.KeyFor(ingestionId)));

        var chat = new ScriptedChatClient();
        chat.EnqueueResponse(ValidPlan);
        await using var sweeping = fixture.CreateFactory(chat, sweepInterval: TimeSpan.FromMilliseconds(200));
        _ = sweeping.Server;

        // Sweeping repeatedly is not licence to touch work someone else holds:
        // several passes go by and this instance keeps its hands off. Asserted
        // on this ingestion's own attempt count rather than on the chat client,
        // which would also answer for anything else the sweep found.
        await Task.Delay(1000);
        Assert.Equal(0, await ReadAttemptsAsync(ingestionId));

        // The owner dies. Its connection drops, Postgres releases the lock with
        // it, and the ingestion is now unfinished and unowned — the state that
        // used to sit there until somebody happened to redeploy.
        await owner.CloseAsync();
        await owner.DisposeAsync();

        // Nothing restarts. The instance that had been skipping it takes it over.
        await WaitForStatusAsync(sweeping.CreateClient(), ingestionId, "Completed");
        Assert.Equal(3, await CountChunksAsync(ingestionId));
    }

    [Fact]
    public async Task A_stale_queue_entry_does_not_re_run_an_ingestion_that_has_since_finished()
    {
        var client = fixture.Factory.CreateClient();
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var ingestionId = await PostAsync(fixture.Factory, "pat-stale-entry");
        await WaitForStatusAsync(client, ingestionId, "Completed");

        var attempts = await ReadAttemptsAsync(ingestionId);
        var chunks = await CountChunksAsync(ingestionId);
        var prompts = fixture.ChatClient.ReceivedPrompts.Count;

        // A queue entry outlives the run it named: another instance's recovery
        // queued this id too, and only reaches it now that the work is over.
        // The advisory lock is free — the run it guarded has ended — so the
        // ingestion's own state is the only thing left to refuse a second run.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IngestionQueue>().EnqueueAsync(ingestionId);

        await Task.Delay(750);

        // Re-running it would re-embed a document already stored, and — where a
        // correction landed after this ingestion completed — supersede the
        // correction with the version it replaced.
        var status = await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");
        Assert.Equal("Completed", status.GetProperty("status").GetString());
        Assert.Equal(attempts, await ReadAttemptsAsync(ingestionId));
        Assert.Equal(chunks, await CountChunksAsync(ingestionId));
        Assert.Equal(prompts, fixture.ChatClient.ReceivedPrompts.Count);

        // And it is refused quietly, as work that is simply no longer anyone's
        // to do — not reported as a document that exhausted its attempts, which
        // would turn a finished ingestion into a failed one.
        Assert.Null(status.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task Resubmitting_a_failed_document_gives_it_a_fresh_set_of_attempts()
    {
        // No scripted response, so the first run fails on its own.
        var client = fixture.Factory.CreateClient();
        var payload = Payload("pat-second-wind");
        var response = await client.PostAsJsonAsync("/ingestions", payload);
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("ingestionId").GetGuid();
        await WaitForStatusAsync(client, ingestionId, "Failed");

        // A deliberate resubmission is a human deciding to try again — usually
        // because whatever broke has been fixed — so it starts the count over
        // rather than inheriting a spent budget.
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        await client.PostAsJsonAsync("/ingestions", payload);
        await WaitForStatusAsync(client, ingestionId, "Completed");

        Assert.Equal(1, await ReadAttemptsAsync(ingestionId));
    }

    private static async Task<Guid> PostAsync(WebApplicationFactory<Program> factory, string patientId)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/ingestions", Payload(patientId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
    }

    private static object Payload(string patientId) => new
    {
        documentType = "SessionTranscript",
        doctorId = "doc-1",
        patientId,
        sessionId = $"sess-{patientId}",
        sequenceNumber = 1,
        language = "en",
        transcript = Transcript,
    };

    private static async Task<JsonElement> WaitForStatusAsync(HttpClient client, Guid ingestionId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var lastSeen = "<never fetched>";
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");
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
            if (await ReadAttemptsAsync(ingestionId) >= expected)
                return;
            await Task.Delay(25);
        }
        Assert.Fail($"Ingestion {ingestionId} never reached attempt {expected}.");
    }

    private async Task<int> ReadAttemptsAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT attempts FROM ingestions WHERE id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        return (int)(await command.ExecuteScalarAsync())!;
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
