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
        var ingestionId = await PostTranscriptAsync(client, patientId: "pat-retry");
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
        var ingestionId = await PostTranscriptAsync(client, patientId: "pat-twice-invalid");
        var (status, detail) = await WaitForTerminalStatusAsync(client, ingestionId);

        Assert.True(status == "Failed", $"Expected Failed but got: {detail}");
        Assert.Contains("invalid chunk plan", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contiguous", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CountChunksAsync(ingestionId));
    }

    /// <summary>
    /// A response cut off at the output-token limit: an opening code fence, and
    /// nothing closing it. More likely on exactly the long transcripts this
    /// service exists to handle.
    /// </summary>
    private const string TruncatedFencedPlan = """
        ```json
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Complaint discu
        """;

    [Fact]
    public async Task A_truncated_response_gets_the_same_corrective_retry_as_any_other_bad_answer()
    {
        var promptsBefore = fixture.ChatClient.ReceivedPrompts.Count;
        fixture.ChatClient.EnqueueResponse(TruncatedFencedPlan);
        fixture.ChatClient.EnqueueResponse(ValidPlan);

        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostTranscriptAsync(client, patientId: "pat-truncated");
        var (status, detail) = await WaitForTerminalStatusAsync(client, ingestionId);

        // An unreadable answer is an unreadable answer, however it got that way.
        // It must reach the retry rather than escaping as an exception from the
        // string handling, which would skip the one correction the design allows.
        Assert.True(status == "Completed", $"Expected Completed but got: {detail}");
        Assert.Equal(promptsBefore + 2, fixture.ChatClient.ReceivedPrompts.Count);
        Assert.Contains("invalid", fixture.ChatClient.ReceivedPrompts[^1], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, await CountChunksAsync(ingestionId));
    }

    [Fact]
    public async Task Two_truncated_responses_fail_with_a_reason_that_names_the_model_not_a_string_index()
    {
        fixture.ChatClient.EnqueueResponse(TruncatedFencedPlan);
        fixture.ChatClient.EnqueueResponse(TruncatedFencedPlan);

        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostTranscriptAsync(client, patientId: "pat-truncated-twice");
        var (status, detail) = await WaitForTerminalStatusAsync(client, ingestionId);

        Assert.True(status == "Failed", $"Expected Failed but got: {detail}");
        Assert.Contains("invalid chunk plan", detail, StringComparison.OrdinalIgnoreCase);

        // The old failure said "length ('-8') must be a non-negative value",
        // which sends whoever reads it looking in the wrong place entirely.
        Assert.DoesNotContain("non-negative", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CountChunksAsync(ingestionId));
    }

    /// <summary>
    /// A well-formed plan whose closing fence the model simply forgot. Nothing is
    /// missing but the fence, so the plan is recoverable and ought to be used.
    /// </summary>
    [Fact]
    public async Task A_plan_missing_only_its_closing_fence_is_still_read()
    {
        var promptsBefore = fixture.ChatClient.ReceivedPrompts.Count;
        fixture.ChatClient.EnqueueResponse($"```json\n{ValidPlan}");

        var client = fixture.Factory.CreateClient();
        var ingestionId = await PostTranscriptAsync(client, patientId: "pat-unclosed-fence");
        var (status, detail) = await WaitForTerminalStatusAsync(client, ingestionId);

        Assert.True(status == "Completed", $"Expected Completed but got: {detail}");
        Assert.Equal(promptsBefore + 1, fixture.ChatClient.ReceivedPrompts.Count); // no retry needed
        Assert.Equal(3, await CountChunksAsync(ingestionId));
    }

    // Each test uses its own patient: one patient's identical content is
    // deduplicated across sessions, and these tests need real ingestions.
    private static async Task<Guid> PostTranscriptAsync(HttpClient client, string patientId)
    {
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "doc-1",
            patientId,
            sessionId = "sess-guard",
            sequenceNumber = 1,
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
