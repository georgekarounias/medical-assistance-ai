using System.Text;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Realtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Pgvector;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The prose ingestion pipeline shared by every free-text Document Type:
/// boundaries-only LLM chunking (ADR-0002) → enrich → batched embed → atomic
/// store. The numbered non-empty lines of the body are sent to a chunking agent
/// that returns only line ranges, a blurb per chunk, and a summary; the chunk
/// text is assembled here, in code, verbatim from those lines — a generative
/// model never produces a stored word of patient text.
///
/// A strategy supplies only what differs between types: the body text, the chunk
/// kind stamped on it ('dialog' for a transcript, 'note' for a doctor's note),
/// the agent instructions to build from (ADR-0008), and the prompt header. The
/// identity, dedup, supersede and status machinery is identical for all of them,
/// which is the point of one pipeline rather than one per type.
/// </summary>
public sealed class ProseIngestionPipeline
{
    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    private readonly IChatClient _chatClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IngestionStore _store;
    private readonly AgentInstructionProvider _instructionProvider;
    private readonly IngestionStatusPublisher _statusPublisher;
    private readonly ChunkSizeGuardrails _sizeGuardrails;
    private readonly string _chatModel;

    public ProseIngestionPipeline(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IngestionStore store,
        AgentInstructionProvider instructionProvider,
        IngestionStatusPublisher statusPublisher,
        IConfiguration configuration)
    {
        _chatClient = chatClient;
        _embeddingGenerator = embeddingGenerator;
        _store = store;
        _instructionProvider = instructionProvider;
        _statusPublisher = statusPublisher;
        _chatModel = (chatClient.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.DefaultModelId
            ?? "unknown";

        // Size limits are operational tuning, not clinical policy — the defaults
        // are the band where embedding quality holds up.
        _sizeGuardrails = new ChunkSizeGuardrails(
            configuration.GetValue("Chunking:MinTokens", 50),
            configuration.GetValue("Chunking:MaxTokens", 800));
    }

    /// <summary>
    /// Runs the whole pipeline for one document: chunk (boundaries-only) → enrich →
    /// embed (batched) → atomic store. Every chunk of the body is stamped with
    /// <paramref name="bodyChunkKind"/>; the summary is stored as its own chunk.
    /// </summary>
    public async Task RunAsync(
        Guid ingestionId,
        IngestionRequest request,
        string body,
        string bodyChunkKind,
        string agentInstructionName,
        string promptHeader,
        CancellationToken ct)
    {
        // Built per run from the instructions loaded at startup (ADR-0008); the
        // version is stamped onto the completed ingestion so a quality regression
        // is traceable to the prompt that caused it.
        var (instructions, instructionVersion) = _instructionProvider.Get(agentInstructionName);
        var chunkingAgent = _chatClient.AsAIAgent(name: agentInstructionName, instructions: instructions);

        var lines = SplitIntoLines(body);

        await PublishStageAsync(ingestionId, request, IngestionStages.Chunking, ct);
        var plan = await RequestChunkPlanAsync(chunkingAgent, lines, promptHeader, ct);
        var sizedChunks = _sizeGuardrails.Apply(lines, plan.Chunks);
        var chunks = AssembleChunks(lines, sizedChunks, plan.Summary, bodyChunkKind);

        await PublishStageAsync(ingestionId, request, IngestionStages.Embedding, ct);
        var embeddings = await _embeddingGenerator.GenerateAsync(
            chunks.Select(c => c.EmbeddingInput).ToList(), cancellationToken: ct);
        var records = chunks
            .Select((chunk, i) => new ChunkToStore(
                i, chunk.Kind, chunk.VerbatimText, chunk.ContextBlurb, chunk.SourceRefJson,
                new Vector(embeddings[i].Vector)))
            .ToList();

        await PublishStageAsync(ingestionId, request, IngestionStages.Storing, ct);
        var documentId = DocumentIdentity.For(request);
        await _store.CompleteWithChunksAsync(
            ingestionId, documentId, request, records, instructionVersion, _chatModel, ct);

        // Announced only after the commit: the doctor is told the document is
        // searchable when it genuinely is.
        await PublishStageAsync(ingestionId, request, IngestionStages.Completed, ct);
    }

    private Task PublishStageAsync(Guid ingestionId, IngestionRequest request, string stage, CancellationToken ct) =>
        _statusPublisher.PublishAsync(ingestionId, IngestionIdentity.Of(request), stage, ct: ct);

    private static IReadOnlyList<string> SplitIntoLines(string body) =>
        body
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    private async Task<ChunkPlan> RequestChunkPlanAsync(
        AIAgent chunkingAgent, IReadOnlyList<string> lines, string promptHeader, CancellationToken ct)
    {
        // Never trust the agent's output blindly: validate, allow ONE corrective
        // retry naming the violation, then fail honestly — no fallback chunking.
        var prompt = BuildChunkingPrompt(lines, promptHeader);
        var (plan, violation) = await TryGetValidPlanAsync(chunkingAgent, prompt, lines.Count, ct);
        if (plan is not null)
            return plan;

        var retryPrompt = prompt +
            $"\n\nYour previous chunk plan was invalid: {violation} " +
            "Return a corrected plan following the same JSON contract.";
        (plan, violation) = await TryGetValidPlanAsync(chunkingAgent, retryPrompt, lines.Count, ct);
        return plan ?? throw new InvalidChunkPlanException(violation!);
    }

    private async Task<(ChunkPlan? Plan, string? Violation)> TryGetValidPlanAsync(
        AIAgent chunkingAgent, string prompt, int lineCount, CancellationToken ct)
    {
        var response = await chunkingAgent.RunAsync(prompt, cancellationToken: ct);
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
            return $"the plan covers lines up to {expectedStart - 1} but the document has lines 0 to {lineCount - 1} — " +
                   "chunks must be contiguous and non-overlapping, covering every line.";
        return null;
    }

    private static string BuildChunkingPrompt(IReadOnlyList<string> lines, string promptHeader)
    {
        var prompt = new StringBuilder(promptHeader).Append('\n');
        for (var i = 0; i < lines.Count; i++)
            prompt.Append($"[{i}] {lines[i]}\n");
        return prompt.ToString();
    }

    private static List<AssembledChunk> AssembleChunks(
        IReadOnlyList<string> lines, IReadOnlyList<PlannedChunk> plannedChunks, string summary, string bodyChunkKind)
    {
        var chunks = new List<AssembledChunk>();
        foreach (var planned in plannedChunks)
        {
            var verbatim = string.Join("\n", lines
                .Skip(planned.StartLine)
                .Take(planned.EndLine - planned.StartLine + 1));
            chunks.Add(new AssembledChunk(
                Kind: bodyChunkKind,
                VerbatimText: verbatim,
                ContextBlurb: planned.ContextBlurb,
                SourceRefJson: $$"""{"startLine":{{planned.StartLine}},"endLine":{{planned.EndLine}}}""",
                EmbeddingInput: $"{planned.ContextBlurb}\n\n{verbatim}"));
        }

        // The summary is a single generated paragraph, not source text — the
        // size guardrails deliberately never touch it, and its kind is 'summary'
        // regardless of the body's kind.
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
    /// limit has an opening fence and nothing else, and long documents make that
    /// more likely rather than less. The opening fence is removed first and the
    /// closing one looked for only in what remains, so it can never find the
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
