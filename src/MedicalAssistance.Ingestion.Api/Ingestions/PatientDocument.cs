namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// How a Document is identified in the store: the id every chunk is stamped
/// with, the patient document list is keyed by, and un-ingest addresses.
///
/// It lives in one place because those features have to agree exactly. A list
/// that names documents the delete endpoint cannot find would be worse than no
/// list at all.
///
/// A transcript is keyed by doctor, patient, session and sequence number
/// together. Those four are what make a document unique, and they are carried
/// in the id rather than merely alongside it because the id travels alone:
/// <c>DELETE /documents/{documentId}</c> receives nothing else, so an id naming
/// only a session would be safe to act on only if session ids were known to be
/// unique across patients — and they are not known to be. Carrying the whole key
/// makes the identifier answer that question by itself, instead of depending on
/// a property of the backend nobody has confirmed.
///
/// The same key decides what a Correction replaces, so every query that asks
/// "is this the same document?" has to match on all four. Those live in
/// <see cref="IngestionStore" />: in-flight detection, duplicate detection, the
/// staleness check on rerun, and supersede.
/// </summary>
public static class DocumentIdentity
{
    /// <summary>The Document id for a submission of the given Document Type.</summary>
    public static string For(
        string documentType, string doctorId, string patientId, string? sessionId, int? sequenceNumber) =>
        documentType switch
        {
            DocumentTypes.SessionTranscript => $"{doctorId}#{patientId}#{sessionId}#{sequenceNumber}",
            _ => throw new NotSupportedException($"No document identity is defined for '{documentType}'."),
        };
}

/// <summary>
/// One Document in a patient's record, in whatever state it is actually in.
/// </summary>
public sealed record PatientDocument
{
    /// <summary>Stable identifier of the Document — what un-ingest takes.</summary>
    public required string DocumentId { get; init; }

    /// <summary>The Document Type, so the doctor can tell a transcript from a lab report.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Session this document belongs to (transcripts and session-linked notes).</summary>
    public string? SessionId { get; init; }

    /// <summary>Ordinal within the session (transcripts only).</summary>
    public int? SequenceNumber { get; init; }

    /// <summary>
    /// Clinical date of the document — when the encounter happened, never when
    /// it was uploaded. This is what a doctor recognises a session by.
    /// </summary>
    public DateTimeOffset? DocumentDate { get; init; }

    /// <summary>
    /// State of the most recent Ingestion of this Document. A <c>Failed</c> entry
    /// is deliberately visible: believing a session was recorded when it was not
    /// is worse than seeing that it failed.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>Why the latest ingestion failed, when it did.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The most recent Ingestion of this Document, for status or retry.</summary>
    public required Guid IngestionId { get; init; }

    /// <summary>When that ingestion last changed state.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
