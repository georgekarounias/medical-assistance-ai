using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The knowledge-store isolation guarantee: a vector similarity search is always
/// scoped, so one patient's chunks can never surface in another patient's
/// retrieval — even when the text is word-for-word identical — and a patient seen
/// by two doctors can be narrowed to just one doctor's documents.
///
/// Isolation is a property of the query's WHERE clause, not of the ANN index (the
/// HNSW index spans every patient and only makes the search fast). These tests run
/// the exact retrieval shape a RAG chat will use — filter by patient_id, optionally
/// by doctor_id, then order by cosine distance over the halfvec cast — and assert
/// nothing outside the scope comes back.
/// </summary>
public class VectorRetrievalIsolationTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    // Identical text for two different patients, so isolation is the only thing
    // that can keep them apart — content alone cannot.
    private const string SharedTranscript = """
        Doctor: What brings you in today?
        Patient: I have had a sore throat for a week.
        Doctor: Any fever with it?
        Patient: A mild one in the evenings.
        """;

    private const string OtherTranscript = """
        Doctor: How is the knee since the injection?
        Patient: Much better, I can climb stairs again.
        Doctor: Any swelling left?
        Patient: Only after a long walk.
        """;

    private const string TwoChunkPlan = """
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Opening of the visit." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Detail gathered." }
          ],
          "summary": "A short clinical exchange."
        }
        """;

    [Fact]
    public async Task A_patients_vector_search_never_returns_another_patients_chunks()
    {
        var client = fixture.Factory.CreateClient();

        // Two patients of the same doctor, submitting the very same transcript.
        await IngestAsync(client, doctorId: "doc-iso", patientId: "iso-alice", SharedTranscript);
        await IngestAsync(client, doctorId: "doc-iso", patientId: "iso-bob", SharedTranscript);

        var alice = await SearchAsync("iso-alice", "sore throat and fever");
        var bob = await SearchAsync("iso-bob", "sore throat and fever");

        // Each patient sees their own three chunks (two + summary) and nothing else,
        // though the stored text is identical across the two.
        Assert.Equal(3, alice.Count);
        Assert.All(alice, hit => Assert.Equal("iso-alice", hit.PatientId));
        Assert.DoesNotContain(alice, hit => hit.PatientId == "iso-bob");

        Assert.Equal(3, bob.Count);
        Assert.All(bob, hit => Assert.Equal("iso-bob", hit.PatientId));
        Assert.DoesNotContain(bob, hit => hit.PatientId == "iso-alice");
    }

    [Fact]
    public async Task A_patient_seen_by_two_doctors_can_be_scoped_to_one_doctor()
    {
        var client = fixture.Factory.CreateClient();

        // One patient, two doctors, two different documents.
        await IngestAsync(client, doctorId: "dr-smith", patientId: "iso-carol", SharedTranscript, session: "s-smith");
        await IngestAsync(client, doctorId: "dr-jones", patientId: "iso-carol", OtherTranscript, session: "s-jones");

        // Scoped to one doctor: only that doctor's chunks come back.
        var smithOnly = await SearchAsync("iso-carol", "sore throat", doctorId: "dr-smith");
        Assert.Equal(3, smithOnly.Count);
        Assert.All(smithOnly, hit => Assert.Equal("dr-smith", hit.DoctorId));
        Assert.DoesNotContain(smithOnly, hit => hit.DoctorId == "dr-jones");

        // Unscoped by doctor: the patient's whole record across both doctors — proof
        // that the doctor filter is what narrows it, not a lack of the other rows.
        var wholeRecord = await SearchAsync("iso-carol", "sore throat");
        Assert.Equal(6, wholeRecord.Count);
        Assert.Contains(wholeRecord, hit => hit.DoctorId == "dr-smith");
        Assert.Contains(wholeRecord, hit => hit.DoctorId == "dr-jones");
    }

    private async Task IngestAsync(
        HttpClient client, string doctorId, string patientId, string transcript, string? session = null)
    {
        fixture.ChatClient.EnqueueResponse(TwoChunkPlan);
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId,
            patientId,
            sessionId = session ?? $"sess-{patientId}",
            sequenceNumber = 1,
            language = "en",
            transcript,
        });
        response.EnsureSuccessStatusCode();
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
        await WaitForStatusAsync(client, ingestionId, "Completed");
    }

    // The retrieval query the RAG chat will run: scoped to one patient (and
    // optionally one doctor), ranked by cosine distance over the halfvec cast the
    // HNSW index is built on.
    private async Task<List<SearchHit>> SearchAsync(string patientId, string question, string? doctorId = null)
    {
        var embedding = await new DeterministicEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions)
            .GenerateAsync([question]);
        var queryVector = "[" + string.Join(
            ",", embedding[0].Vector.ToArray().Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

        var doctorFilter = doctorId is null ? "" : "AND doctor_id = $3";
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT document_id, patient_id, doctor_id, verbatim_text
            FROM chunks
            WHERE patient_id = $1 {doctorFilter}
            ORDER BY embedding::halfvec(3072) <=> $2::halfvec(3072)
            LIMIT 50
            """,
            connection);
        command.Parameters.AddWithValue(patientId);
        command.Parameters.AddWithValue(queryVector);
        if (doctorId is not null)
            command.Parameters.AddWithValue(doctorId);

        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            hits.Add(new SearchHit(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return hits;
    }

    private static async Task WaitForStatusAsync(HttpClient client, Guid ingestionId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var lastSeen = "<never fetched>";
        while (DateTime.UtcNow < deadline)
        {
            lastSeen = (await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}"))
                .GetProperty("status").GetString()!;
            if (lastSeen == expected)
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Ingestion {ingestionId} never reached {expected}. Last: {lastSeen}");
    }

    private sealed record SearchHit(string DocumentId, string PatientId, string DoctorId, string Text);
}
