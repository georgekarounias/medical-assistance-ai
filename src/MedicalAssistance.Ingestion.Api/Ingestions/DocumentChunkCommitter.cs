using System.Diagnostics;
using MedicalAssistance.Ingestion.Api.Realtime;
using Microsoft.Extensions.AI;
using Pgvector;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// A chunk assembled by a strategy and ready to embed: its stored text (verbatim
/// from the source, or a labeled generated summary), the retrieval blurb, its
/// provenance, and the exact text to embed. How the chunks were produced differs
/// per Document Type — an LLM proposing prose boundaries, code rendering a lab
/// panel — but from here on they are handled identically.
/// </summary>
/// <param name="Kind">The chunk kind stamped on the row: dialog, note, summary, labPanel, imagingReport.</param>
/// <param name="VerbatimText">The text stored and returned by retrieval.</param>
/// <param name="ContextBlurb">Optional retrieval context prepended for embedding only.</param>
/// <param name="SourceRefJson">Optional type-specific provenance JSON (line range, table index, image link).</param>
/// <param name="EmbeddingInput">The exact text to embed (blurb + verbatim, or the text alone).</param>
public sealed record AssembledChunk(
    string Kind, string VerbatimText, string? ContextBlurb, string? SourceRefJson, string EmbeddingInput);

/// <summary>
/// What the chunking of one document took, carried from the prose pipeline into
/// the atomic commit so the quality report (T35) can record it beside the chunks
/// it describes. A deterministic strategy (a LabReport rendering panels) runs no
/// chunking agent, so it commits without diagnostics and the report reads
/// <c>0</c> merges, <c>0</c> splits and no corrective retry — honest, since none
/// of those repairs was ever possible.
/// </summary>
/// <param name="GuardrailMerges">Sub-floor fragments the size guardrails merged.</param>
/// <param name="GuardrailSplits">Extra chunks the size guardrails produced by splitting over-ceiling chunks.</param>
/// <param name="CorrectiveRetryFired">Whether the chunking agent's first plan was rejected and the retry fired.</param>
public sealed record ChunkingDiagnostics(int GuardrailMerges, int GuardrailSplits, bool CorrectiveRetryFired);

/// <summary>
/// The final, shared leg of every strategy: embed the assembled chunks in batched
/// calls and commit them with the ingestion's completion in one transaction
/// (ADR-0003). Extracted so the prose pipeline, the lab strategy and the imaging
/// strategy all reach Completed the same way — nothing is partially visible, and
/// the Embedding / Storing / Completed stage events fire from one place.
/// </summary>
public sealed class DocumentChunkCommitter(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IngestionStore store,
    IngestionStatusPublisher statusPublisher)
{
    /// <summary>
    /// Embeds and atomically stores the chunks, stamping the ingestion with the
    /// instruction version and chat model that produced them — null for a
    /// deterministic strategy that used no LLM (Tier 1 lab rendering).
    /// </summary>
    public async Task CommitAsync(
        Guid ingestionId,
        IngestionRequest request,
        IReadOnlyList<AssembledChunk> chunks,
        int? instructionVersion,
        string? chatModel,
        IReadOnlyList<VerifiedAnalyte>? analytes,
        bool? analytesExtracted,
        string? documentSummary,
        CancellationToken ct,
        ChunkingDiagnostics? diagnostics = null)
    {
        await PublishStageAsync(ingestionId, request, IngestionStages.Embedding, ct);

        // The model that produced these vectors is stamped on every chunk, so an
        // embedding-model change becomes a managed re-embedding rather than silent
        // search corruption (the metadata spine's embeddingModel).
        var embeddingModel =
            (embeddingGenerator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata)
            ?.DefaultModelId;

        // A span around the embedding batch — chunk count and model, never the text
        // being embedded, which is the patient's verbatim content (ADR-0002/0006).
        GeneratedEmbeddings<Embedding<float>> embeddings;
        using (var activity = IngestionTelemetry.StartActivity("embed"))
        {
            activity?.SetTag("embedding.chunk_count", chunks.Count);
            activity?.SetTag("embedding.model", embeddingModel);
            embeddings = await embeddingGenerator.GenerateAsync(
                chunks.Select(c => c.EmbeddingInput).ToList(), cancellationToken: ct);
        }

        var records = chunks
            .Select((chunk, i) => new ChunkToStore(
                i, chunk.Kind, chunk.VerbatimText, chunk.ContextBlurb, chunk.SourceRefJson,
                new Vector(embeddings[i].Vector)))
            .ToList();

        // The quality report describes the chunk set actually stored, so it is
        // built here from the assembled chunks and committed in the same
        // transaction (T35). Token counts use the same cheap estimate the size
        // guardrails judge boundaries by, over the stored verbatim text of every
        // chunk — the summary chunk included, since it is one of the stored units.
        var report = BuildQualityReport(chunks, diagnostics);

        await PublishStageAsync(ingestionId, request, IngestionStages.Storing, ct);
        var documentId = DocumentIdentity.For(request);
        await store.CompleteWithChunksAsync(
            ingestionId, documentId, request, records, instructionVersion, chatModel, embeddingModel,
            analytes, analytesExtracted, documentSummary, report, ct);

        // Announced only after the commit: the doctor is told the document is
        // searchable when it genuinely is.
        await PublishStageAsync(ingestionId, request, IngestionStages.Completed, ct);
    }

    private static QualityReportToStore BuildQualityReport(
        IReadOnlyList<AssembledChunk> chunks, ChunkingDiagnostics? diagnostics)
    {
        var tokenCounts = chunks.Select(chunk => ChunkTokens.Estimate(chunk.VerbatimText)).ToArray();
        return new QualityReportToStore(
            ChunkCount: tokenCounts.Length,
            TokenCounts: tokenCounts,
            TotalTokens: tokenCounts.Sum(),
            MinTokens: tokenCounts.Length == 0 ? 0 : tokenCounts.Min(),
            MaxTokens: tokenCounts.Length == 0 ? 0 : tokenCounts.Max(),
            // No diagnostics means no chunking agent ran (a deterministic LabReport),
            // so no merge, split or retry was ever possible — recorded as such.
            GuardrailMerges: diagnostics?.GuardrailMerges ?? 0,
            GuardrailSplits: diagnostics?.GuardrailSplits ?? 0,
            CorrectiveRetryFired: diagnostics?.CorrectiveRetryFired ?? false);
    }

    private Task PublishStageAsync(Guid ingestionId, IngestionRequest request, string stage, CancellationToken ct) =>
        statusPublisher.PublishAsync(ingestionId, IngestionIdentity.Of(request), stage, ct: ct);
}
