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

    /// <summary>
    /// The Document this Ingestion is of, assembled once from its parts by
    /// <see cref="DocumentIdentity.For" /> and stored so it can be matched
    /// exactly. Un-ingest addresses a document by this string alone, and joining
    /// the parts back together on demand would depend on them being separable —
    /// which the '#' join does not guarantee. Stored, it is compared, never
    /// parsed.
    /// </summary>
    public string DocumentId { get; set; } = null!;

    /// <summary>The declared Document Type of the submitted payload.</summary>
    public string DocumentType { get; set; } = null!;

    /// <summary>Doctor the document belongs to.</summary>
    public string DoctorId { get; set; } = null!;

    /// <summary>Patient the document is about.</summary>
    public string PatientId { get; set; } = null!;

    /// <summary>Session link — a transcript's session, or a note's optional encounter link. Not part of a note's identity.</summary>
    public string? SessionId { get; set; }

    /// <summary>Transcript ordinal within its Session (transcripts only).</summary>
    public int? SequenceNumber { get; set; }

    /// <summary>
    /// Clinical date of the document (the session date), copied from the payload
    /// so the patient document list can be answered without opening it.
    /// </summary>
    public DateTimeOffset? DocumentDate { get; set; }

    /// <summary>Lifecycle state: Queued, Processing, Completed, Failed, Superseded or Deleted.</summary>
    public string Status { get; set; } = null!;

    /// <summary>Failure reason; set only when <see cref="Status"/> is Failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Who un-ingested this Document, and when. Set together, only on the move to
    /// Deleted: a removal has to be accountable (PRD story 21), and this service
    /// has no user identity of its own — the acting user is named by the trusted
    /// backend on the delete request. Null on every ingestion that has not been
    /// un-ingested.
    /// </summary>
    public string? DeletedBy { get; set; }

    /// <inheritdoc cref="DeletedBy" />
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// How many times a worker has picked this ingestion up. Counts crashes as
    /// well as failures, which is the point: a document that takes the process
    /// down leaves no error message behind, only this number.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>SHA-256 of the canonical payload JSON; used to detect identical re-POSTs.</summary>
    public string ContentHash { get; set; } = null!;

    /// <summary>
    /// The submitted document payload, verbatim, as JSON — the input for retry
    /// and rerun-from-scratch. Null once the Document has been un-ingested: the
    /// raw transcript is patient content and is removed with the chunks, leaving
    /// the tombstone the fact of the document without its text.
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>Version of the agent instructions that produced the stored chunks (set on completion).</summary>
    public int? InstructionVersion { get; set; }

    /// <summary>Chat model that processed the document (set on completion).</summary>
    public string? ChatModel { get; set; }

    /// <summary>
    /// For a LabReport: whether verified analyte rows were extracted (Tier 2). True
    /// when every mapped row passed verbatim verification and the rows were stored;
    /// false when any row could not be verified, in which case none were stored
    /// (all-or-nothing); null for document types that have no analytes. Queryable,
    /// so a report whose analytes failed can be found and re-processed.
    /// </summary>
    public bool? AnalytesExtracted { get; set; }

    /// <summary>When the ingestion was accepted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the ingestion last changed state.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The record that a patient's data was erased. It is what remains after GDPR
/// Erasure has removed everything else — chunks, ingestion rows, and the Deleted
/// tombstones un-ingest leaves — so the act of erasing is itself accountable
/// even though its subject is gone. Nothing in the service ever deletes from
/// this table; a second erasure of the same patient appends another row.
/// </summary>
public class ErasureLogEntry
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The patient whose data was erased.</summary>
    public string PatientId { get; set; } = null!;

    /// <summary>Who performed the erasure — named by the trusted backend, since this service has no user identity.</summary>
    public string ErasedBy { get; set; } = null!;

    /// <summary>When the erasure ran, in UTC.</summary>
    public DateTimeOffset ErasedAt { get; set; }

    /// <summary>How many Ingestion rows were removed — evidence of what the erasure took out.</summary>
    public int IngestionsErased { get; set; }

    /// <summary>How many Chunks were removed.</summary>
    public int ChunksErased { get; set; }
}

/// <summary>
/// One verified analyte result from a LabReport (Tier 2), stored relationally
/// beside the vector chunks for trend queries vector search cannot answer
/// ("HbA1c over the year"). The value, unit, reference range and flag are copied
/// verbatim from the extracted cells the mapping agent pointed at — only the
/// canonical name is agent-assigned (ADR-0006). Rows exist all-or-nothing: a
/// document holds every verified row or none, so a trend query never sees a
/// partially extracted panel.
/// </summary>
public class AnalyteResult
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The Ingestion run that produced this row (audit; cascade-delete with the document).</summary>
    public Guid IngestionId { get; set; }

    /// <summary>Identity of the source Document — the delete/supersede target, matched with its chunks.</summary>
    public string DocumentId { get; set; } = null!;

    /// <summary>Patient scope — the retrieval filter and the erasure target.</summary>
    public string PatientId { get; set; } = null!;

    /// <summary>Doctor scope for access filtering.</summary>
    public string DoctorId { get; set; } = null!;

    /// <summary>Agent-assigned canonical analyte name, e.g. <c>Hemoglobin</c> (the one mapping, not a value).</summary>
    public string CanonicalName { get; set; } = null!;

    /// <summary>The analyte name exactly as printed in the report — copied verbatim from the source cell.</summary>
    public string VerbatimName { get; set; } = null!;

    /// <summary>The measured value, copied verbatim from the source cell (never generated).</summary>
    public string Value { get; set; } = null!;

    /// <summary>Unit as printed, if any — copied verbatim.</summary>
    public string? Unit { get; set; }

    /// <summary>Reference range as printed, if any — copied verbatim.</summary>
    public string? ReferenceRange { get; set; }

    /// <summary>Flag as printed (e.g. HIGH/LOW), if any — copied verbatim.</summary>
    public string? Flag { get; set; }

    /// <summary>Provenance: the extracted table this row came from.</summary>
    public int TableIndex { get; set; }

    /// <summary>Provenance: the row within that table.</summary>
    public int RowIndex { get; set; }
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

    /// <summary>What this text is: dialog, note, summary, labPanel or imagingReport.</summary>
    public string ChunkKind { get; set; } = null!;

    /// <summary>Type-specific provenance as JSON — for transcripts the line range, e.g. {"startLine":0,"endLine":3}.</summary>
    public string? SourceRef { get; set; }

    /// <summary>The chunk text, copied verbatim from the source document (never LLM-generated), except summary chunks which are labeled AI text.</summary>
    public string VerbatimText { get; set; } = null!;

    /// <summary>LLM-written 1–2 sentence description of the chunk, prepended for embedding only (dialog chunks).</summary>
    public string? ContextBlurb { get; set; }

    /// <summary>The pgvector embedding of blurb + verbatim text (or the summary text).</summary>
    public Vector Embedding { get; set; } = null!;

    /// <summary>
    /// The embedding model that produced this chunk's vector — the write/read
    /// contract in the metadata spine. Recording it per chunk is what makes an
    /// embedding-model change a managed re-embedding migration instead of silent
    /// search corruption: a query can tell which vectors a given model produced.
    /// </summary>
    public string? EmbeddingModel { get; set; }
}
