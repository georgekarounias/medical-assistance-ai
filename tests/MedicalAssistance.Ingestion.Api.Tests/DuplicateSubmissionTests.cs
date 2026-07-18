using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The platform dedup rule: content that is already ingested for a patient is
/// never ingested again, whatever session or sequence number it arrives under.
/// An identical re-POST after success is a no-op; after failure it is a retry.
/// A doctor's double-click, a client's network retry, and a re-upload filed
/// under a fresh sequence number all have to be free.
///
/// Each test uses its own patientId, because dedup is patient-scoped and tests
/// would otherwise deduplicate against each other.
/// </summary>
public class DuplicateSubmissionTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
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

    private const string InvalidPlanWithGap = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 0, "contextBlurb": "Opening." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Advice." }
          ],
          "summary": "Session summary."
        }
        """;

    [Fact]
    public async Task Re_posting_a_completed_document_returns_the_same_id_and_reprocesses_nothing()
    {
        var client = fixture.Factory.CreateClient();
        var payload = TranscriptPayload("pat-completed", sequenceNumber: 1, Transcript);

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var first = await PostAsync(client, payload);
        Assert.False(first.Duplicate);
        await WaitForStatusAsync(client, first.Id, "Completed");

        var second = await PostAsync(client, payload);

        Assert.Equal(first.Id, second.Id);
        Assert.True(second.Duplicate);
        await AssertNothingWasReprocessedAsync(client, first.Id);
    }

    [Fact]
    public async Task Re_posting_a_failed_document_retries_it_on_the_same_ingestion()
    {
        var client = fixture.Factory.CreateClient();
        var payload = TranscriptPayload("pat-failed", sequenceNumber: 1, Transcript);

        // Two bad plans in a row is an honest failure, not a duplicate.
        fixture.ChatClient.EnqueueResponse(InvalidPlanWithGap);
        fixture.ChatClient.EnqueueResponse(InvalidPlanWithGap);
        var first = await PostAsync(client, payload);
        await WaitForStatusAsync(client, first.Id, "Failed");
        var ingestionsAfterFirst = await CountAsync("ingestions");

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var second = await PostAsync(client, payload);

        // The same ingestion is tried again — the id the caller already holds
        // stays valid, and a poison document cannot multiply rows.
        Assert.Equal(first.Id, second.Id);
        Assert.False(second.Duplicate);
        await WaitForStatusAsync(client, second.Id, "Completed");
        Assert.Equal(ingestionsAfterFirst, await CountAsync("ingestions"));
    }

    [Fact]
    public async Task Re_posting_a_failed_document_a_correction_replaced_ingests_it_as_new_work()
    {
        var client = fixture.Factory.CreateClient();
        const string patientId = "pat-failed-then-corrected";
        var original = TranscriptPayload(patientId, sequenceNumber: 1, Transcript);

        fixture.ChatClient.EnqueueResponse(InvalidPlanWithGap);
        fixture.ChatClient.EnqueueResponse(InvalidPlanWithGap);
        var failed = await PostAsync(client, original);
        await WaitForStatusAsync(client, failed.Id, "Failed");

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var corrected = await PostAsync(client, TranscriptPayload(patientId, sequenceNumber: 1, """
            Doctor: Good morning, what brings you in today?
            Patient: I keep waking up with migraines, not headaches.
            Doctor: How much water do you usually drink?
            Patient: Maybe two glasses a day.
            """));
        await WaitForStatusAsync(client, corrected.Id, "Completed");

        // Sending the original again is a doctor saying this text is what the
        // record should hold — new work, not the revival of a run the document
        // has moved past, so it gets its own ingestion rather than reusing the
        // failed one and quietly reverting the correction.
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var reposted = await PostAsync(client, original);

        Assert.NotEqual(failed.Id, reposted.Id);
        Assert.False(reposted.Duplicate);
        await WaitForStatusAsync(client, reposted.Id, "Completed");

        var chunks = await ReadDocumentChunksAsync(patientId, sequenceNumber: 1);
        Assert.Contains(chunks, text => text.Contains("one glass a day"));
        Assert.DoesNotContain(chunks, text => text.Contains("migraines"));
        Assert.Equal("Superseded", await ReadStatusAsync(client, corrected.Id));
        Assert.Equal("Failed", await ReadStatusAsync(client, failed.Id));
    }

    [Fact]
    public async Task The_same_content_filed_under_a_new_sequence_number_is_skipped()
    {
        var client = fixture.Factory.CreateClient();

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var original = await PostAsync(client, TranscriptPayload("pat-resequenced", sequenceNumber: 1, Transcript));
        await WaitForStatusAsync(client, original.Id, "Completed");

        // Same recording, re-uploaded as if it were a continuation. Ingesting it
        // would put the same passages in the patient's record twice and let the
        // chat quote one encounter as though it were two.
        var refiled = await PostAsync(client, TranscriptPayload("pat-resequenced", sequenceNumber: 2, Transcript));

        Assert.Equal(original.Id, refiled.Id);
        Assert.True(refiled.Duplicate);
        await AssertNothingWasReprocessedAsync(client, original.Id);
    }

    [Fact]
    public async Task The_same_content_filed_under_a_new_session_is_skipped()
    {
        var client = fixture.Factory.CreateClient();

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var original = await PostAsync(client, TranscriptPayload("pat-resessioned", sequenceNumber: 1, Transcript));
        await WaitForStatusAsync(client, original.Id, "Completed");

        var refiled = await PostAsync(
            client, TranscriptPayload("pat-resessioned", sequenceNumber: 1, Transcript, sessionId: "sess-second"));

        Assert.Equal(original.Id, refiled.Id);
        Assert.True(refiled.Duplicate);
        await AssertNothingWasReprocessedAsync(client, original.Id);
    }

    [Fact]
    public async Task The_same_content_for_a_different_patient_is_ingested_not_skipped()
    {
        var client = fixture.Factory.CreateClient();

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var first = await PostAsync(client, TranscriptPayload("pat-shared-a", sequenceNumber: 1, Transcript));
        await WaitForStatusAsync(client, first.Id, "Completed");

        // Dedup is a per-patient rule. Skipping here would silently leave the
        // second patient's record missing a document that was accepted.
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var second = await PostAsync(client, TranscriptPayload("pat-shared-b", sequenceNumber: 1, Transcript));

        Assert.NotEqual(first.Id, second.Id);
        Assert.False(second.Duplicate);
        await WaitForStatusAsync(client, second.Id, "Completed");
        Assert.Equal(3, await CountChunksAsync(second.Id)); // two dialog chunks and the summary
    }

    [Fact]
    public async Task Re_posting_the_same_identity_with_different_content_is_not_a_duplicate()
    {
        var client = fixture.Factory.CreateClient();

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var first = await PostAsync(client, TranscriptPayload("pat-corrected", sequenceNumber: 1, Transcript));
        await WaitForStatusAsync(client, first.Id, "Completed");

        fixture.ChatClient.EnqueueResponse("""
            {
              "chunks": [ { "startLine": 0, "endLine": 1, "contextBlurb": "Blood results reviewed." } ],
              "summary": "Iron levels discussed."
            }
            """);
        var corrected = await PostAsync(client, TranscriptPayload("pat-corrected", sequenceNumber: 1, """
            Doctor: Let's review the blood test results from last week.
            Patient: I was told my iron levels were low.
            """));

        // Same identity, different content is a Correction, not a no-op: it gets
        // its own ingestion and runs. (Superseding the old chunks is T16.)
        Assert.NotEqual(first.Id, corrected.Id);
        Assert.False(corrected.Duplicate);
        await WaitForStatusAsync(client, corrected.Id, "Completed");
    }

    /// <summary>
    /// Asserts a skipped submission cost nothing: no model call, no new rows,
    /// and the original ingestion still standing. No scripted chat response is
    /// queued for these submissions, so any processing at all would drive the
    /// chat fake to throw and the ingestion to Failed.
    /// </summary>
    private async Task AssertNothingWasReprocessedAsync(HttpClient client, Guid originalId)
    {
        var prompts = fixture.ChatClient.ReceivedPrompts.Count;
        var ingestions = await CountAsync("ingestions");
        var chunks = await CountAsync("chunks");

        await Task.Delay(300); // give any wrongly-queued work time to show itself

        Assert.Equal("Completed", await ReadStatusAsync(client, originalId));
        Assert.Equal(prompts, fixture.ChatClient.ReceivedPrompts.Count);
        Assert.Equal(ingestions, await CountAsync("ingestions"));
        Assert.Equal(chunks, await CountAsync("chunks"));
    }

    private static object TranscriptPayload(
        string patientId, int sequenceNumber, string transcript, string sessionId = "sess-dedup") => new
    {
        documentType = "SessionTranscript",
        doctorId = "doc-1",
        patientId,
        sessionId,
        sequenceNumber,
        language = "en",
        transcript,
    };

    private static async Task<(Guid Id, bool Duplicate)> PostAsync(HttpClient client, object payload)
    {
        var response = await client.PostAsJsonAsync("/ingestions", payload);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("ingestionId").GetGuid(), body.GetProperty("duplicate").GetBoolean());
    }

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
            if (lastSeen is "Completed" or "Failed")
                break;
            await Task.Delay(100);
        }
        Assert.Fail($"Ingestion {ingestionId} was {lastSeen}, expected {expected}.");
    }

    /// <summary>
    /// The chunk texts a document currently holds. Scoped by patient as well as
    /// document id, because the tests here deliberately share a session id.
    /// </summary>
    private async Task<List<string>> ReadDocumentChunksAsync(string patientId, int sequenceNumber)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT verbatim_text FROM chunks WHERE document_id = $1 AND patient_id = $2 ORDER BY chunk_index",
            connection);
        command.Parameters.AddWithValue($"sess-dedup#{sequenceNumber}");
        command.Parameters.AddWithValue(patientId);

        var texts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            texts.Add(reader.GetString(0));
        return texts;
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
