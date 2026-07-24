namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// One Document Type's ingestion pipeline. The Orchestrator is a deterministic
/// <c>documentType → strategy</c> lookup (ADR-0004): a strategy declares the type
/// it handles, and <see cref="IngestionStrategyRegistry"/> routes each accepted
/// document to exactly one of them. Adding a Document Type is adding a strategy —
/// the intake, queue, status and storage around it never change.
///
/// AI lives inside a strategy (e.g. the transcript strategy's chunking agent),
/// never above it: routing is a dictionary, so a document can never be silently
/// misclassified down the wrong path.
/// </summary>
public interface IIngestionStrategy
{
    /// <summary>
    /// The Document Type this strategy handles, e.g. <c>SessionTranscript</c>.
    /// Declared by the strategy and never inferred from content. It is both the
    /// registry key and — through the registry — a member of the set of types the
    /// front door accepts, so a type is routable and submittable by one act.
    /// </summary>
    string DocumentType { get; }

    /// <summary>
    /// Runs the full pipeline for one document of this type, from its stored
    /// request to the atomic commit that makes it searchable (ADR-0003). A failure
    /// throws and the worker records it as Failed; nothing is partially visible.
    /// </summary>
    Task IngestAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct);
}
