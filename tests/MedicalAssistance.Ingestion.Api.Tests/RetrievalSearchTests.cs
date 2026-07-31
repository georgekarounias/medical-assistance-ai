using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Retrieval;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The retrieval search step (T41): the query is embedded with the same generator
/// ingestion used, and the ANN scan runs over the authoritative chunks — scoped to
/// the patient, narrowed by the optional filters in the same WHERE, ordered by
/// cosine distance over the halfvec cast, and packaged as Evidence Items.
///
/// Chunks under test are created by ingesting real documents through the existing
/// pipeline, so the rows are genuine. The one difference from the isolation tests
/// is the embedding seam: a <see cref="ControllableEmbeddingGenerator"/> with pinned
/// vectors (T39), so ranking order is exactly what each test dictates rather than an
/// artefact of a hash. Retrieval is driven through the internal
/// <see cref="IRetrievalService"/> — the HTTP endpoint arrives in T42.
/// </summary>
public class RetrievalSearchTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    // Vectors live in a tiny semantic plane: the query points along the first axis,
    // so a chunk's closeness is set by how much of it lies on that axis.
    private static readonly float[] Query = [1f, 0f];
    private static readonly float[] Near = [0.98f, 0.02f];
    private static readonly float[] Middling = [0.6f, 0.8f];
    private static readonly float[] Orthogonal = [0f, 1f]; // ~zero similarity to the query

    [Fact]
    public async Task Search_is_patient_scoped_ranks_by_similarity_and_packages_provenance()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "Does this patient take insulin for their diabetes?";
        emb.Pin(question, Query);

        const string aliceNearLine = "The patient reports taking insulin injections every single day for their diabetes.";
        const string aliceFarLine = "Blood pressure this morning measured one twenty over eighty which is entirely normal.";
        const string bobLine = "The patient describes a dull headache that has persisted for the last three days now.";

        // Alice: a near chunk and a middling one, each with an orthogonal summary.
        await IngestTranscriptAsync(client, chat, emb, "ret-alice", "dr-a", "alice-s1",
            aliceNearLine, "Diabetes management.", "Alice first visit summary.", Near, Orthogonal);
        await IngestTranscriptAsync(client, chat, emb, "ret-alice", "dr-a", "alice-s2",
            aliceFarLine, "Vitals check.", "Alice second visit summary.", Middling, Orthogonal);
        // Bob: identical scope shape but a different patient, pinned CLOSEST of all —
        // so only the patient_id boundary can keep him out of Alice's results.
        await IngestTranscriptAsync(client, chat, emb, "ret-bob", "dr-a", "bob-s1",
            bobLine, "Headache.", "Bob summary.", Query, Orthogonal);

        var alice = await SearchAsync(factory, new RetrievalRequest
        {
            PatientId = "ret-alice", Question = question, TopK = 8,
        });

        // Two documents × (body + summary) = four chunks, and Bob is nowhere in them
        // despite being the closest match — the boundary held.
        Assert.Equal(4, alice.Count);
        Assert.DoesNotContain(alice, e => e.VerbatimText.Contains("headache", StringComparison.OrdinalIgnoreCase));

        // Ranked: the near dialog chunk first, then the middling one, with the
        // orthogonal summaries behind them.
        var top = alice[0];
        Assert.Equal(aliceNearLine, top.VerbatimText);
        Assert.Equal(aliceFarLine, alice[1].VerbatimText);
        Assert.True(top.Score > alice[1].Score, $"near {top.Score} should outrank middling {alice[1].Score}");
        Assert.True(alice[1].Score > alice[2].Score, "the dialog chunks should outrank the orthogonal summaries");

        // Provenance is packaged from the row, not hydrated from elsewhere.
        Assert.NotEqual(Guid.Empty, top.ChunkId);
        Assert.False(string.IsNullOrEmpty(top.DocumentId));
        Assert.Equal("SessionTranscript", top.DocumentType);
        Assert.Equal("alice-s1", top.SessionId);
        Assert.Equal(0, top.ChunkIndex);
        Assert.Equal("dialog", top.ChunkKind);
        Assert.Equal("en", top.Language);
        // source_ref comes back as Postgres-normalised jsonb (keys sorted, spaced),
        // so assert on the parsed value rather than the exact string.
        Assert.NotNull(top.SourceRef);
        using var sourceRef = JsonDocument.Parse(top.SourceRef!);
        Assert.Equal(0, sourceRef.RootElement.GetProperty("startLine").GetInt32());
        Assert.True(top.Score > 0.9, $"the near chunk's score {top.Score} should be close to 1");
        Assert.Contains(alice, e => e.ChunkKind == "summary");

        // The boundary cuts the other way too: Bob's own search sees Bob.
        var bob = await SearchAsync(factory, new RetrievalRequest { PatientId = "ret-bob", Question = question });
        Assert.Contains(bob, e => e.VerbatimText == bobLine);
    }

    [Fact]
    public async Task An_unknown_patient_returns_no_evidence()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);

        const string question = "Anything at all about this patient?";
        emb.Pin(question, Query);

        // No existence check, no 404 here — an empty result the answer path reads as
        // insufficient evidence (the refusal precondition, wired in T45).
        var evidence = await SearchAsync(factory, new RetrievalRequest
        {
            PatientId = "nobody-here", Question = question,
        });

        Assert.Empty(evidence);
    }

    [Fact]
    public async Task Filters_narrow_within_the_patient_in_the_same_query()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "What is the latest on this patient?";
        emb.Pin(question, Query);

        const string transcriptLine = "The consultation covered the patient's ongoing management of chronic hypertension.";
        const string noteLine = "Follow up note regarding the patient's recent adjustment to their medication schedule.";

        // One patient, two doctors, two document types, one session on the transcript.
        await IngestTranscriptAsync(client, chat, emb, "ret-carol", "dr-x", "carol-s1",
            transcriptLine, "Consultation.", "Transcript summary.", Near, Orthogonal);
        await IngestNoteAsync(client, chat, emb, "ret-carol", "dr-y", "carol-note-1",
            noteLine, "Note.", "Note summary.", Near, Orthogonal);

        // No filter: the whole record — both documents' body + summary.
        var all = await SearchAsync(factory, new RetrievalRequest { PatientId = "ret-carol", Question = question, TopK = 8 });
        Assert.Contains(all, e => e.VerbatimText == transcriptLine);
        Assert.Contains(all, e => e.VerbatimText == noteLine);

        // doctorId filter — only dr-x's transcript.
        var byDoctor = await SearchAsync(factory, new RetrievalRequest
        {
            PatientId = "ret-carol", Question = question, TopK = 8,
            Filters = new RetrievalFilters { DoctorId = "dr-x" },
        });
        Assert.Contains(byDoctor, e => e.VerbatimText == transcriptLine);
        Assert.DoesNotContain(byDoctor, e => e.VerbatimText == noteLine);

        // documentType filter — only the note.
        var byType = await SearchAsync(factory, new RetrievalRequest
        {
            PatientId = "ret-carol", Question = question, TopK = 8,
            Filters = new RetrievalFilters { DocumentType = "DoctorNote" },
        });
        Assert.All(byType, e => Assert.Equal("DoctorNote", e.DocumentType));
        Assert.Contains(byType, e => e.VerbatimText == noteLine);
        Assert.DoesNotContain(byType, e => e.VerbatimText == transcriptLine);

        // sessionId filter — only the transcript (the note has no session).
        var bySession = await SearchAsync(factory, new RetrievalRequest
        {
            PatientId = "ret-carol", Question = question, TopK = 8,
            Filters = new RetrievalFilters { SessionId = "carol-s1" },
        });
        Assert.Contains(bySession, e => e.VerbatimText == transcriptLine);
        Assert.DoesNotContain(bySession, e => e.VerbatimText == noteLine);
    }

    [Fact]
    public async Task The_clinical_date_range_filter_narrows_by_document_date()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "Summarise this patient's year.";
        emb.Pin(question, Query);

        const string janLine = "January visit where the patient first presented with mild seasonal cold symptoms.";
        const string aprLine = "April visit reviewing the patient's steady progress and generally improving health.";
        const string julLine = "July visit confirming the patient has fully recovered with no remaining concerns.";
        var date = (int month) => new DateTimeOffset(2026, month, 15, 0, 0, 0, TimeSpan.Zero);

        await IngestTranscriptAsync(client, chat, emb, "ret-dave", "dr-d", "dave-s1",
            janLine, "Jan.", "Jan summary.", Near, Orthogonal, date: date(1));
        await IngestTranscriptAsync(client, chat, emb, "ret-dave", "dr-d", "dave-s2",
            aprLine, "Apr.", "Apr summary.", Near, Orthogonal, date: date(4));
        await IngestTranscriptAsync(client, chat, emb, "ret-dave", "dr-d", "dave-s3",
            julLine, "Jul.", "Jul summary.", Near, Orthogonal, date: date(7));

        // A spring window catches only the April visit.
        var spring = await SearchAsync(factory, new RetrievalRequest
        {
            PatientId = "ret-dave", Question = question, TopK = 8,
            Filters = new RetrievalFilters { From = date(3), To = date(6) },
        });

        Assert.Contains(spring, e => e.VerbatimText == aprLine);
        Assert.DoesNotContain(spring, e => e.VerbatimText == janLine);
        Assert.DoesNotContain(spring, e => e.VerbatimText == julLine);
    }

    [Fact]
    public async Task TopK_limits_the_result_count_and_clamps_below_one()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "Give me everything on this patient.";
        emb.Pin(question, Query);

        // Three documents → six chunks, all pinned near so all would qualify.
        for (var i = 1; i <= 3; i++)
            await IngestTranscriptAsync(client, chat, emb, "ret-erin", "dr-e", $"erin-s{i}",
                $"Visit number {i} covering the patient's routine follow up and current status.",
                $"Visit {i}.", $"Visit {i} summary.", Near, Orthogonal);

        // TopK caps the rows returned.
        var two = await SearchAsync(factory, new RetrievalRequest { PatientId = "ret-erin", Question = question, TopK = 2 });
        Assert.Equal(2, two.Count);

        // A non-positive TopK is clamped up to one rather than returning nothing.
        var clampedLow = await SearchAsync(factory, new RetrievalRequest { PatientId = "ret-erin", Question = question, TopK = 0 });
        Assert.Single(clampedLow);
    }

    private static async Task<IReadOnlyList<EvidenceItem>> SearchAsync(
        WebApplicationFactory<Program> factory, RetrievalRequest request)
    {
        using var scope = factory.Services.CreateScope();
        var retrieval = scope.ServiceProvider.GetRequiredService<IRetrievalService>();
        var result = await retrieval.SearchAsync(request);
        return result.Evidence;
    }

    private async Task IngestTranscriptAsync(
        HttpClient client, ScriptedChatClient chat, ControllableEmbeddingGenerator emb,
        string patientId, string doctorId, string sessionId, string line, string blurb, string summary,
        float[] bodyVector, float[] summaryVector, string language = "en", DateTimeOffset? date = null)
    {
        PinChunkVectors(emb, line, blurb, summary, bodyVector, summaryVector);
        chat.EnqueueResponse(OneChunkPlan(blurb, summary));
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId,
            patientId,
            sessionId,
            sequenceNumber = 1,
            language,
            sessionDate = date,
            transcript = line,
        });
        await CompleteAsync(client, response);
    }

    private async Task IngestNoteAsync(
        HttpClient client, ScriptedChatClient chat, ControllableEmbeddingGenerator emb,
        string patientId, string doctorId, string noteId, string line, string blurb, string summary,
        float[] bodyVector, float[] summaryVector, string language = "en")
    {
        PinChunkVectors(emb, line, blurb, summary, bodyVector, summaryVector);
        chat.EnqueueResponse(OneChunkPlan(blurb, summary));
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "DoctorNote",
            doctorId,
            patientId,
            noteId,
            language,
            text = line,
        });
        await CompleteAsync(client, response);
    }

    // The prose pipeline embeds "{blurb}\n\n{verbatim}" for a body chunk and the raw
    // summary text for the summary chunk (ProseIngestionPipeline.AssembleChunks), so
    // pinning those exact strings fixes each chunk's vector.
    private static void PinChunkVectors(
        ControllableEmbeddingGenerator emb, string line, string blurb, string summary,
        float[] bodyVector, float[] summaryVector)
    {
        emb.Pin($"{blurb}\n\n{line}", bodyVector);
        emb.Pin(summary, summaryVector);
    }

    private static string OneChunkPlan(string blurb, string summary) =>
        $$"""
        { "chunks": [ { "startLine": 0, "endLine": 0, "contextBlurb": {{JsonSerializer.Serialize(blurb)}} } ],
          "summary": {{JsonSerializer.Serialize(summary)}} }
        """;

    private static async Task CompleteAsync(HttpClient client, HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var lastSeen = "<never fetched>";
        while (DateTime.UtcNow < deadline)
        {
            lastSeen = (await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}"))
                .GetProperty("status").GetString()!;
            if (lastSeen == "Completed")
                return;
            Assert.NotEqual("Failed", lastSeen);
            await Task.Delay(50);
        }
        Assert.Fail($"Ingestion {ingestionId} never reached Completed. Last: {lastSeen}");
    }
}
