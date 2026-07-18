using System.Diagnostics.CodeAnalysis;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// A clinical Document submitted for ingestion by the existing backend.
/// The unit that flows through the pipeline; identified per Document Type
/// (a transcript's identity is <see cref="SessionId"/> + <see cref="SequenceNumber"/>).
/// </summary>
public sealed record IngestionRequest
{
    /// <summary>The declared Document Type, supplied by the uploader — never inferred. Currently supported: <c>SessionTranscript</c>.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Identifier of the doctor the document belongs to. Stamped on every chunk for access scoping.</summary>
    public required string DoctorId { get; init; }

    /// <summary>Identifier of the patient the document is about. The universal retrieval filter and security boundary.</summary>
    public required string PatientId { get; init; }

    /// <summary>Identifier of the Session (the real-world doctor–patient encounter) this transcript belongs to.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Ordinal of this Transcript within its Session. A new sequence number is a
    /// Continuation (sibling transcript); reusing an existing one is a Correction (supersedes).
    /// </summary>
    public int? SequenceNumber { get; init; }

    /// <summary>Clinical date/time of the session (not the upload time). Powers recency queries like "the last session".</summary>
    public DateTimeOffset? SessionDate { get; init; }

    /// <summary>Language of the transcript content, e.g. <c>el</c> or <c>en</c>.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// The transcript as free text. Dialog-like, by convention one utterance per line
    /// ("Doctor: …" / "Patient: …"); the service treats non-empty lines as the atoms
    /// that chunk boundaries snap to, and never alters the text.
    /// </summary>
    public required string Transcript { get; init; }
}

/// <summary>The current state of one Ingestion.</summary>
public sealed record IngestionStatus
{
    /// <summary>Identifier of the Ingestion, as returned when the document was submitted.</summary>
    public required Guid IngestionId { get; init; }

    /// <summary>Lifecycle state: <c>Queued</c>, <c>Processing</c>, <c>Completed</c> or <c>Failed</c>.</summary>
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

/// <summary>Acknowledgement that a Document was accepted and queued for ingestion.</summary>
public sealed record IngestionAccepted
{
    /// <summary>Identifier of the created Ingestion; use it to poll status at <c>GET /ingestions/{id}</c>.</summary>
    public required Guid IngestionId { get; init; }
}
