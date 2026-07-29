namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// One chunk the chunking agent proposes: a line range plus the blurb that will
/// be embedded alongside it. Boundaries only — the text is assembled from the
/// source lines, in code (ADR-0002).
/// </summary>
internal sealed record PlannedChunk(int StartLine, int EndLine, string ContextBlurb);

/// <summary>
/// What the guardrails did to a plan: the resized chunks, plus how many repairs
/// each pass made. The counts are the quality report's <c>guardrailMerges</c> and
/// <c>guardrailSplits</c> (T35) — a rise in either across a golden set is a
/// chunking regression made visible, since every repair is a boundary the agent
/// proposed outside the configured band.
/// </summary>
/// <param name="Chunks">The plan's chunks resized to fit the band.</param>
/// <param name="Merges">Sub-floor fragments absorbed into a neighbor — one per merge operation.</param>
/// <param name="Splits">Extra chunks produced by cutting oversized chunks (parts − 1, summed).</param>
internal sealed record GuardrailOutcome(IReadOnlyList<PlannedChunk> Chunks, int Merges, int Splits);

/// <summary>
/// Chunk-size guardrails, applied after the plan is validated and before any
/// text is assembled. Embedding quality collapses at both extremes — a
/// three-word fragment carries no retrievable meaning, and an overlong chunk
/// dilutes the one passage that mattered — so code, never the model, merges
/// sub-floor fragments into a neighbor and splits oversized chunks.
///
/// Verbatim completeness outranks both limits: boundaries only ever move to
/// existing line boundaries, every line stays in exactly one chunk in its
/// original order, and a single line longer than the ceiling is kept whole
/// rather than cut mid-sentence.
/// </summary>
internal sealed class ChunkSizeGuardrails(int minTokens, int maxTokens)
{
    /// <summary>
    /// Returns the plan's chunks resized to fit the configured band, alongside a
    /// count of the merges and splits it took — the two repair counts the quality
    /// report records (T35).
    /// </summary>
    public GuardrailOutcome Apply(IReadOnlyList<string> lines, IReadOnlyList<PlannedChunk> planned)
    {
        var merged = MergeUndersized(lines, planned, out var merges);
        var resized = SplitOversized(lines, merged, out var splits);
        return new GuardrailOutcome(resized, merges, splits);
    }

    private List<PlannedChunk> MergeUndersized(
        IReadOnlyList<string> lines, IReadOnlyList<PlannedChunk> planned, out int merges)
    {
        var chunks = planned.ToList();
        merges = 0;
        // Each merge removes one chunk, so this terminates; a whole document
        // below the floor ends as one undersized chunk, which is honest — there
        // is nothing left to merge it with.
        for (var undersized = FindUndersized(lines, chunks); chunks.Count > 1 && undersized >= 0;
             undersized = FindUndersized(lines, chunks))
        {
            var neighbor = SmallerNeighborOf(lines, chunks, undersized);
            var (first, second) = neighbor < undersized ? (neighbor, undersized) : (undersized, neighbor);
            chunks[first] = new PlannedChunk(
                chunks[first].StartLine,
                chunks[second].EndLine,
                CombineBlurbs(chunks[first].ContextBlurb, chunks[second].ContextBlurb));
            chunks.RemoveAt(second);
            merges++;
        }
        return chunks;
    }

    private int FindUndersized(IReadOnlyList<string> lines, List<PlannedChunk> chunks) =>
        chunks.FindIndex(chunk => Tokens(lines, chunk.StartLine, chunk.EndLine) < minTokens);

    /// <summary>
    /// Merges into whichever neighbor is smaller, so absorbing a fragment never
    /// pushes a chunk that is already near the ceiling over it.
    /// </summary>
    private static int SmallerNeighborOf(IReadOnlyList<string> lines, List<PlannedChunk> chunks, int index)
    {
        if (index == 0)
            return 1;
        if (index == chunks.Count - 1)
            return index - 1;
        var previous = Tokens(lines, chunks[index - 1].StartLine, chunks[index - 1].EndLine);
        var next = Tokens(lines, chunks[index + 1].StartLine, chunks[index + 1].EndLine);
        return previous <= next ? index - 1 : index + 1;
    }

    private static string CombineBlurbs(string first, string second) =>
        string.IsNullOrWhiteSpace(second) ? first
        : string.IsNullOrWhiteSpace(first) ? second
        : $"{first.TrimEnd()} {second.TrimStart()}";

    private List<PlannedChunk> SplitOversized(IReadOnlyList<string> lines, List<PlannedChunk> chunks, out int splits)
    {
        var resized = new List<PlannedChunk>();
        splits = 0;
        foreach (var chunk in chunks)
        {
            if (Tokens(lines, chunk.StartLine, chunk.EndLine) <= maxTokens)
            {
                resized.Add(chunk);
                continue;
            }

            // The blurb describes the topic of the whole range, so each part
            // inherits it; the stored line range is what tells the parts apart.
            var partsBefore = resized.Count;
            for (var start = chunk.StartLine; start <= chunk.EndLine;)
            {
                var end = LastLineOfNextPart(lines, start, chunk.EndLine);
                resized.Add(chunk with { StartLine = start, EndLine = end });
                start = end + 1;
            }

            // Every part beyond the first is a split this chunk cost. A single
            // over-ceiling line that cannot be cut yields exactly one part, so it
            // adds nothing here — kept whole is not a split.
            splits += resized.Count - partsBefore - 1;
        }
        return resized;
    }

    /// <summary>
    /// Chooses where the next part ends, aiming for parts of even size — the
    /// tokens still to place, spread over the number of parts still needed — so
    /// a split never leaves a starved tail that the merge pass would undo.
    /// </summary>
    private int LastLineOfNextPart(IReadOnlyList<string> lines, int start, int lastLine)
    {
        var remaining = Tokens(lines, start, lastLine);
        var partsNeeded = Math.Max(1, (remaining + maxTokens - 1) / maxTokens);
        var target = (remaining + partsNeeded - 1) / partsNeeded;

        var end = start;
        while (end < lastLine
               && Tokens(lines, start, end) < target
               && Tokens(lines, start, end + 1) <= maxTokens)
        {
            end++;
        }
        return end;
    }

    /// <summary>Estimated size of a line range, counting the newlines that join it.</summary>
    private static int Tokens(IReadOnlyList<string> lines, int startLine, int endLine)
    {
        var characters = 0;
        for (var line = startLine; line <= endLine; line++)
            characters += lines[line].Length + (line > startLine ? 1 : 0);
        return ChunkTokens.FromCharacters(characters);
    }
}
