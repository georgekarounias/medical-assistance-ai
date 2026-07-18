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
    /// Finds the most recent Ingestion for the same Document identity whose
    /// submitted content is byte-for-byte identical, or null when this content
    /// has never been submitted for this identity. Same identity with different
    /// content is a Correction, not a duplicate, and is not reported here.
    /// </summary>
    public Task<IdenticalIngestion?> FindIdenticalAsync(IngestionRequest request, CancellationToken ct = default)
    {
        var (_, contentHash) = SerializeAndHash(request);
        return db.Ingestions.AsNoTracking()
            .Where(i => i.DocumentType == request.DocumentType
                        && i.SessionId == request.SessionId
                        && i.SequenceNumber == request.SequenceNumber
                        && i.ContentHash == contentHash)
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
    /// (ADR-0003), so the earlier failure leaves nothing to clean up.
    /// </summary>
    public Task RequeueAsync(Guid id, CancellationToken ct = default) =>
        UpdateStatusAsync(id, "Queued", null, ct);

    /// <summary>Durably records a submitted Document as a Queued Ingestion (with content hash and raw payload) and returns its id.</summary>
    public async Task<Guid> CreateQueuedAsync(IngestionRequest request, CancellationToken ct = default)
    {
        var (payload, contentHash) = SerializeAndHash(request);
        var record = new IngestionRecord
        {
            Id = Guid.NewGuid(),
            DocumentType = request.DocumentType,
            DoctorId = request.DoctorId,
            PatientId = request.PatientId,
            SessionId = request.SessionId,
            SequenceNumber = request.SequenceNumber,
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

    /// <summary>Returns the lifecycle state of one Ingestion, or null if the id is unknown.</summary>
    public Task<IngestionStatus?> GetStatusAsync(Guid id, CancellationToken ct = default) =>
        db.Ingestions.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new IngestionStatus(i.Id, i.Status, i.ErrorMessage))
            .FirstOrDefaultAsync(ct);

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

    /// <summary>Moves an Ingestion to Processing.</summary>
    public Task MarkProcessingAsync(Guid id, CancellationToken ct = default) =>
        UpdateStatusAsync(id, "Processing", null, ct);

    /// <summary>Moves an Ingestion to Failed, recording why — an honest, retriable failure (never silent).</summary>
    public Task MarkFailedAsync(Guid id, string errorMessage, CancellationToken ct = default) =>
        UpdateStatusAsync(id, "Failed", errorMessage, ct);

    /// <summary>
    /// The atomic commit of an Ingestion: all chunks and the Completed status
    /// land in one SaveChanges — one transaction, so nothing is ever partially
    /// visible (ADR-0003).
    /// </summary>
    public async Task CompleteWithChunksAsync(
        Guid ingestionId, string documentId, IngestionRequest request, IReadOnlyList<ChunkToStore> chunks,
        int instructionVersion, string chatModel, CancellationToken ct = default)
    {
        var ingestion = await db.Ingestions.FirstAsync(i => i.Id == ingestionId, ct);

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
        }));

        ingestion.Status = "Completed";
        ingestion.InstructionVersion = instructionVersion;
        ingestion.ChatModel = chatModel;
        ingestion.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The submitted payload, plus a hash of what the document actually *is*.
    ///
    /// The hash deliberately excludes <see cref="IngestionRequest.SessionId"/>
    /// and <see cref="IngestionRequest.SequenceNumber"/> — where a document sits
    /// in a session's numbering is filing, not content — so the same recording
    /// re-uploaded under a fresh session or sequence number is still recognised
    /// as already ingested. Everything else is in scope: the patient and doctor
    /// (so one patient's document can never dedup against another's), and the
    /// clinical date and language (so correcting them re-ingests and the stored
    /// chunk metadata is corrected with them).
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
}
