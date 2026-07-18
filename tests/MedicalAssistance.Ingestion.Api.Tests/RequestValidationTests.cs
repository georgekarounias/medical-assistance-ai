using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The submission contract, enforced at the door: a bad payload comes back as a
/// 400 naming the offending fields, and — the property that matters downstream —
/// never becomes an ingestion row that some worker has to fail later.
/// </summary>
public class RequestValidationTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string ValidTranscript = """
        Doctor: Good morning, what brings you in today?
        Patient: I keep waking up with headaches.
        """;

    [Fact]
    public async Task An_unsupported_document_type_is_rejected_and_names_what_is_supported()
    {
        var errors = await AssertRejectedAsync(new
        {
            documentType = "LabResult",
            doctorId = "doc-1",
            patientId = "pat-validation",
            sessionId = "sess-validation",
            sequenceNumber = 1,
            transcript = ValidTranscript,
        });

        Assert.Contains("SessionTranscript", MessageFor(errors, "documentType"));
    }

    [Fact]
    public async Task A_transcript_missing_its_session_identity_is_rejected_field_by_field()
    {
        var errors = await AssertRejectedAsync(new
        {
            documentType = "SessionTranscript",
            doctorId = "doc-1",
            patientId = "pat-validation",
            transcript = ValidTranscript,
        });

        // Both halves of a transcript's identity are reported at once, so the
        // caller fixes the payload in one round trip rather than two.
        Assert.NotEmpty(MessageFor(errors, "sessionId"));
        Assert.NotEmpty(MessageFor(errors, "sequenceNumber"));
    }

    [Fact]
    public async Task A_transcript_that_is_blank_is_rejected()
    {
        var errors = await AssertRejectedAsync(new
        {
            documentType = "SessionTranscript",
            doctorId = "doc-1",
            patientId = "pat-validation",
            sessionId = "sess-validation",
            sequenceNumber = 1,
            transcript = "   \n  \n ",
        });

        Assert.NotEmpty(MessageFor(errors, "transcript"));
    }

    [Fact]
    public async Task A_submission_with_no_clinical_identifiers_reports_every_missing_field_at_once()
    {
        var errors = await AssertRejectedAsync(new { transcript = ValidTranscript });

        Assert.NotEmpty(MessageFor(errors, "documentType"));
        Assert.NotEmpty(MessageFor(errors, "doctorId"));
        Assert.NotEmpty(MessageFor(errors, "patientId"));
    }

    /// <summary>
    /// Posts a payload, asserts it was rejected with field-level errors, and
    /// asserts no ingestion row was created — then hands back the error map.
    /// </summary>
    private async Task<JsonElement> AssertRejectedAsync(object payload)
    {
        var ingestionsBefore = await CountIngestionsAsync();

        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/ingestions", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ingestionsBefore, await CountIngestionsAsync());

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("errors");
    }

    private static string MessageFor(JsonElement errors, string field)
    {
        Assert.True(errors.TryGetProperty(field, out var messages), $"Expected an error for '{field}' in: {errors}");
        return string.Join(" ", messages.EnumerateArray().Select(m => m.GetString()));
    }

    private async Task<long> CountIngestionsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM ingestions", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
