namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// One retrieved chunk as evidence: its verbatim text, provenance, and similarity
/// score. Projected from a <c>chunks</c> row — which is already the authoritative
/// record, so there is nothing to hydrate it from (ADR-0011). The chat endpoint
/// turns these into citations; the Npgsql/pgvector types that produced them stay
/// inside infrastructure.
/// </summary>
public sealed record EvidenceItem
{
    /// <summary>The chunk's primary key.</summary>
    public required Guid ChunkId { get; init; }

    /// <summary>The source Document this chunk belongs to.</summary>
    public required string DocumentId { get; init; }

    /// <summary>The source document's type (SessionTranscript, DoctorNote, LabReport, ImagingReport).</summary>
    public required string DocumentType { get; init; }

    /// <summary>Ordinal of the chunk within its document.</summary>
    public required int ChunkIndex { get; init; }

    /// <summary>Session link, for session-scoped documents.</summary>
    public string? SessionId { get; init; }

    /// <summary>Clinical date of the source document — this record's notion of "freshness".</summary>
    public DateTimeOffset? DocumentDate { get; init; }

    /// <summary>Language of the chunk text (el/en).</summary>
    public string? Language { get; init; }

    /// <summary>What the text is: dialog, note, summary, labPanel, imagingReport.</summary>
    public required string ChunkKind { get; init; }

    /// <summary>Type-specific provenance as JSON — e.g. a transcript line range.</summary>
    public string? SourceRef { get; init; }

    /// <summary>The chunk text, verbatim from the source (or labeled AI text for summary chunks).</summary>
    public required string VerbatimText { get; init; }

    /// <summary>
    /// Cosine similarity to the query — 1 minus the cosine distance the search
    /// ranked by, so higher is closer (1 = identical direction). The Package step's
    /// confidence threshold (T45) compares against this.
    /// </summary>
    public required double Score { get; init; }
}

/// <summary>
/// The outcome of a retrieval: the evidence, ranked highest score first. An empty
/// set is a valid result — the answer path reads it as insufficient evidence.
/// </summary>
public sealed record RetrievalResult(IReadOnlyList<EvidenceItem> Evidence);
