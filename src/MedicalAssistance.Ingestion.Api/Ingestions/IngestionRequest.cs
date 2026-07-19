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
    /// <summary>The declared Document Type, supplied by the uploader — never inferred. Required. Currently supported: <c>SessionTranscript</c>.</summary>
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

    /// <summary>Clinical date/time of the session (not the upload time). Powers recency queries like "the last session".</summary>
    public DateTimeOffset? SessionDate { get; init; }

    /// <summary>Language of the transcript content, e.g. <c>el</c> or <c>en</c>.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// The transcript as free text. Required, and must hold at least one non-empty
    /// line. Dialog-like, by convention one utterance per line ("Doctor: …" /
    /// "Patient: …"); the service treats non-empty lines as the atoms that chunk
    /// boundaries snap to, and never alters the text.
    /// </summary>
    public string Transcript { get; init; } = null!;
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

    /// <summary>Creates a status snapshot.</summary>
    public IngestionStatus()
    {
    }

    /// <summary>Creates a status snapshot with all fields.</summary>
    [SetsRequiredMembers]
    public IngestionStatus(Guid ingestionId, string status, string? errorMessage)
    {
        IngestionId = ingestionId;
        Status = status;
        ErrorMessage = errorMessage;
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
