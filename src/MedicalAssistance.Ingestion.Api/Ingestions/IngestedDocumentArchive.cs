using System.Text;
using System.Text.Json;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Persists a submitted document to a durable landing zone before it is ingested,
/// independent of the database payload — for local inspection and testing.
///
/// Best-effort by contract: it never throws and never affects the ingestion. The
/// durable Queued row (and its stored payload) is the source of truth, so a
/// storage hiccup must never fail an accepted upload.
/// </summary>
public interface IIngestedDocumentArchive
{
    /// <summary>Archives one submitted document. Called after it is durably queued, before a worker runs it.</summary>
    Task ArchiveAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct);
}

/// <summary>The default when no archive is configured: archiving is a no-op.</summary>
internal sealed class NullDocumentArchive : IIngestedDocumentArchive
{
    public Task ArchiveAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Saves each submitted document to a local filesystem folder structure before
/// ingestion, under a configured root:
/// <c>{root}/{doctorId}/{patientId}/{documentType}/{documentId}/{ingestionId}.{ext}</c>
/// with a <c>metadata.json</c> manifest beside it. The body extension is
/// <c>.txt</c> for transcripts and notes, <c>.pdf</c> for lab and imaging reports.
///
/// Best-effort: a write failure is logged and swallowed, so the archive can never
/// fail an accepted upload — the database payload remains the system of record.
/// Path segments are sanitized because the identifiers are backend-supplied and a
/// stray path character must not let a write escape the root.
/// </summary>
internal sealed class LocalFileSystemDocumentArchive(
    string rootPath, ILogger<LocalFileSystemDocumentArchive> logger) : IIngestedDocumentArchive
{
    private static readonly JsonSerializerOptions MetadataJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task ArchiveAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct)
    {
        try
        {
            var documentId = DocumentIdentity.For(request);
            var folder = Path.Combine(
                rootPath,
                Sanitize(request.DoctorId),
                Sanitize(request.PatientId),
                Sanitize(request.DocumentType),
                Sanitize(documentId));
            Directory.CreateDirectory(folder);

            var (body, extension) = BodyOf(request);
            var bodyFile = body is null ? null : $"{ingestionId}.{extension}";
            if (body is not null)
                await File.WriteAllBytesAsync(Path.Combine(folder, bodyFile!), body, ct);

            var manifest = new
            {
                ingestionId,
                documentId,
                request.DocumentType,
                request.DoctorId,
                request.PatientId,
                request.SessionId,
                request.SequenceNumber,
                request.NoteId,
                request.SessionDate,
                request.Language,
                archivedAt = DateTimeOffset.UtcNow,
                bodyFile,
            };
            await File.WriteAllTextAsync(
                Path.Combine(folder, "metadata.json"), JsonSerializer.Serialize(manifest, MetadataJson), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not archive document for ingestion {IngestionId}; the ingestion is unaffected",
                ingestionId);
        }
    }

    // The raw body and its file extension, per document type. A PDF arrives as
    // base64 and is decoded to its bytes; text bodies are written as UTF-8.
    private static (byte[]? Body, string Extension) BodyOf(IngestionRequest request) =>
        request.DocumentType switch
        {
            DocumentTypes.SessionTranscript => (Utf8(request.Transcript), "txt"),
            DocumentTypes.DoctorNote => (Utf8(request.Text), "txt"),
            _ when request.PdfContent is not null => (DecodeBase64(request.PdfContent), "pdf"),
            _ => (null, "bin"),
        };

    private static byte[]? Utf8(string? text) => text is null ? null : Encoding.UTF8.GetBytes(text);

    private static byte[]? DecodeBase64(string content)
    {
        try
        {
            return Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Sanitize(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(segment.Length);
        foreach (var character in segment)
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        return builder.Length == 0 ? "_" : builder.ToString();
    }
}
