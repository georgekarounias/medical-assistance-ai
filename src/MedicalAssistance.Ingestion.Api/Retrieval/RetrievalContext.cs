namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// The request-scoped working state that flows through the ordered pipeline. One
/// is created per <see cref="IRetrievalService.SearchAsync"/> call; each
/// <see cref="IRetrievalStep"/> reads what earlier steps set and writes what later
/// steps need. Never shared between requests, never persisted.
/// </summary>
public sealed class RetrievalContext(RetrievalRequest request)
{
    /// <summary>The original request, unchanged for the life of the pipeline.</summary>
    public RetrievalRequest Request { get; } = request;

    /// <summary>
    /// The resolved patient scope and narrowing filters, set by the Scope step
    /// (Order 10) before any other step runs. Null until then — a step that reads
    /// it expects to run after Scope.
    /// </summary>
    public RetrievalScope? Scope { get; set; }

    /// <summary>
    /// The query that will actually be embedded. Starts as the raw question; the
    /// optional Refine step (T44) may rewrite it. Only ever affects the query
    /// vector, never the answer's grounding.
    /// </summary>
    public string EffectiveQuery { get; set; } = request.Question;

    /// <summary>
    /// The embedded <see cref="EffectiveQuery"/>, produced by the Embed step
    /// (Order 30) with the same model and dimensions ingestion used, and consumed
    /// by the Search step (Order 40) as the ANN probe. Null until Embed runs.
    /// </summary>
    public float[]? QueryEmbedding { get; set; }

    /// <summary>
    /// The retrieved evidence, filled by the Search/Package steps (T41). Empty
    /// until then — and an empty set is a valid outcome the answer path turns into
    /// an insufficient-evidence refusal.
    /// </summary>
    public IReadOnlyList<EvidenceItem> Evidence { get; set; } = [];
}

/// <summary>
/// The resolved scope of one retrieval: the mandatory patient boundary plus any
/// optional narrowing. Produced by the Scope step from the request; consumed by
/// the Search step (T41), which turns every field into a clause in the same WHERE
/// as the vector search (ADR-0011).
/// </summary>
public sealed record RetrievalScope
{
    /// <summary>The one hard boundary — always set. Enforced in the query, never as a post-filter.</summary>
    public required string PatientId { get; init; }

    /// <summary>Optional: narrow to one doctor's documents.</summary>
    public string? DoctorId { get; init; }

    /// <summary>Optional: narrow to one Document Type.</summary>
    public string? DocumentType { get; init; }

    /// <summary>Optional: clinical-date lower bound (inclusive).</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Optional: clinical-date upper bound (inclusive).</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Optional: narrow to one clinical session.</summary>
    public string? SessionId { get; init; }

    /// <summary>Optional: narrow to one language.</summary>
    public string? Language { get; init; }
}
