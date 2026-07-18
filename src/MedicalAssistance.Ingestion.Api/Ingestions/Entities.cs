using Pgvector;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The durable record of one Ingestion: identity, lifecycle status, content hash
/// (for Correction/duplicate detection), and the raw payload (kept for retry,
/// rerun-from-scratch, and audit — see ADR-0003).
/// </summary>
public class IngestionRecord
{
    /// <summary>Primary key; returned to the caller as the ingestion id.</summary>
    public Guid Id { get; set; }

    /// <summary>The declared Document Type of the submitted payload.</summary>
    public string DocumentType { get; set; } = null!;

    /// <summary>Doctor the document belongs to.</summary>
    public string DoctorId { get; set; } = null!;

    /// <summary>Patient the document is about.</summary>
    public string PatientId { get; set; } = null!;

    /// <summary>Session identity component (transcripts only).</summary>
    public string? SessionId { get; set; }

    /// <summary>Transcript ordinal within its Session (transcripts only).</summary>
    public int? SequenceNumber { get; set; }

    /// <summary>Lifecycle state: Queued, Processing, Completed or Failed.</summary>
    public string Status { get; set; } = null!;

    /// <summary>Failure reason; set only when <see cref="Status"/> is Failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>SHA-256 of the canonical payload JSON; used to detect identical re-POSTs.</summary>
    public string ContentHash { get; set; } = null!;

    /// <summary>The submitted document payload, verbatim, as JSON.</summary>
    public string Payload { get; set; } = null!;

    /// <summary>When the ingestion was accepted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the ingestion last changed state.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One retrievable unit in the vector store: verbatim text plus its embedding,
/// carrying the shared metadata spine that makes cross-document retrieval and
/// cascade deletion possible.
/// </summary>
public class Chunk
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The Ingestion run that produced this chunk (audit/debug).</summary>
    public Guid IngestionId { get; set; }

    /// <summary>Ordinal of the chunk within its document.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>Identity of the source Document (for transcripts: sessionId#sequenceNumber). Cascade-delete target.</summary>
    public string DocumentId { get; set; } = null!;

    /// <summary>Document Type of the source document; lets the chat filter/weight by kind.</summary>
    public string DocumentType { get; set; } = null!;

    /// <summary>Patient scope — the universal retrieval filter and security boundary.</summary>
    public string PatientId { get; set; } = null!;

    /// <summary>Doctor scope for access filtering.</summary>
    public string DoctorId { get; set; } = null!;

    /// <summary>Session link (transcripts and session-linked notes only).</summary>
    public string? SessionId { get; set; }

    /// <summary>Clinical date of the source document (session date), not upload time.</summary>
    public DateTimeOffset? DocumentDate { get; set; }

    /// <summary>Language of the chunk text (el/en).</summary>
    public string? Language { get; set; }

    /// <summary>What this text is: dialog or summary (note, labPanel, imagingReport planned).</summary>
    public string ChunkKind { get; set; } = null!;

    /// <summary>Type-specific provenance as JSON — for transcripts the line range, e.g. {"startLine":0,"endLine":3}.</summary>
    public string? SourceRef { get; set; }

    /// <summary>The chunk text, copied verbatim from the source document (never LLM-generated), except summary chunks which are labeled AI text.</summary>
    public string VerbatimText { get; set; } = null!;

    /// <summary>LLM-written 1–2 sentence description of the chunk, prepended for embedding only (dialog chunks).</summary>
    public string? ContextBlurb { get; set; }

    /// <summary>The pgvector embedding of blurb + verbatim text (or the summary text).</summary>
    public Vector Embedding { get; set; } = null!;
}
