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
/// All database access for the ingestion pipeline. Owns the lifecycle of an
/// Ingestion record and the atomic chunk commit; nothing outside this class
/// writes to the database.
/// </summary>
public sealed class IngestionStore(IngestionDbContext db)
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>Durably records a submitted Document as a Queued Ingestion (with content hash and raw payload) and returns its id.</summary>
    public async Task<Guid> CreateQueuedAsync(IngestionRequest request, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(request, PayloadJson);
        var record = new IngestionRecord
        {
            Id = Guid.NewGuid(),
            DocumentType = request.DocumentType,
            DoctorId = request.DoctorId,
            PatientId = request.PatientId,
            SessionId = request.SessionId,
            SequenceNumber = request.SequenceNumber,
            Status = "Queued",
            ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
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

    private async Task UpdateStatusAsync(Guid id, string status, string? errorMessage, CancellationToken ct)
    {
        await db.Ingestions.Where(i => i.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(i => i.Status, status)
            .SetProperty(i => i.ErrorMessage, errorMessage)
            .SetProperty(i => i.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }
}
