using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Pgvector;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The Ingestion Strategy for SessionTranscript documents:
/// boundaries-only LLM chunking (ADR-0002) — the transcript's non-empty Lines
/// are numbered, the chunking agent proposes line ranges, blurbs, and a summary;
/// verbatim chunk text is assembled here, in code.
/// </summary>
public sealed class TranscriptIngestionStrategy
{
    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    private readonly AIAgent _chunkingAgent;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IngestionStore _store;

    public TranscriptIngestionStrategy(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IngestionStore store)
    {
        // A Microsoft Agent Framework agent wrapping whatever IChatClient is
        // configured — Azure OpenAI in production, a scripted fake in tests.
        _chunkingAgent = chatClient.AsAIAgent(
            name: "TranscriptChunker",
            instructions:
                "You segment doctor-patient session transcripts into topically coherent chunks. " +
                "You only return line boundaries and descriptions — never transcript text. " +
                "Respond with JSON only: {\"chunks\":[{\"startLine\":int,\"endLine\":int,\"contextBlurb\":string}],\"summary\":string}. " +
                "Boundaries are inclusive, contiguous, non-overlapping, and must cover every line.");
        _embeddingGenerator = embeddingGenerator;
        _store = store;
    }

    /// <summary>Runs the full strategy for one Transcript: chunk (boundaries-only) → enrich → embed (batched) → atomic store.</summary>
    public async Task IngestAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct)
    {
        var lines = SplitIntoLines(request.Transcript);
        var plan = await RequestChunkPlanAsync(lines, ct);
        var chunks = AssembleChunks(lines, plan);

        var embeddings = await _embeddingGenerator.GenerateAsync(chunks.Select(c => c.EmbeddingInput).ToList(), cancellationToken: ct);
        var records = chunks
            .Select((chunk, i) => new ChunkToStore(
                i, chunk.Kind, chunk.VerbatimText, chunk.ContextBlurb, chunk.SourceRefJson,
                new Vector(embeddings[i].Vector)))
            .ToList();

        var documentId = $"{request.SessionId}#{request.SequenceNumber}";
        await _store.CompleteWithChunksAsync(ingestionId, documentId, request, records, ct);
    }

    private static IReadOnlyList<string> SplitIntoLines(string transcript) =>
        transcript
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    private async Task<ChunkPlan> RequestChunkPlanAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        var response = await _chunkingAgent.RunAsync(BuildChunkingPrompt(lines), cancellationToken: ct);
        var json = StripCodeFences(response.Text);
        return JsonSerializer.Deserialize<ChunkPlan>(json, PlanJson)
            ?? throw new InvalidOperationException("Chunking agent returned an empty plan.");
    }

    private static string BuildChunkingPrompt(IReadOnlyList<string> lines)
    {
        var prompt = new StringBuilder("Transcript lines:\n");
        for (var i = 0; i < lines.Count; i++)
            prompt.Append($"[{i}] {lines[i]}\n");
        return prompt.ToString();
    }

    private static List<AssembledChunk> AssembleChunks(IReadOnlyList<string> lines, ChunkPlan plan)
    {
        var chunks = new List<AssembledChunk>();
        foreach (var planned in plan.Chunks)
        {
            var verbatim = string.Join("\n", lines
                .Skip(planned.StartLine)
                .Take(planned.EndLine - planned.StartLine + 1));
            chunks.Add(new AssembledChunk(
                Kind: "dialog",
                VerbatimText: verbatim,
                ContextBlurb: planned.ContextBlurb,
                SourceRefJson: $$"""{"startLine":{{planned.StartLine}},"endLine":{{planned.EndLine}}}""",
                EmbeddingInput: $"{planned.ContextBlurb}\n\n{verbatim}"));
        }

        chunks.Add(new AssembledChunk(
            Kind: "summary",
            VerbatimText: plan.Summary,
            ContextBlurb: null,
            SourceRefJson: null,
            EmbeddingInput: plan.Summary));
        return chunks;
    }

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```"))
            return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return trimmed[(firstNewline + 1)..lastFence].Trim();
    }

    private sealed record ChunkPlan(List<PlannedChunk> Chunks, string Summary);

    private sealed record PlannedChunk(int StartLine, int EndLine, string ContextBlurb);

    private sealed record AssembledChunk(
        string Kind, string VerbatimText, string? ContextBlurb, string? SourceRefJson, string EmbeddingInput);
}
