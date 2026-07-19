using System.Text;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Realtime;
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
    private readonly int _instructionVersion;
    private readonly string _chatModel;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IngestionStore _store;
    private readonly ChunkSizeGuardrails _sizeGuardrails;

    private readonly IngestionStatusPublisher _statusPublisher;

    public TranscriptIngestionStrategy(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IngestionStore store,
        AgentInstructionProvider instructionProvider,
        IngestionStatusPublisher statusPublisher,
        IConfiguration configuration)
    {
        _statusPublisher = statusPublisher;
        // A Microsoft Agent Framework agent wrapping whatever IChatClient is
        // configured — Azure OpenAI in production, a scripted fake in tests.
        // Instructions come from the database via the startup-loaded singleton
        // (ADR-0008); their version is stamped onto every completed ingestion.
        var (instructions, version) = instructionProvider.Get(AgentInstructionDefaults.TranscriptChunker);
        _chunkingAgent = chatClient.AsAIAgent(name: AgentInstructionDefaults.TranscriptChunker, instructions: instructions);
        _instructionVersion = version;
        _chatModel = (chatClient.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.DefaultModelId ?? "unknown";
        _embeddingGenerator = embeddingGenerator;
        _store = store;

        // Size limits are operational tuning, not clinical policy — the
        // defaults are the band where embedding quality holds up.
        _sizeGuardrails = new ChunkSizeGuardrails(
            configuration.GetValue("Chunking:MinTokens", 50),
            configuration.GetValue("Chunking:MaxTokens", 800));
    }

    /// <summary>Runs the full strategy for one Transcript: chunk (boundaries-only) → enrich → embed (batched) → atomic store.</summary>
    public async Task IngestAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct)
    {
        var lines = SplitIntoLines(request.Transcript);

        await PublishStageAsync(ingestionId, request, IngestionStages.Chunking, ct);
        var plan = await RequestChunkPlanAsync(lines, ct);
        var sizedChunks = _sizeGuardrails.Apply(lines, plan.Chunks);
        var chunks = AssembleChunks(lines, sizedChunks, plan.Summary);

        await PublishStageAsync(ingestionId, request, IngestionStages.Embedding, ct);
        var embeddings = await _embeddingGenerator.GenerateAsync(chunks.Select(c => c.EmbeddingInput).ToList(), cancellationToken: ct);
        var records = chunks
            .Select((chunk, i) => new ChunkToStore(
                i, chunk.Kind, chunk.VerbatimText, chunk.ContextBlurb, chunk.SourceRefJson,
                new Vector(embeddings[i].Vector)))
            .ToList();

        await PublishStageAsync(ingestionId, request, IngestionStages.Storing, ct);
        var documentId = DocumentIdentity.For(
            request.DocumentType, request.DoctorId, request.PatientId, request.SessionId, request.SequenceNumber);
        await _store.CompleteWithChunksAsync(ingestionId, documentId, request, records, _instructionVersion, _chatModel, ct);

        // Announced only after the commit: the doctor is told the document is
        // searchable when it genuinely is.
        await PublishStageAsync(ingestionId, request, IngestionStages.Completed, ct);
    }

    private Task PublishStageAsync(Guid ingestionId, IngestionRequest request, string stage, CancellationToken ct) =>
        _statusPublisher.PublishAsync(ingestionId, request.DoctorId, request.PatientId, stage, ct: ct);

    private static IReadOnlyList<string> SplitIntoLines(string transcript) =>
        transcript
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    private async Task<ChunkPlan> RequestChunkPlanAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        // Never trust the agent's output blindly: validate, allow ONE corrective
        // retry naming the violation, then fail honestly — no fallback chunking.
        var prompt = BuildChunkingPrompt(lines);
        var (plan, violation) = await TryGetValidPlanAsync(prompt, lines.Count, ct);
        if (plan is not null)
            return plan;

        var retryPrompt = prompt +
            $"\n\nYour previous chunk plan was invalid: {violation} " +
            "Return a corrected plan following the same JSON contract.";
        (plan, violation) = await TryGetValidPlanAsync(retryPrompt, lines.Count, ct);
        return plan ?? throw new InvalidChunkPlanException(violation!);
    }

    private async Task<(ChunkPlan? Plan, string? Violation)> TryGetValidPlanAsync(string prompt, int lineCount, CancellationToken ct)
    {
        var response = await _chunkingAgent.RunAsync(prompt, cancellationToken: ct);
        ChunkPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<ChunkPlan>(StripCodeFences(response.Text), PlanJson);
        }
        catch (JsonException)
        {
            return (null, "the response was not valid JSON.");
        }

        if (plan is null)
            return (null, "the response was empty.");
        var violation = ValidatePlan(plan, lineCount);
        return violation is null ? (plan, null) : (null, violation);
    }

    private static string? ValidatePlan(ChunkPlan plan, int lineCount)
    {
        if (plan.Chunks is not { Count: > 0 })
            return "the plan contains no chunks.";
        if (string.IsNullOrWhiteSpace(plan.Summary))
            return "the plan is missing the summary.";

        var ordered = plan.Chunks.OrderBy(c => c.StartLine).ToList();
        plan.Chunks.Clear();
        plan.Chunks.AddRange(ordered);

        var expectedStart = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var chunk = ordered[i];
            if (chunk.EndLine < chunk.StartLine)
                return $"chunk {i + 1} ends at line {chunk.EndLine} before it starts at line {chunk.StartLine}.";
            if (chunk.StartLine != expectedStart)
                return $"chunk {i + 1} starts at line {chunk.StartLine} but expected line {expectedStart} — " +
                       "chunks must be contiguous and non-overlapping, covering every line.";
            expectedStart = chunk.EndLine + 1;
        }

        if (expectedStart != lineCount)
            return $"the plan covers lines up to {expectedStart - 1} but the transcript has lines 0 to {lineCount - 1} — " +
                   "chunks must be contiguous and non-overlapping, covering every line.";
        return null;
    }

    private static string BuildChunkingPrompt(IReadOnlyList<string> lines)
    {
        var prompt = new StringBuilder("Transcript lines:\n");
        for (var i = 0; i < lines.Count; i++)
            prompt.Append($"[{i}] {lines[i]}\n");
        return prompt.ToString();
    }

    private static List<AssembledChunk> AssembleChunks(
        IReadOnlyList<string> lines, IReadOnlyList<PlannedChunk> plannedChunks, string summary)
    {
        var chunks = new List<AssembledChunk>();
        foreach (var planned in plannedChunks)
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

        // The summary is a single generated paragraph, not source text — the
        // size guardrails deliberately never touch it.
        chunks.Add(new AssembledChunk(
            Kind: "summary",
            VerbatimText: summary,
            ContextBlurb: null,
            SourceRefJson: null,
            EmbeddingInput: summary));
        return chunks;
    }

    /// <summary>
    /// Unwraps a ```-fenced response, tolerating one that was never closed.
    ///
    /// A closing fence is not guaranteed: an answer cut off at the output-token
    /// limit has an opening fence and nothing else, and long transcripts make
    /// that more likely rather than less. The opening fence is removed first and
    /// the closing one looked for only in what remains, so it can never find the
    /// opening fence and slice backwards — which used to throw out of here, past
    /// the JSON handling, and skip the corrective retry meant for bad answers.
    ///
    /// Never throws: whatever comes back is handed to the parser, and an
    /// unreadable response fails as unreadable rather than as a string index.
    /// </summary>
    private static string StripCodeFences(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith("```"))
            return trimmed;

        // Everything after the opening fence line — which carries the optional
        // language tag, as in ```json.
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return string.Empty;
        var body = trimmed[(firstNewline + 1)..];

        // Closing fence if there is one; the whole body if there is not, so a
        // plan whose fence the model merely forgot is still read.
        var closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closingFence < 0 ? body : body[..closingFence]).Trim();
    }

    private sealed record ChunkPlan(List<PlannedChunk> Chunks, string Summary);

    private sealed record AssembledChunk(
        string Kind, string VerbatimText, string? ContextBlurb, string? SourceRefJson, string EmbeddingInput);
}

/// <summary>
/// The chunking agent produced an invalid plan twice in a row; the ingestion
/// is marked Failed rather than degrading — an explicit design decision.
/// </summary>
public sealed class InvalidChunkPlanException(string violation)
    : Exception($"Chunking agent produced an invalid chunk plan after a corrective retry: {violation}");
