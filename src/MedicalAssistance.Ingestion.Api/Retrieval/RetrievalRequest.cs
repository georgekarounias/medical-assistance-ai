namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// One retrieval request: a patient scope, a question, and optional narrowing.
/// The input to <see cref="IRetrievalService.SearchAsync"/>, independent of any
/// HTTP shape — the chat endpoint (T42) maps its payload onto this.
/// </summary>
public sealed record RetrievalRequest
{
    /// <summary>
    /// The patient whose record is searched — the one hard boundary (ADR-0011),
    /// carried from the route and always present. A retrieval without it is a bug.
    /// </summary>
    public required string PatientId { get; init; }

    /// <summary>The question to answer, as asked.</summary>
    public required string Question { get; init; }

    /// <summary>
    /// The asking doctor, for audit and telemetry only — <em>not</em> a scope
    /// filter. Narrowing to one doctor's documents is <see cref="RetrievalFilters.DoctorId"/>.
    /// </summary>
    public string? DoctorId { get; init; }

    /// <summary>How many chunks to retrieve; clamped to 1..50 by the endpoint (T48). Default 8.</summary>
    public int TopK { get; init; } = 8;

    /// <summary>Optional narrowing within the patient's record — every field applied in the same WHERE.</summary>
    public RetrievalFilters Filters { get; init; } = new();

    /// <summary>
    /// Optional recent conversation turns, most recent last — input only, used by the
    /// Refine step (T44) to resolve pronouns into a cleaner query. Never an evidence
    /// source and never stored (ADR-0010).
    /// </summary>
    public IReadOnlyList<ConversationTurn>? RecentTurns { get; init; }

    /// <summary>Optional rolling conversation summary from the backend — input only, same use and limits as <see cref="RecentTurns"/>.</summary>
    public string? PriorSummary { get; init; }
}

/// <summary>One turn of prior conversation, supplied for query refinement — retrieval's own view, independent of the chat DTOs.</summary>
public sealed record ConversationTurn(string? Role, string? Text);

/// <summary>
/// Optional narrowing within a patient's record. Each field, when set, becomes a
/// filter in the same SQL WHERE as the vector search (ADR-0011) — none is a
/// hard boundary; that is <see cref="RetrievalRequest.PatientId"/> alone.
/// </summary>
public sealed record RetrievalFilters
{
    /// <summary>Narrow to one doctor's documents (distinct from the asking doctor).</summary>
    public string? DoctorId { get; init; }

    /// <summary>Narrow to one Document Type (SessionTranscript, DoctorNote, LabReport, ImagingReport).</summary>
    public string? DocumentType { get; init; }

    /// <summary>Clinical-date lower bound (inclusive), by DocumentDate — not upload time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Clinical-date upper bound (inclusive), by DocumentDate.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Narrow to one clinical session.</summary>
    public string? SessionId { get; init; }

    /// <summary>Narrow to one language. Retrieval is cross-language by default; this is an optional filter, never a barrier.</summary>
    public string? Language { get; init; }
}
