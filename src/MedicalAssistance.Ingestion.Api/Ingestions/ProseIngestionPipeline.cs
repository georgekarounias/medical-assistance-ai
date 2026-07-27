using System.Text;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Realtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The prose ingestion pipeline shared by every free-text Document Type:
/// boundaries-only LLM chunking (ADR-0002) → enrich → (embed + atomic store, via
/// <see cref="DocumentChunkCommitter"/>). The numbered non-empty lines of the body
/// are sent to a chunking agent that returns only line ranges, a blurb per chunk,
/// and a summary; the chunk text is assembled here, in code, verbatim from those
/// lines — a generative model never produces a stored word of patient text.
///
/// A strategy supplies only what differs between types: the body text, the chunk
/// kind stamped on it ('dialog' for a transcript, 'note' for a doctor's note), the
/// agent instructions to build from (ADR-0008), and the prompt header.
/// </summary>
public sealed class ProseIngestionPipeline
{
    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    private readonly IChatClient _chatClient;
    private readonly AgentInstructionProvider _instructionProvider;
    private readonly IngestionStatusPublisher _statusPublisher;
    private readonly DocumentChunkCommitter _committer;
    private readonly ChunkSizeGuardrails _sizeGuardrails;
    private readonly string _chatModel;

    public ProseIngestionPipeline(
        IChatClient chatClient,
        AgentInstructionProvider instructionProvider,
        IngestionStatusPublisher statusPublisher,
        DocumentChunkCommitter committer,
        IConfiguration configuration)
    {
        _chatClient = chatClient;
        _instructionProvider = instructionProvider;
        _statusPublisher = statusPublisher;
        _committer = committer;
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
    /// embed + atomic store. Every chunk of the body is stamped with
    /// <paramref name="bodyChunkKind"/>; the summary is stored as its own chunk.
    /// </summary>
    public async Task RunAsync(
        Guid ingestionId,
        IngestionRequest request,
        string body,
        string bodyChunkKind,
        string agentInstructionName,
        string promptHeader,
        CancellationToken ct,
        string? imageLink = null)
    {
        // Built per run from the instructions loaded at startup (ADR-0008); the
        // version is stamped onto the completed ingestion so a quality regression
        // is traceable to the prompt that caused it.
        var (instructions, instructionVersion) = _instructionProvider.Get(agentInstructionName);
        var chunkingAgent = _chatClient.AsAIAgent(name: agentInstructionName, instructions: instructions);

        var lines = SplitIntoLines(body);

        await _statusPublisher.PublishAsync(
            ingestionId, IngestionIdentity.Of(request), IngestionStages.Chunking, ct: ct);
        var plan = await RequestChunkPlanAsync(chunkingAgent, lines, promptHeader, ct);
        var sizedChunks = _sizeGuardrails.Apply(lines, plan.Chunks);
        var chunks = AssembleChunks(lines, sizedChunks, plan.Summary, bodyChunkKind, imageLink);

        await _committer.CommitAsync(
            ingestionId, request, chunks, instructionVersion, _chatModel,
            analytes: null, analytesExtracted: null, documentSummary: plan.Summary, ct);
    }

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
            plan = JsonSerializer.Deserialize<ChunkPlan>(AgentResponse.Unfence(response.Text), PlanJson);
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
        IReadOnlyList<string> lines, IReadOnlyList<PlannedChunk> plannedChunks, string summary, string bodyChunkKind,
        string? imageLink)
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
                SourceRefJson: SourceRef(planned.StartLine, planned.EndLine, imageLink),
                EmbeddingInput: $"{planned.ContextBlurb}\n\n{verbatim}"));
        }

        // The summary is a single generated paragraph, not source text — the size
        // guardrails deliberately never touch it, and its kind is 'summary'
        // regardless of the body's kind. It carries the image link too, so every
        // chunk of an imaging report is one tap from the image (ADR-0005).
        chunks.Add(new AssembledChunk(
            Kind: "summary",
            VerbatimText: summary,
            ContextBlurb: null,
            SourceRefJson: imageLink is null ? null : SourceRef(null, null, imageLink),
            EmbeddingInput: summary));
        return chunks;
    }

    // The provenance JSON on a chunk: a line range for prose, plus the image link
    // for an imaging report. Built so transcripts and notes (no image link) keep
    // exactly the {"startLine":..,"endLine":..} shape they had, while an imaging
    // chunk gains a JSON-escaped "imageLink".
    private static string SourceRef(int? startLine, int? endLine, string? imageLink)
    {
        var parts = new List<string>(3);
        if (startLine is { } start)
            parts.Add($"\"startLine\":{start}");
        if (endLine is { } end)
            parts.Add($"\"endLine\":{end}");
        if (imageLink is not null)
            parts.Add($"\"imageLink\":{JsonSerializer.Serialize(imageLink)}");
        return $"{{{string.Join(",", parts)}}}";
    }

    private sealed record ChunkPlan(List<PlannedChunk> Chunks, string Summary);
}

/// <summary>
/// The chunking agent produced an invalid plan twice in a row; the ingestion
/// is marked Failed rather than degrading — an explicit design decision.
/// </summary>
public sealed class InvalidChunkPlanException(string violation)
    : Exception($"Chunking agent produced an invalid chunk plan after a corrective retry: {violation}");
