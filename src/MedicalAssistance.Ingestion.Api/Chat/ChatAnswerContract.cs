namespace MedicalAssistance.Ingestion.Api.Chat;

/// <summary>
/// A grounded-answer request. The patient is the route (the hard boundary, ADR-0011);
/// this is the body. Conversation context (<see cref="RecentTurns"/>,
/// <see cref="PriorSummary"/>) is input only — used to interpret and phrase, never
/// stored, never an evidence source (ADR-0010).
/// </summary>
public sealed record ChatAnswerRequest
{
    /// <summary>The asking doctor — audit/telemetry, not a scope filter (narrowing is <see cref="ChatAnswerFilters.DoctorId"/>).</summary>
    public string? DoctorId { get; init; }

    /// <summary>The question to answer, as asked.</summary>
    public string? Question { get; init; }

    /// <summary>Optional recent conversation turns, most recent last — bounded and used only for phrasing/refinement.</summary>
    public IReadOnlyList<ChatTurn>? RecentTurns { get; init; }

    /// <summary>Optional rolling conversation summary from the backend — used only for phrasing/refinement.</summary>
    public string? PriorSummary { get; init; }

    /// <summary>How many chunks to retrieve; clamped 1..50, default 8.</summary>
    public int? TopK { get; init; }

    /// <summary>Optional narrowing within the patient's record.</summary>
    public ChatAnswerFilters? Filters { get; init; }
}

/// <summary>One turn of prior conversation, supplied by the backend.</summary>
public sealed record ChatTurn
{
    /// <summary><c>user</c> or <c>assistant</c>.</summary>
    public string? Role { get; init; }

    /// <summary>The turn's text.</summary>
    public string? Text { get; init; }
}

/// <summary>Optional retrieval narrowing — each field, when set, is applied in the same WHERE as the search (ADR-0011).</summary>
public sealed record ChatAnswerFilters
{
    /// <summary>Narrow to one doctor's documents (distinct from the asking doctor).</summary>
    public string? DoctorId { get; init; }

    /// <summary>Narrow to one Document Type.</summary>
    public string? DocumentType { get; init; }

    /// <summary>Clinical-date lower bound (inclusive), by DocumentDate.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Clinical-date upper bound (inclusive), by DocumentDate.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Narrow to one clinical session.</summary>
    public string? SessionId { get; init; }

    /// <summary>Narrow to one language. Retrieval is cross-language by default; this is a filter, never a barrier.</summary>
    public string? Language { get; init; }
}

/// <summary>
/// A grounded answer, or a refusal — 200 either way (a refusal is a normal
/// outcome, not an error). Stateless: a pure function of the request and the
/// patient's record on this turn.
/// </summary>
public sealed record ChatAnswerResponse
{
    /// <summary>The grounded prose, or (from T45) the deterministic insufficient-evidence refusal.</summary>
    public required string Answer { get; init; }

    /// <summary>True ⇒ insufficient evidence; the answer is a refusal and there are no citations.</summary>
    public required bool Refused { get; init; }

    /// <summary>Whether retrieval ran for this turn (always true in v1).</summary>
    public required bool RetrievalUsed { get; init; }

    /// <summary>The language the answer was written in (the question's language).</summary>
    public required string Language { get; init; }

    /// <summary>The evidence behind the answer, highest score first; empty on a refusal.</summary>
    public required IReadOnlyList<ChatCitation> Citations { get; init; }
}

/// <summary>One cited Evidence Item, with its provenance and a bounded verbatim quote.</summary>
public sealed record ChatCitation
{
    /// <summary>The label the answer cites this evidence by — <c>E1</c>, <c>E2</c>, … in retrieval order.</summary>
    public required string Label { get; init; }

    /// <summary>The chunk this evidence came from.</summary>
    public required Guid ChunkId { get; init; }

    /// <summary>The source Document.</summary>
    public required string DocumentId { get; init; }

    /// <summary>The source document's type.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Session link, for session-scoped documents.</summary>
    public string? SessionId { get; init; }

    /// <summary>Clinical date of the source document.</summary>
    public DateTimeOffset? DocumentDate { get; init; }

    /// <summary>Type-specific provenance as JSON (e.g. a transcript line range).</summary>
    public string? SourceRef { get; init; }

    /// <summary>The verbatim chunk text, bounded in length.</summary>
    public required string Quote { get; init; }

    /// <summary>Cosine similarity to the question — higher is closer.</summary>
    public required double Score { get; init; }
}
