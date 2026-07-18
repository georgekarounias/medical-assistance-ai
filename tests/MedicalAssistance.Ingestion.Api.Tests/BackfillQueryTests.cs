using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The backfill query. A client that drops its hub connection asks one question
/// on reconnect — "what is still running for this doctor?" — and is caught up.
/// This is what lets status events stay disposable hints: nothing replays them,
/// because REST is the source of truth by design.
/// </summary>
public class BackfillQueryTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
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
    public async Task A_reconnecting_client_sees_what_is_still_running_for_its_doctor()
    {
        var client = fixture.Factory.CreateClient();

        // Work that has already landed: the client is not resyncing this.
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var finishedId = await PostAsync(client, "doc-reconnect", "pat-finished");
        await WaitForStatusAsync(client, finishedId, "Completed");

        // Work still in flight, plus another doctor's, which must not leak in.
        Guid firstInFlight, secondInFlight, otherDoctors;
        await using (var parked = fixture.CreateFactory(new ScriptedChatClient(), workerCount: 0))
        {
            var parkedClient = parked.CreateClient();
            firstInFlight = await PostAsync(parkedClient, "doc-reconnect", "pat-running-one");
            secondInFlight = await PostAsync(parkedClient, "doc-reconnect", "pat-running-two");
            otherDoctors = await PostAsync(parkedClient, "doc-elsewhere", "pat-not-mine");
        }

        var active = await GetAsync(client, "/ingestions?doctorId=doc-reconnect&active=true");

        Assert.Equal(
            new[] { firstInFlight, secondInFlight }.Order().ToArray(),
            active.Select(i => i.IngestionId).Order().ToArray());
        Assert.DoesNotContain(active, i => i.IngestionId == finishedId);
        Assert.DoesNotContain(active, i => i.IngestionId == otherDoctors);

        // Enough to rebuild the progress list without a second round trip.
        var resumed = active.Single(i => i.IngestionId == firstInFlight);
        Assert.Equal("Queued", resumed.Status);
        Assert.Equal("pat-running-one", resumed.PatientId);
        Assert.Equal("SessionTranscript", resumed.DocumentType);
        Assert.Equal("sess-pat-running-one", resumed.SessionId);
        Assert.Equal(1, resumed.SequenceNumber);
    }

    [Fact]
    public async Task In_flight_is_what_the_query_means_by_default()
    {
        var client = fixture.Factory.CreateClient();

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var finishedId = await PostAsync(client, "doc-default", "pat-default-done");
        await WaitForStatusAsync(client, finishedId, "Completed");

        var byDefault = await GetAsync(client, "/ingestions?doctorId=doc-default");

        Assert.DoesNotContain(byDefault, i => i.IngestionId == finishedId);
    }

    [Fact]
    public async Task Asking_for_everything_includes_work_that_has_finished()
    {
        var client = fixture.Factory.CreateClient();

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var finishedId = await PostAsync(client, "doc-history", "pat-history");
        await WaitForStatusAsync(client, finishedId, "Completed");

        var everything = await GetAsync(client, "/ingestions?doctorId=doc-history&active=false");

        Assert.Contains(everything, i => i.IngestionId == finishedId && i.Status == "Completed");
    }

    [Fact]
    public async Task Asking_without_a_doctor_is_refused_by_field()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/ingestions?active=true");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("doctorId", out _));
    }

    private static async Task<List<IngestionSummaryDto>> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<IngestionSummaryDto>>())!;
    }

    private static async Task<Guid> PostAsync(HttpClient client, string doctorId, string patientId)
    {
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId,
            patientId,
            sessionId = $"sess-{patientId}",
            sequenceNumber = 1,
            language = "en",
            transcript = Transcript,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
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

    private sealed record IngestionSummaryDto(
        Guid IngestionId,
        string DocumentType,
        string PatientId,
        string? SessionId,
        int? SequenceNumber,
        string Status,
        string? ErrorMessage);
}
