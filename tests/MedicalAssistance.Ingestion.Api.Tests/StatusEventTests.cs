using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Realtime;
using MedicalAssistance.Ingestion.Api.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// Stage-level progress events. The doctor is watching a progress bar while a
/// transcript is processed, so the service says where it actually is — and when
/// it fails, says why. The events are a convenience: the REST status stays the
/// source of truth, and an ingestion runs identically whether or not anyone is
/// listening.
/// </summary>
public class StatusEventTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
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
    public async Task Each_stage_of_a_successful_ingestion_is_announced_in_order()
    {
        var received = new ConcurrentQueue<IngestionStatusEvent>();
        await using var connection = await ConnectAsync(received);

        var client = fixture.Factory.CreateClient();
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var ingestionId = await PostAsync(client, "pat-watched");

        var events = await WaitForTerminalEventAsync(received, ingestionId);

        // Named stages, in the order the work actually happens — a progress bar
        // that says "Embedding" is telling the truth about where it is.
        Assert.Equal(
            [
                IngestionStages.Queued,
                IngestionStages.Chunking,
                IngestionStages.Embedding,
                IngestionStages.Storing,
                IngestionStages.Completed,
            ],
            events.Select(e => e.Stage).ToArray());

        // Every event carries the doctor, because the backend fans out on it.
        Assert.All(events, e => Assert.Equal("doc-watching", e.DoctorId));
        Assert.All(events, e => Assert.Equal("pat-watched", e.PatientId));
        Assert.All(events, e => Assert.Null(e.ErrorMessage));

        // And says which document it is about, so a client can name the
        // transcript on screen without a round trip to work out what an
        // ingestion id refers to.
        Assert.All(events, e => Assert.Equal("doc-watching#pat-watched#sess-pat-watched#1", e.DocumentId));
        Assert.All(events, e => Assert.Equal("sess-pat-watched", e.SessionId));
    }

    [Fact]
    public async Task A_failure_names_the_document_it_could_not_ingest()
    {
        var received = new ConcurrentQueue<IngestionStatusEvent>();
        await using var connection = await ConnectAsync(received);

        // No scripted response, so this fails. The failure path rebuilds the
        // event from the stored payload rather than from a live request, so it
        // is the one most likely to lose the document it is talking about.
        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostAsync(client, "pat-failed-named");

        var failure = (await WaitForTerminalEventAsync(received, ingestionId))[^1];

        Assert.Equal(IngestionStages.Failed, failure.Stage);
        Assert.Equal("doc-watching#pat-failed-named#sess-pat-failed-named#1", failure.DocumentId);
        Assert.Equal("sess-pat-failed-named", failure.SessionId);
    }

    [Fact]
    public async Task A_failure_is_announced_with_the_reason_it_failed()
    {
        var received = new ConcurrentQueue<IngestionStatusEvent>();
        await using var connection = await ConnectAsync(received);

        // No scripted response, so chunking fails.
        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostAsync(client, "pat-failed-loudly");

        var events = await WaitForTerminalEventAsync(received, ingestionId);
        var failure = events[^1];

        Assert.Equal(IngestionStages.Failed, failure.Stage);
        Assert.False(string.IsNullOrWhiteSpace(failure.ErrorMessage));

        // The doctor is told something went wrong at the point it went wrong,
        // rather than watching a bar that never moves.
        Assert.Equal(IngestionStages.Chunking, events[^2].Stage);
    }

    [Fact]
    public async Task A_rerun_asked_for_by_the_retry_endpoint_is_announced_as_Queued()
    {
        var received = new ConcurrentQueue<IngestionStatusEvent>();
        await using var connection = await ConnectAsync(received);

        // No scripted response, so the first run fails and there is something
        // to recover.
        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostAsync(client, "pat-rerun-announced");
        await WaitForTerminalEventAsync(received, ingestionId);
        received.Clear();

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        await client.PostAsync($"/ingestions/{ingestionId}/retry", null);

        // Recovery is the moment a doctor is most likely to be watching, so a
        // rerun has to announce itself like any other work — otherwise the bar
        // sits still until Chunking and the retry looks like it did nothing.
        var events = await WaitForTerminalEventAsync(received, ingestionId);
        Assert.Equal(IngestionStages.Queued, events[0].Stage);
        Assert.Equal("doc-watching", events[0].DoctorId);
        Assert.Equal("pat-rerun-announced", events[0].PatientId);
    }

    [Fact]
    public async Task A_rerun_asked_for_by_resubmitting_the_content_is_announced_as_Queued()
    {
        var received = new ConcurrentQueue<IngestionStatusEvent>();
        await using var connection = await ConnectAsync(received);

        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostAsync(client, "pat-resubmit-announced");
        await WaitForTerminalEventAsync(received, ingestionId);
        received.Clear();

        // The other way to ask for the same rerun: send the identical content
        // again. It reuses the failed ingestion, so it must announce it too.
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        await client.PostAsJsonAsync("/ingestions", Payload("pat-resubmit-announced"));

        var events = await WaitForTerminalEventAsync(received, ingestionId);
        Assert.Equal(IngestionStages.Queued, events[0].Stage);
        Assert.Equal("pat-resubmit-announced", events[0].PatientId);
    }

    [Fact]
    public async Task An_ingestion_that_nobody_is_listening_to_still_completes()
    {
        var client = fixture.Factory.CreateClient();
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var ingestionId = await PostAsync(client, "pat-unwatched");

        // Publishing is a side channel: with no subscriber at all, the pipeline
        // must be entirely unaffected.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");
            if (status.GetProperty("status").GetString() == "Completed")
                return;
            await Task.Delay(50);
        }
        Assert.Fail("Ingestion never completed while no client was connected to the hub.");
    }

    private async Task<HubConnection> ConnectAsync(ConcurrentQueue<IngestionStatusEvent> received)
    {
        var server = fixture.Factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/ingestion-status"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Headers.Add(ApiKeyAuthentication.HeaderName, IngestionApiFixture.ApiKey);
            })
            .Build();

        connection.On<IngestionStatusEvent>(
            IngestionStatusPublisher.ClientMethod, statusEvent => received.Enqueue(statusEvent));
        await connection.StartAsync();
        return connection;
    }

    private static async Task<List<IngestionStatusEvent>> WaitForTerminalEventAsync(
        ConcurrentQueue<IngestionStatusEvent> received, Guid ingestionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var mine = received.Where(e => e.IngestionId == ingestionId).ToList();
            if (mine.Any(e => e.Stage is IngestionStages.Completed or IngestionStages.Failed))
                return mine;
            await Task.Delay(25);
        }

        Assert.Fail($"No terminal status event arrived for {ingestionId}. " +
                    $"Saw: {string.Join(", ", received.Select(e => e.Stage))}");
        throw new UnreachableException();
    }

    private static async Task<Guid> PostAsync(HttpClient client, string patientId)
    {
        var response = await client.PostAsJsonAsync("/ingestions", Payload(patientId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
    }

    private static object Payload(string patientId) => new
    {
        documentType = "SessionTranscript",
        doctorId = "doc-watching",
        patientId,
        sessionId = $"sess-{patientId}",
        sequenceNumber = 1,
        language = "en",
        transcript = Transcript,
    };
}
