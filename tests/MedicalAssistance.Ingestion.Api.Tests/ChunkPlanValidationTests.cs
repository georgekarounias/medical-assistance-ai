using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

public class ChunkPlanValidationTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string InvalidPlanWithGap = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 0, "contextBlurb": "Opening." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Advice." }
          ],
          "summary": "Session summary."
        }
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
    public async Task Invalid_plan_gets_one_corrective_retry_naming_the_violation_then_succeeds()
    {
        var promptsBefore = fixture.ChatClient.ReceivedPrompts.Count;
        fixture.ChatClient.EnqueueResponse(InvalidPlanWithGap);
        fixture.ChatClient.EnqueueResponse(ValidPlan);

        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostTranscriptAsync(client, sequenceNumber: 1);
        var (status, detail) = await WaitForTerminalStatusAsync(client, ingestionId);

        Assert.True(status == "Completed", $"Expected Completed but got: {detail}");
        Assert.Equal(promptsBefore + 2, fixture.ChatClient.ReceivedPrompts.Count);
        var retryPrompt = fixture.ChatClient.ReceivedPrompts[^1];
        Assert.Contains("invalid", retryPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contiguous", retryPrompt, StringComparison.OrdinalIgnoreCase);

        // The stored chunks come from the corrected plan, not the invalid one.
        Assert.Equal(3, await CountChunksAsync(ingestionId));
    }

    [Fact]
    public async Task Two_invalid_plans_fail_the_ingestion_with_the_violation_and_store_nothing()
    {
        fixture.ChatClient.EnqueueResponse(InvalidPlanWithGap);
        fixture.ChatClient.EnqueueResponse(InvalidPlanWithGap);

        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostTranscriptAsync(client, sequenceNumber: 2);
        var (status, detail) = await WaitForTerminalStatusAsync(client, ingestionId);

        Assert.True(status == "Failed", $"Expected Failed but got: {detail}");
        Assert.Contains("invalid chunk plan", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contiguous", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CountChunksAsync(ingestionId));
    }

    private static async Task<Guid> PostTranscriptAsync(HttpClient client, int sequenceNumber)
    {
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "doc-1",
            patientId = "pat-guard",
            sessionId = "sess-guard",
            sequenceNumber,
            language = "en",
            transcript = """
                Doctor: Good morning, what brings you in today?
                Patient: I keep waking up with headaches.
                Doctor: How much water do you usually drink?
                Patient: Maybe one glass a day.
                """,
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

    private async Task<long> CountChunksAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM chunks WHERE ingestion_id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
