using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>A fully assembled, embedded chunk handed from a strategy to the store for the atomic commit.</summary>
/// <param name="Index">Ordinal of the chunk within its document.</param>
/// <param name="Kind">What the text is: dialog or summary.</param>
/// <param name="VerbatimText">The chunk text, verbatim from the source (or the labeled AI summary).</param>
/// <param name="ContextBlurb">LLM-written retrieval context; null for summary chunks.</param>
/// <param name="SourceRefJson">Type-specific provenance JSON (line range for transcripts).</param>
/// <param name="Embedding">The pgvector embedding.</param>
public sealed record ChunkToStore(
    int Index,
    string Kind,
    string VerbatimText,
    string? ContextBlurb,
    string? SourceRefJson,
    Vector Embedding);

/// <summary>
/// One live document as it feeds a patient's rolling summary: its type, its clinical
/// date, and the per-document summary produced at ingestion (null for a type that
/// produces none, e.g. a LabReport).
/// </summary>
/// <param name="DocumentType">The Document Type, so the overview can weight a transcript differently from a lab report.</param>
/// <param name="DocumentDate">Clinical date of the document, for ordering the timeline.</param>
/// <param name="Summary">The document's own summary, or null when its type produces none.</param>
public sealed record PatientDocumentSummary(
    string DocumentType, DateTimeOffset? DocumentDate, string? Summary);

/// <summary>
/// What an Ingestion is about: which Document it concerns and who it belongs to —
/// the routing information every status event carries, because this service has no
/// idea which devices are online.
/// </summary>
/// <param name="DocumentId">
/// The Document this Ingestion is of. Assembled once by <see cref="DocumentIdentity.For"/>
/// and thereafter read from the stored column, never rebuilt from parts — different
/// Document Types put different parts in it (a note has no sequence number), so a
/// single reassembly formula could not serve them all.
/// </param>
/// <param name="DoctorId">The doctor who submitted the document.</param>
/// <param name="PatientId">The patient the document is about.</param>
/// <param name="SessionId">Session link (transcripts and session-linked notes only).</param>
public sealed record IngestionIdentity(
    string DocumentId, string DoctorId, string PatientId, string? SessionId)
{
    /// <summary>The identity of a submission that has not been stored yet.</summary>
    public static IngestionIdentity Of(IngestionRequest request) => new(
        DocumentIdentity.For(request), request.DoctorId, request.PatientId, request.SessionId);
}

/// <summary>What came of asking for an Ingestion to be rerun.</summary>
public enum RetryOutcome
{
    /// <summary>No Ingestion has that id.</summary>
    NotFound,

    /// <summary>It had failed, and is now queued for a complete rerun.</summary>
    Requeued,

    /// <summary>It is not in a state that can be rerun — only a Failed Ingestion can.</summary>
    NotRetryable,

    /// <summary>
    /// It failed, but a later submission of the same Document has completed since.
    /// Rerunning it would replace the newer version with the older one.
    /// </summary>
    Overtaken,
}

/// <summary>What came of asking to un-ingest a Document.</summary>
public enum UnIngestOutcome
{
    /// <summary>The live version's chunks and payload were removed and its Ingestion is now a Deleted tombstone.</summary>
    Deleted,

    /// <summary>No live version of this Document exists to remove — the id is unknown, or it was already un-ingested.</summary>
    NotFound,

    /// <summary>A version of this Document is still Queued or Processing; it cannot be removed until that run settles.</summary>
    InFlight,
}

/// <summary>What came of a worker trying to take an Ingestion off the queue.</summary>
public enum ClaimOutcome
{
    /// <summary>The attempt is counted and the Ingestion is now Processing.</summary>
    Claimed,

    /// <summary>
    /// It is no longer unfinished, so there is nothing here to run: some other
    /// run carried it to a terminal state while this queue entry waited its turn.
    /// </summary>
    NotClaimable,

    /// <summary>
    /// It is still unfinished but has used up its attempts, and must be failed
    /// rather than started again.
    /// </summary>
    AttemptsExhausted,
}

/// <summary>
/// An existing Ingestion of the same Document identity carrying byte-for-byte
/// identical content — the input to the dedup decision. Lifecycle strings stay
/// inside the store; callers ask what the outcome was, not how it is spelled.
/// </summary>
/// <param name="Id">Identifier of the existing Ingestion.</param>
/// <param name="Status">Its lifecycle state.</param>
public sealed record IdenticalIngestion(Guid Id, string Status)
{
    /// <summary>The identical content is already ingested; re-running it would only reproduce what exists.</summary>
    public bool Succeeded => Status == "Completed";

    /// <summary>The identical content failed to ingest; re-posting it is a retry, not a duplicate.</summary>
    public bool Failed => Status == "Failed";
}

/// <summary>
/// All database access for the ingestion pipeline. Owns the lifecycle of an
/// Ingestion record and the atomic chunk commit; nothing outside this class
/// writes to the database.
/// </summary>
public sealed class IngestionStore(IngestionDbContext db)
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Finds an Ingestion that has been accepted but has not finished — Queued
    /// or Processing — for this Document identity, or for this exact content
    /// filed anywhere else. Returns null when nothing is in flight.
    ///
    /// Two workers running the same document at once would race to write its
    /// chunk set, and neither dedup nor Correction can settle a document that
    /// has not landed yet, so the second submission is refused until the first
    /// reaches a terminal state.
    ///
    /// The document is matched by its assembled id, which carries the whole
    /// per-type key including the patient: whatever a type's identity is, two
    /// submissions are the same document exactly when their ids are equal, and one
    /// patient's upload can never block another's.
    /// </summary>
    public Task<Guid?> FindInFlightAsync(IngestionRequest request, CancellationToken ct = default)
    {
        var (_, contentHash) = SerializeAndHash(request);
        var documentId = DocumentIdentity.For(request);
        return db.Ingestions.AsNoTracking()
            .Where(i => (i.Status == "Queued" || i.Status == "Processing")
                        && (i.DocumentId == documentId || i.ContentHash == contentHash))
            .OrderBy(i => i.CreatedAt)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Finds the most recent Ingestion for the same Document identity whose
    /// submitted content is byte-for-byte identical, or null when this content
    /// has never been submitted for this identity. Same identity with different
    /// content is a Correction, not a duplicate, and is not reported here.
    ///
    /// The document is matched by its assembled id, which is the whole per-type
    /// key. The hash covers the identity's parts too, so pairing the two was
    /// already correct — but the id states the key outright rather than leaving it
    /// to an argument, and it is the one match every type shares.
    /// </summary>
    public Task<IdenticalIngestion?> FindIdenticalAsync(IngestionRequest request, CancellationToken ct = default)
    {
        var (_, contentHash) = SerializeAndHash(request);
        var documentId = DocumentIdentity.For(request);
        return db.Ingestions.AsNoTracking()
            .Where(i => i.DocumentId == documentId && i.ContentHash == contentHash)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new IdenticalIngestion(i.Id, i.Status))
            .FirstOrDefaultAsync(ct)!;
    }

    /// <summary>
    /// Finds a completed Ingestion of this exact content filed under a different
    /// identity — the same recording re-uploaded as a new session or a new
    /// sequence number. Ingesting it again would put the same passages in the
    /// patient's record twice and let the chat quote one encounter as if it were
    /// two, so it is skipped. Only completed ingestions qualify: content that is
    /// still in flight or that failed has nothing to deduplicate against.
    /// </summary>
    public Task<IdenticalIngestion?> FindSameContentElsewhereAsync(
        IngestionRequest request, CancellationToken ct = default)
    {
        var (_, contentHash) = SerializeAndHash(request);
        return db.Ingestions.AsNoTracking()
            .Where(i => i.ContentHash == contentHash && i.Status == "Completed")
            .OrderBy(i => i.CreatedAt)
            .Select(i => new IdenticalIngestion(i.Id, i.Status))
            .FirstOrDefaultAsync(ct)!;
    }

    /// <summary>
    /// Returns a Failed Ingestion to the queue for a fresh, complete rerun from
    /// its stored payload — there are no stage checkpoints to resume from
    /// (ADR-0003), so the earlier failure leaves nothing to clean up. Only a
    /// Failed ingestion can be rerun; the outcome says why not, when not.
    ///
    /// The attempt count starts over. Deciding to rerun is a deliberate act,
    /// usually taken because whatever broke has been fixed; inheriting a spent
    /// budget would leave a document unrecoverable forever because of an outage
    /// that is long over.
    ///
    /// A failure that a later submission has already completed over is
    /// <see cref="RetryOutcome.Overtaken"/> rather than rerunnable. Completing it
    /// would supersede the newer version with the older one, silently reverting
    /// the document to text a doctor had already replaced — the one outcome a
    /// rerun must never produce.
    /// </summary>
    public async Task<(RetryOutcome Outcome, string? CurrentStatus)> TryRetryAsync(
        Guid id, CancellationToken ct = default)
    {
        // Both conditions live in the same statement as the update, so neither a
        // worker picking this up nor a correction landing mid-call can slip
        // between the check and the requeue.
        var requeued = await db.Ingestions
            .Where(i => i.Id == id
                        && i.Status == "Failed"
                        && !db.Ingestions.Any(newer =>
                            newer.Id != i.Id
                            && newer.Status == "Completed"
                            && newer.CreatedAt > i.CreatedAt
                            && newer.DocumentId == i.DocumentId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(i => i.Status, "Queued")
                    .SetProperty(i => i.ErrorMessage, (string?)null)
                    .SetProperty(i => i.Attempts, 0)
                    .SetProperty(i => i.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        if (requeued > 0)
            return (RetryOutcome.Requeued, "Failed");

        var current = await db.Ingestions.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => i.Status)
            .FirstOrDefaultAsync(ct);

        // Still Failed, yet not requeued: the only other condition is the one
        // above, so a newer version of this document has landed.
        return current switch
        {
            null => (RetryOutcome.NotFound, null),
            "Failed" => (RetryOutcome.Overtaken, current),
            _ => (RetryOutcome.NotRetryable, current),
        };
    }

    /// <summary>Durably records a submitted Document as a Queued Ingestion (with content hash and raw payload) and returns its id.</summary>
    public async Task<Guid> CreateQueuedAsync(IngestionRequest request, CancellationToken ct = default)
    {
        var (payload, contentHash) = SerializeAndHash(request);
        var record = new IngestionRecord
        {
            Id = Guid.NewGuid(),
            DocumentId = DocumentIdentity.For(request),
            DocumentType = request.DocumentType,
            DoctorId = request.DoctorId,
            PatientId = request.PatientId,
            SessionId = request.SessionId,
            SequenceNumber = request.SequenceNumber,
            DocumentDate = request.SessionDate,
            Status = "Queued",
            ContentHash = contentHash,
            Payload = payload,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Ingestions.Add(record);
        await db.SaveChangesAsync(ct);
        return record.Id;
    }

    /// <summary>
    /// The doctor and patient an Ingestion belongs to, or null if the id is
    /// unknown. Read from the record's own columns rather than the stored
    /// payload, which carries the entire transcript and would be a wasteful
    /// thing to deserialize for two strings.
    /// </summary>
    public Task<IngestionIdentity?> GetIdentityAsync(Guid id, CancellationToken ct = default) =>
        db.Ingestions.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new IngestionIdentity(i.DocumentId, i.DoctorId, i.PatientId, i.SessionId))
            .FirstOrDefaultAsync(ct)!;

    /// <summary>Returns the lifecycle state of one Ingestion, or null if the id is unknown.</summary>
    public Task<IngestionStatus?> GetStatusAsync(Guid id, CancellationToken ct = default) =>
        db.Ingestions.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new IngestionStatus(i.Id, i.Status, i.ErrorMessage, i.Summary))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Lists a doctor's Ingestions, newest activity first. With
    /// <paramref name="activeOnly"/> this is the resync answer — everything
    /// accepted but not yet finished — which is what a client asks for after
    /// losing its hub connection, so no events ever need replaying.
    ///
    /// Capped rather than paged: the resync list is inherently short, and a
    /// caller asking for history has no use case yet that a page cursor would
    /// serve better than a limit.
    /// </summary>
    public async Task<List<IngestionSummary>> ListForDoctorAsync(
        string doctorId, bool activeOnly, int limit, CancellationToken ct = default)
    {
        var query = db.Ingestions.AsNoTracking().Where(i => i.DoctorId == doctorId);
        if (activeOnly)
            query = query.Where(i => i.Status == "Queued" || i.Status == "Processing");

        var ingestions = await query
            .OrderByDescending(i => i.UpdatedAt)
            .Take(limit)
            .Select(i => new
            {
                i.Id, i.DocumentId, i.DocumentType, i.PatientId, i.SessionId, i.SequenceNumber,
                i.Status, i.ErrorMessage, i.CreatedAt, i.UpdatedAt,
            })
            .ToListAsync(ct);

        return ingestions
            .Select(i => new IngestionSummary
            {
                IngestionId = i.Id,
                DocumentId = i.DocumentId,
                DocumentType = i.DocumentType,
                PatientId = i.PatientId,
                SessionId = i.SessionId,
                SequenceNumber = i.SequenceNumber,
                Status = i.Status,
                ErrorMessage = i.ErrorMessage,
                CreatedAt = i.CreatedAt,
                UpdatedAt = i.UpdatedAt,
            })
            .ToList();
    }

    /// <summary>
    /// Every Document the service holds for a patient, one row each, in the state
    /// its most recent Ingestion left it. Superseded versions and earlier failed
    /// attempts collapse into the document they belong to — a doctor counting
    /// rows here is counting transcripts, not uploads.
    ///
    /// A Document whose current state is Deleted is left out: un-ingest removes it
    /// from the record, and a doctor should not still see a document that has been
    /// taken out. Its tombstone remains for audit, but audit is not this view.
    ///
    /// <paramref name="doctorId" /> narrows the list to one doctor's documents.
    /// It is a filter and not a permission check: this service does not decide
    /// who may see what, the backend does (ADR-0007). Left null, the answer is
    /// the patient's whole record — which is a legitimate thing for the backend
    /// to ask for, and why this does not insist on a doctor.
    /// </summary>
    public async Task<List<PatientDocument>> ListPatientDocumentsAsync(
        string patientId, string? doctorId = null, CancellationToken ct = default)
    {
        var query = db.Ingestions.AsNoTracking().Where(i => i.PatientId == patientId);
        if (!string.IsNullOrWhiteSpace(doctorId))
            query = query.Where(i => i.DoctorId == doctorId);

        var ingestions = await query
            .OrderByDescending(i => i.UpdatedAt)
            .Select(i => new
            {
                i.Id, i.DocumentId, i.DocumentType, i.DoctorId, i.SessionId, i.SequenceNumber,
                i.DocumentDate, i.Status, i.ErrorMessage, i.Summary, i.UpdatedAt,
            })
            .ToListAsync(ct);

        // Bounded by one patient's care history, so collapsing to the latest per
        // document runs here rather than as a window function nobody can read. The
        // collapse key is the assembled document id — the whole per-type key — so a
        // note keyed on noteId and a transcript keyed on (session, sequence) each
        // group correctly, and two session-less notes never merge into one row the
        // way comparing their (null) session and sequence columns would.
        return ingestions
            .DistinctBy(i => i.DocumentId)
            // After the collapse, so it is the document's current state that is
            // judged: a slot whose latest version is a Deleted tombstone drops
            // out entirely rather than falling back to an older, still-live-looking
            // row beneath it.
            .Where(i => i.Status != "Deleted")
            .Select(i => new PatientDocument
            {
                DocumentId = i.DocumentId,
                DocumentType = i.DocumentType,
                SessionId = i.SessionId,
                SequenceNumber = i.SequenceNumber,
                DocumentDate = i.DocumentDate,
                Status = i.Status,
                ErrorMessage = i.ErrorMessage,
                Summary = i.Summary,
                IngestionId = i.Id,
                UpdatedAt = i.UpdatedAt,
            })
            .OrderByDescending(document => document.DocumentDate ?? document.UpdatedAt)
            .ToList();
    }

    /// <summary>
    /// The live documents that feed a patient's rolling summary: one line per
    /// currently-held document, oldest first, so the overview reads as a timeline. A
    /// Correction sets the previous version to Superseded and un-ingest to Deleted, so
    /// filtering to Completed leaves exactly the current version of each document.
    /// </summary>
    public async Task<List<PatientDocumentSummary>> ListCompletedDocumentSummariesAsync(
        string patientId, CancellationToken ct = default) =>
        await db.Ingestions.AsNoTracking()
            .Where(i => i.PatientId == patientId && i.Status == "Completed")
            .OrderBy(i => i.DocumentDate ?? i.UpdatedAt)
            .Select(i => new PatientDocumentSummary(i.DocumentType, i.DocumentDate, i.Summary))
            .ToListAsync(ct);

    /// <summary>Returns a patient's rolling overview, or null if none has been generated yet.</summary>
    public Task<PatientSummary?> GetPatientSummaryAsync(string patientId, CancellationToken ct = default) =>
        db.PatientSummaries.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PatientId == patientId, ct);

    /// <summary>
    /// Writes a patient's freshly regenerated overview, creating the row on the first
    /// ingestion and replacing it thereafter. Callers serialize regeneration per
    /// patient with an advisory lock, so this find-then-write cannot race itself into
    /// two rows for one patient.
    /// </summary>
    public async Task UpsertPatientSummaryAsync(
        string patientId, string summary, int documentCount, string? chatModel, int? instructionVersion,
        CancellationToken ct = default)
    {
        var existing = await db.PatientSummaries.FirstOrDefaultAsync(p => p.PatientId == patientId, ct);
        if (existing is null)
        {
            db.PatientSummaries.Add(new PatientSummary
            {
                PatientId = patientId,
                Summary = summary,
                DocumentCount = documentCount,
                ChatModel = chatModel,
                InstructionVersion = instructionVersion,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Summary = summary;
            existing.DocumentCount = documentCount;
            existing.ChatModel = chatModel;
            existing.InstructionVersion = instructionVersion;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Reloads the original submitted payload of an Ingestion — the input for processing and rerun-from-scratch.</summary>
    public async Task<IngestionRequest> LoadRequestAsync(Guid id, CancellationToken ct = default)
    {
        var payload = await db.Ingestions.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => i.Payload)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Ingestion {id} has no stored payload.");
        return JsonSerializer.Deserialize<IngestionRequest>(payload, PayloadJson)
            ?? throw new InvalidOperationException($"Ingestion {id} payload could not be deserialized.");
    }

    /// <summary>
    /// Claims an Ingestion for a worker: counts the attempt and moves it to
    /// Processing, in one statement so two workers cannot both claim it.
    ///
    /// Only an unfinished Ingestion can be claimed — the same Queued-or-Processing
    /// condition <see cref="FindUnfinishedAsync"/> selects on, because what is
    /// recoverable and what is runnable are the same thing. A queue entry can
    /// outlive the run it named: recovery hands one id to more than one instance
    /// by design, and the advisory lock only stops them running it at the same
    /// time, not one of them arriving after the other has finished. Without the
    /// status in the condition, that late arrival re-runs a Completed Ingestion
    /// and its commit supersedes whatever correction landed in the meantime.
    ///
    /// The two refusals are kept apart deliberately: attempts spent is a
    /// document that has to be failed, while no longer claimable is work that
    /// is simply not this worker's any more. Reporting the second as the first
    /// would mark a finished ingestion Failed.
    /// </summary>
    public async Task<ClaimOutcome> TryClaimAsync(Guid id, int maxAttempts, CancellationToken ct = default)
    {
        var claimed = await db.Ingestions
            .Where(i => i.Id == id
                        && (i.Status == "Queued" || i.Status == "Processing")
                        && i.Attempts < maxAttempts)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(i => i.Status, "Processing")
                    .SetProperty(i => i.Attempts, i => i.Attempts + 1)
                    .SetProperty(i => i.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        if (claimed > 0)
            return ClaimOutcome.Claimed;

        // Not claimed, so exactly one of the two conditions failed. Reading the
        // status back says which — and an id with no row at all is nobody's work.
        var status = await db.Ingestions.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => i.Status)
            .FirstOrDefaultAsync(ct);

        return status is "Queued" or "Processing"
            ? ClaimOutcome.AttemptsExhausted
            : ClaimOutcome.NotClaimable;
    }

    /// <summary>
    /// Ids of every Ingestion that was accepted but never reached a terminal
    /// state — what a crash or a deploy leaves behind. Swept up and queued
    /// again, because an accepted upload is a promise, and a doctor watching a
    /// progress bar has no way to know the process died.
    ///
    /// This is every unfinished Ingestion, including the ones another instance
    /// is running right now. Which of them are actually abandoned is a question
    /// about who holds their advisory locks, and it is
    /// <see cref="IngestionRecoverySweep" /> that asks it.
    /// </summary>
    public Task<List<Guid>> FindUnfinishedAsync(CancellationToken ct = default) =>
        db.Ingestions.AsNoTracking()
            .Where(i => i.Status == "Queued" || i.Status == "Processing")
            .OrderBy(i => i.CreatedAt)
            .Select(i => i.Id)
            .ToListAsync(ct);

    /// <summary>Moves an Ingestion to Failed, recording why — an honest, retriable failure (never silent).</summary>
    public Task MarkFailedAsync(Guid id, string errorMessage, CancellationToken ct = default) =>
        UpdateStatusAsync(id, "Failed", errorMessage, ct);

    /// <summary>
    /// GDPR Erasure: removes everything the service holds about a patient — all
    /// chunks and every Ingestion row, the Deleted tombstones un-ingest leaves
    /// included — and writes one <see cref="ErasureLogEntry" /> recording that it
    /// happened, in a single transaction so the record and the removal cannot
    /// come apart.
    ///
    /// It guarantees a state (no data for this patient) rather than acting on a
    /// precondition: a patient the service holds nothing about is a valid erasure
    /// of zero rows, still logged, so an erasure request always has an auditable
    /// answer and running it twice is safe.
    ///
    /// The log is the one thing erasure does not erase — its subject is gone, so
    /// the act has to be accountable on its own. Chunks are deleted before
    /// ingestions because a chunk points at its ingestion; the erasure_log row
    /// references neither, so it survives both.
    /// </summary>
    public Task<(int IngestionsErased, int ChunksErased)> ErasePatientDataAsync(
        string patientId, string erasedBy, CancellationToken ct = default) =>
        InTransactionAsync(async innerCt =>
        {
            // Chunks and analyte rows first: each carries a foreign key to its
            // ingestion, so the ingestion rows cannot go until nothing points at
            // them. Analyte rows are a patient's clinical numbers and are erased
            // with everything else (T31).
            var chunksErased = await db.Chunks.Where(c => c.PatientId == patientId).ExecuteDeleteAsync(innerCt);
            await db.AnalyteResults.Where(a => a.PatientId == patientId).ExecuteDeleteAsync(innerCt);
            var ingestionsErased = await db.Ingestions.Where(i => i.PatientId == patientId).ExecuteDeleteAsync(innerCt);

            db.ErasureLog.Add(new ErasureLogEntry
            {
                Id = Guid.NewGuid(),
                PatientId = patientId,
                ErasedBy = erasedBy,
                ErasedAt = DateTimeOffset.UtcNow,
                IngestionsErased = ingestionsErased,
                ChunksErased = chunksErased,
            });
            await db.SaveChangesAsync(innerCt);

            return (ingestionsErased, chunksErased);
        }, ct);

    /// <summary>
    /// Un-ingests a Document: in one transaction its chunks are deleted, its raw
    /// payload is scrubbed, and its live Ingestion becomes a Deleted tombstone
    /// naming who removed it and when. The canonical case is a wrong-patient
    /// upload, so removal has to be complete — nothing of the clinical content
    /// left behind — while the tombstone keeps the removal accountable.
    ///
    /// A Document mid-ingest is <see cref="UnIngestOutcome.InFlight"/>: its chunk
    /// set is not settled and a worker is writing it, so the run has to reach a
    /// terminal state before it can be removed. A Document with no live version —
    /// an unknown id, or one already un-ingested — is
    /// <see cref="UnIngestOutcome.NotFound"/>.
    ///
    /// The tombstone deliberately survives: a document that simply vanished would
    /// be worse than the mistake un-ingest exists to correct. Erasing even the
    /// tombstone is GDPR Erasure's job, guarded by its own admin secret.
    /// </summary>
    public Task<(UnIngestOutcome Outcome, DateTimeOffset? DeletedAt)> TryUnIngestAsync(
        string documentId, string removedBy, CancellationToken ct = default) =>
        InTransactionAsync(async innerCt =>
        {
            // In-flight is checked first and across every row for the document: a
            // correction can be Queued while the previous version is still
            // Completed, and removing one out from under the other mid-run is the
            // race this avoids. The check writes nothing, so returning here just
            // ends an empty transaction.
            var inFlight = await db.Ingestions
                .Where(i => i.DocumentId == documentId && (i.Status == "Queued" || i.Status == "Processing"))
                .AnyAsync(innerCt);
            if (inFlight)
                return (UnIngestOutcome.InFlight, (DateTimeOffset?)null);

            // Guarded on Completed, so two deletes racing settle to one, and a
            // document with no live version is left untouched rather than
            // tombstoned twice. There is at most one Completed row per document —
            // supersede demotes the old one as the new one lands — so this flips
            // exactly it. A no-match changes nothing, so the early return again
            // commits an empty transaction.
            var deletedAt = DateTimeOffset.UtcNow;
            var tombstoned = await db.Ingestions
                .Where(i => i.DocumentId == documentId && i.Status == "Completed")
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(i => i.Status, "Deleted")
                        .SetProperty(i => i.DeletedBy, removedBy)
                        .SetProperty(i => i.DeletedAt, deletedAt)
                        .SetProperty(i => i.Payload, (string?)null)
                        .SetProperty(i => i.UpdatedAt, deletedAt),
                    innerCt);
            if (tombstoned == 0)
                return (UnIngestOutcome.NotFound, null);

            // The chunks carry the same assembled id, so this removes exactly the
            // live version's chunks and no sibling's — atomically with the flip
            // above. A LabReport's analyte rows carry the same id and go with them,
            // so un-ingest leaves nothing of the document's clinical content (T31).
            await db.Chunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync(innerCt);
            await db.AnalyteResults.Where(a => a.DocumentId == documentId).ExecuteDeleteAsync(innerCt);

            return (UnIngestOutcome.Deleted, deletedAt);
        }, ct);

    /// <summary>
    /// The atomic commit of an Ingestion: any superseded version of the document
    /// is removed, all new chunks are written, and the status becomes Completed —
    /// in one transaction, so nothing is ever partially visible (ADR-0003).
    ///
    /// When this is a Correction, retrieval goes straight from the old version to
    /// the new one: it can never see both versions of a transcript at once, and
    /// never sees the document missing in between.
    /// </summary>
    public Task CompleteWithChunksAsync(
        Guid ingestionId, string documentId, IngestionRequest request, IReadOnlyList<ChunkToStore> chunks,
        int? instructionVersion, string? chatModel, string? embeddingModel,
        IReadOnlyList<VerifiedAnalyte>? analytes, bool? analytesExtracted, string? documentSummary,
        CancellationToken ct = default) =>
        InTransactionAsync(async innerCt =>
        {
            var ingestion = await db.Ingestions.FirstAsync(i => i.Id == ingestionId, innerCt);
            await SupersedePreviousVersionAsync(ingestionId, documentId, request, innerCt);

            db.Chunks.AddRange(chunks.Select(chunk => new Chunk
            {
                Id = Guid.NewGuid(),
                IngestionId = ingestionId,
                ChunkIndex = chunk.Index,
                DocumentId = documentId,
                DocumentType = request.DocumentType,
                PatientId = request.PatientId,
                DoctorId = request.DoctorId,
                SessionId = request.SessionId,
                DocumentDate = request.SessionDate,
                Language = request.Language,
                ChunkKind = chunk.Kind,
                SourceRef = chunk.SourceRefJson,
                VerbatimText = chunk.VerbatimText,
                ContextBlurb = chunk.ContextBlurb,
                Embedding = chunk.Embedding,
                EmbeddingModel = embeddingModel,
            }));

            // Analyte rows commit in the same transaction as the chunks (T31): a
            // document never holds a chunk set without its verified analytes, or the
            // reverse, and a Correction's supersede removes both together below.
            if (analytes is not null)
                db.AnalyteResults.AddRange(analytes.Select(analyte => new AnalyteResult
                {
                    Id = Guid.NewGuid(),
                    IngestionId = ingestionId,
                    DocumentId = documentId,
                    PatientId = request.PatientId,
                    DoctorId = request.DoctorId,
                    CanonicalName = analyte.CanonicalName,
                    VerbatimName = analyte.VerbatimName,
                    Value = analyte.Value,
                    Unit = analyte.Unit,
                    ReferenceRange = analyte.ReferenceRange,
                    Flag = analyte.Flag,
                    TableIndex = analyte.TableIndex,
                    RowIndex = analyte.RowIndex,
                }));

            ingestion.Status = "Completed";
            ingestion.InstructionVersion = instructionVersion;
            ingestion.ChatModel = chatModel;
            ingestion.AnalytesExtracted = analytesExtracted;
            ingestion.Summary = documentSummary;
            ingestion.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(innerCt);
        }, ct);

    /// <summary>
    /// Clears out the version of this Document that is being replaced: its chunks
    /// are deleted and its Ingestion is marked Superseded.
    ///
    /// The status matters as much as the delete. A Correction leaves the earlier
    /// ingestion with no chunks at all, so leaving it as Completed would let the
    /// dedup rule answer "already ingested" about text that no longer exists —
    /// and a re-upload of the original would be silently dropped. Superseded is
    /// the honest record: this ran, and this is no longer what the store holds.
    /// </summary>
    private async Task SupersedePreviousVersionAsync(
        Guid ingestionId, string documentId, IngestionRequest request, CancellationToken ct)
    {
        // The patient is inside documentId now, so this predicate is redundant.
        // It stays because the statement deletes clinical data: if an id is ever
        // built wrongly, the blast radius is confined to the patient it names
        // rather than reaching whoever else happens to match.
        await db.Chunks
            .Where(c => c.DocumentId == documentId && c.PatientId == request.PatientId)
            .ExecuteDeleteAsync(ct);

        // A LabReport's analyte rows are part of the version being replaced, so they
        // go with its chunks — otherwise a corrected report would keep the previous
        // version's numbers in the analyte table (T31).
        await db.AnalyteResults
            .Where(a => a.DocumentId == documentId && a.PatientId == request.PatientId)
            .ExecuteDeleteAsync(ct);

        await db.Ingestions
            .Where(i => i.Id != ingestionId
                        && i.Status == "Completed"
                        && i.DocumentId == documentId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(i => i.Status, "Superseded")
                    .SetProperty(i => i.UpdatedAt, DateTimeOffset.UtcNow),
                ct);
    }

    /// <summary>
    /// The submitted payload, plus a hash of what the document actually *is*.
    ///
    /// The hash deliberately excludes the filing identifiers — a transcript's
    /// <see cref="IngestionRequest.SessionId"/> and
    /// <see cref="IngestionRequest.SequenceNumber"/>, and a note's
    /// <see cref="IngestionRequest.NoteId"/> — because where a document is filed is
    /// not what it contains, so the same content re-uploaded under a fresh
    /// identifier is still recognised as already ingested. Everything else is in
    /// scope: the patient and doctor (so one patient's document can never dedup
    /// against another's), the clinical date and language (so correcting them
    /// re-ingests), and the body itself — a transcript's Transcript, a note's Text,
    /// or a report's PdfContent, whichever the type carries.
    /// </summary>
    private static (string Payload, string ContentHash) SerializeAndHash(IngestionRequest request)
    {
        var payload = JsonSerializer.Serialize(request, PayloadJson);
        var content = JsonSerializer.Serialize(
            new
            {
                request.DocumentType,
                request.PatientId,
                request.DoctorId,
                request.SessionDate,
                request.Language,
                request.Transcript,
                request.Text,
                request.PdfContent,
                request.ImageLink,
            },
            PayloadJson);
        return (payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
    }

    private async Task UpdateStatusAsync(Guid id, string status, string? errorMessage, CancellationToken ct)
    {
        await db.Ingestions.Where(i => i.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(i => i.Status, status)
            .SetProperty(i => i.ErrorMessage, errorMessage)
            .SetProperty(i => i.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>
    /// The one place a write becomes atomic. Every operation that changes more
    /// than one row — the supersede-and-insert commit, un-ingest, erasure — runs
    /// its body here, so beginning, committing, rolling back, and choosing the
    /// isolation level happen in a single spot instead of being re-established,
    /// and left to diverge, in each method. The body either returns and the whole
    /// of it commits, or it throws and disposing the transaction rolls all of it
    /// back: nothing a body wrote is ever left half-applied, and a body that
    /// returns early having written nothing simply commits an empty transaction.
    ///
    /// Read Committed is set explicitly rather than inherited from the connection
    /// default, so the isolation the whole service's writes run under is stated in
    /// one readable place. Strengthening it — to close a write-skew such as B18 —
    /// is a change to this line, and would want an execution strategy wrapped
    /// around the body to retry the serialization failures a stricter level
    /// introduces. (The provider is on its non-retrying default today, which is
    /// why the body is not required to be idempotent.)
    /// </summary>
    private async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var result = await body(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <inheritdoc cref="InTransactionAsync{T}" />
    private Task InTransactionAsync(Func<CancellationToken, Task> body, CancellationToken ct) =>
        InTransactionAsync<object?>(async innerCt => { await body(innerCt); return null; }, ct);
}
