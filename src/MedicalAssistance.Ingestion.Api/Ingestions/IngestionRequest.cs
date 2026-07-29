using System.Diagnostics.CodeAnalysis;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// A clinical Document submitted for ingestion by the existing backend.
/// The unit that flows through the pipeline; identified per Document Type — a
/// transcript's identity is <see cref="DoctorId"/> + <see cref="PatientId"/> +
/// <see cref="SessionId"/> + <see cref="SequenceNumber"/>, assembled by
/// <see cref="DocumentIdentity"/>.
///
/// Mandatory fields are enforced by <see cref="IngestionRequestValidation"/>
/// rather than by the deserializer: a missing field has to come back as a named
/// field error, not as a deserialization failure keyed on the whole document.
/// Nothing downstream of the controller ever sees an unvalidated request.
/// </summary>
public sealed record IngestionRequest
{
    /// <summary>The declared Document Type, supplied by the uploader — never inferred. Required. Currently supported: <c>SessionTranscript</c>, <c>DoctorNote</c>, <c>LabReport</c>, <c>ImagingReport</c>.</summary>
    public string DocumentType { get; init; } = null!;

    /// <summary>Identifier of the doctor the document belongs to. Required. Stamped on every chunk for access scoping.</summary>
    public string DoctorId { get; init; } = null!;

    /// <summary>Identifier of the patient the document is about. Required. The universal retrieval filter and security boundary.</summary>
    public string PatientId { get; init; } = null!;

    /// <summary>Identifier of the Session (the real-world doctor–patient encounter) this transcript belongs to. Required for <c>SessionTranscript</c>.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Ordinal of this Transcript within its Session. A new sequence number is a
    /// Continuation (sibling transcript); reusing an existing one is a Correction
    /// (supersedes). Required for <c>SessionTranscript</c>.
    /// </summary>
    public int? SequenceNumber { get; init; }

    /// <summary>
    /// Identifier of a DoctorNote, assigned by the backend. It is the note's whole
    /// identity: re-submitting the same noteId with different text is a Correction
    /// (supersedes), just as a transcript's (sessionId, sequenceNumber) is.
    /// Required for <c>DoctorNote</c>; ignored for other types.
    /// </summary>
    public string? NoteId { get; init; }

    /// <summary>
    /// Backend-assigned identifier of a lab or imaging report — the report's whole
    /// identity, so a re-POST of the same reportId with different content is a
    /// Correction (supersedes). Required for <c>LabReport</c> and <c>ImagingReport</c>.
    /// </summary>
    public string? ReportId { get; init; }

    /// <summary>Clinical date/time of the session (not the upload time). Powers recency queries like "the last session".</summary>
    public DateTimeOffset? SessionDate { get; init; }

    /// <summary>Language of the transcript content, e.g. <c>el</c> or <c>en</c>.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// The transcript as free text. Required for <c>SessionTranscript</c>, and must
    /// hold at least one non-empty line. Dialog-like, by convention one utterance
    /// per line ("Doctor: …" / "Patient: …"); the service treats non-empty lines as
    /// the atoms that chunk boundaries snap to, and never alters the text.
    /// </summary>
    public string? Transcript { get; init; }

    /// <summary>
    /// A DoctorNote's body as free text. Required for <c>DoctorNote</c>, and must
    /// hold at least one non-empty line. It runs through the same prose pipeline as
    /// a transcript (monologue rather than dialog); the service never alters it.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// A lab or imaging report as a base64-encoded, digitally generated PDF.
    /// Required for the PDF-backed types (lab and imaging reports) and size-capped
    /// at intake. Digital PDFs only — scanned or photographed documents have no
    /// text layer and are out of scope (ADR-0005).
    /// </summary>
    public string? PdfContent { get; init; }

    /// <summary>
    /// Link to the actual image in the doctor's existing viewer, for an
    /// <c>ImagingReport</c>. Required for that type: every stored chunk carries it,
    /// so a finding is one tap from the image. The pixels themselves are never
    /// ingested (ADR-0005).
    /// </summary>
    public string? ImageLink { get; init; }
}

/// <summary>The current state of one Ingestion.</summary>
public sealed record IngestionStatus
{
    /// <summary>Identifier of the Ingestion, as returned when the document was submitted.</summary>
    public required Guid IngestionId { get; init; }

    /// <summary>
    /// Lifecycle state: <c>Queued</c>, <c>Processing</c>, <c>Completed</c>,
    /// <c>Failed</c>, or <c>Superseded</c> — this ingestion succeeded once, but a
    /// later correction of the same document replaced its chunks, so it no longer
    /// describes anything in the store.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>Why the ingestion failed; present only when <see cref="Status"/> is <c>Failed</c>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The LLM-written summary of this Document, present once it has <c>Completed</c>
    /// and its type produces one (prose types do; a LabReport does not). Directly
    /// readable here without running a vector search over the stored summary chunk.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>Creates a status snapshot.</summary>
    public IngestionStatus()
    {
    }

    /// <summary>Creates a status snapshot with all fields.</summary>
    [SetsRequiredMembers]
    public IngestionStatus(Guid ingestionId, string status, string? errorMessage, string? summary = null)
    {
        IngestionId = ingestionId;
        Status = status;
        ErrorMessage = errorMessage;
        Summary = summary;
    }
}

/// <summary>
/// One Ingestion as it appears in a list — enough for a reconnecting client to
/// rebuild what it was showing, without a call per ingestion.
/// </summary>
public sealed record IngestionSummary
{
    /// <summary>Identifier of the Ingestion.</summary>
    public required Guid IngestionId { get; init; }

    /// <summary>
    /// The Document this Ingestion is of — the same identifier the status events
    /// and the patient document list use, assembled here so no consumer has to
    /// rebuild it from the parts below.
    /// </summary>
    public required string DocumentId { get; init; }

    /// <summary>The declared Document Type.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Patient the document is about.</summary>
    public required string PatientId { get; init; }

    /// <summary>Session identity component (transcripts only).</summary>
    public string? SessionId { get; init; }

    /// <summary>Transcript ordinal within its Session (transcripts only).</summary>
    public int? SequenceNumber { get; init; }

    /// <summary>Lifecycle state, as on <c>GET /ingestions/{id}</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Why it failed; present only for a Failed ingestion.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>When the document was accepted.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the ingestion last changed state.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// The chunking quality report of one completed Ingestion (T35): the shape the
/// chunker produced and how far its own plan fell outside the configured band
/// before code repaired it. Read at <c>GET /ingestions/{id}/quality</c>, it is
/// what turns a golden-set baseline into measured numbers — a rise in the
/// guardrail counts or the retry rate across a fixed corpus is a chunking
/// regression seen rather than suspected.
/// </summary>
public sealed record IngestionQualityReportView
{
    /// <summary>The Ingestion this report is of.</summary>
    public required Guid IngestionId { get; init; }

    /// <summary>How many chunks were stored, the summary chunk included.</summary>
    public required int ChunkCount { get; init; }

    /// <summary>Estimated tokens of every stored chunk, in chunk order — the full distribution.</summary>
    public required IReadOnlyList<int> TokenCounts { get; init; }

    /// <summary>Total estimated tokens across all stored chunks.</summary>
    public required int TotalTokens { get; init; }

    /// <summary>Smallest chunk's estimated tokens.</summary>
    public required int MinTokens { get; init; }

    /// <summary>Largest chunk's estimated tokens.</summary>
    public required int MaxTokens { get; init; }

    /// <summary>Mean estimated tokens per chunk (whole-number, matching the estimate's precision).</summary>
    public required int MeanTokens { get; init; }

    /// <summary>Sub-floor fragments the guardrails merged; <c>0</c> for a deterministic strategy (a LabReport).</summary>
    public required int GuardrailMerges { get; init; }

    /// <summary>Extra chunks the guardrails produced by splitting over-ceiling chunks; <c>0</c> for a deterministic strategy.</summary>
    public required int GuardrailSplits { get; init; }

    /// <summary>Whether the chunking agent's first plan was rejected and the corrective retry fired.</summary>
    public required bool CorrectiveRetryFired { get; init; }

    /// <summary>When the report was written — the ingestion's completion, in UTC.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Acknowledgement that a Document was accepted for ingestion.</summary>
public sealed record IngestionAccepted
{
    /// <summary>Identifier of the Ingestion; use it to poll status at <c>GET /ingestions/{id}</c>.</summary>
    public required Guid IngestionId { get; init; }

    /// <summary>
    /// True when this exact content had already been ingested successfully for
    /// this document identity: nothing was reprocessed and the id refers to the
    /// existing Ingestion. A double-click or a client retry lands here.
    /// </summary>
    public bool Duplicate { get; init; }
}

/// <summary>Confirmation that a patient's data was erased, and how much was removed.</summary>
public sealed record PatientDataErased
{
    /// <summary>The patient whose data was erased.</summary>
    public required string PatientId { get; init; }

    /// <summary>Who performed the erasure — echoed back, and recorded in the erasure log.</summary>
    public required string ErasedBy { get; init; }

    /// <summary>When it ran, in UTC.</summary>
    public required DateTimeOffset ErasedAt { get; init; }

    /// <summary>How many Ingestion rows were removed (zero if the service held nothing for the patient).</summary>
    public required int IngestionsErased { get; init; }

    /// <summary>How many Chunks were removed.</summary>
    public required int ChunksErased { get; init; }
}

/// <summary>Confirmation that a Document was un-ingested, and the tombstone it left.</summary>
public sealed record DocumentUnIngested
{
    /// <summary>The Document that was removed.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Who removed it — echoed back from the request, and recorded on the tombstone.</summary>
    public required string RemovedBy { get; init; }

    /// <summary>When it was removed, in UTC.</summary>
    public required DateTimeOffset DeletedAt { get; init; }
}
