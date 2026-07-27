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
        CancellationToken ct)
    {
        await PublishStageAsync(ingestionId, request, IngestionStages.Embedding, ct);
        var embeddings = await embeddingGenerator.GenerateAsync(
            chunks.Select(c => c.EmbeddingInput).ToList(), cancellationToken: ct);

        // The model that produced these vectors is stamped on every chunk, so an
        // embedding-model change becomes a managed re-embedding rather than silent
        // search corruption (the metadata spine's embeddingModel).
        var embeddingModel =
            (embeddingGenerator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata)
            ?.DefaultModelId;

        var records = chunks
            .Select((chunk, i) => new ChunkToStore(
                i, chunk.Kind, chunk.VerbatimText, chunk.ContextBlurb, chunk.SourceRefJson,
                new Vector(embeddings[i].Vector)))
            .ToList();

        await PublishStageAsync(ingestionId, request, IngestionStages.Storing, ct);
        var documentId = DocumentIdentity.For(request);
        await store.CompleteWithChunksAsync(
            ingestionId, documentId, request, records, instructionVersion, chatModel, embeddingModel,
            analytes, analytesExtracted, documentSummary, ct);

        // Announced only after the commit: the doctor is told the document is
        // searchable when it genuinely is.
        await PublishStageAsync(ingestionId, request, IngestionStages.Completed, ct);
    }

    private Task PublishStageAsync(Guid ingestionId, IngestionRequest request, string stage, CancellationToken ct) =>
        statusPublisher.PublishAsync(ingestionId, IngestionIdentity.Of(request), stage, ct: ct);
}
