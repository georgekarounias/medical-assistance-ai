using System.Collections.Concurrent;
using MedicalAssistance.Ingestion.Api.Ingestions;

namespace MedicalAssistance.Ingestion.Api.Tests.Fakes;

/// <summary>
/// Canned document extractor for tests (ADR-0005): returns scripted
/// <see cref="ExtractedDocument"/> results regardless of the PDF bytes, and records
/// what it was asked to extract. Replaces the Azure Document Intelligence adapter
/// via DI, so the PDF-backed strategies can be exercised with no Azure account and
/// no real PDF.
///
/// Enqueued results are returned in order (for a Correction that re-extracts a
/// different document); once they run out, a default result is returned for every
/// further call.
/// </summary>
public sealed class FakeDocumentExtractor(ExtractedDocument result) : IDocumentExtractor
{
    private readonly ConcurrentQueue<byte[]> _extracted = new();
    private readonly ConcurrentQueue<ExtractedDocument> _scripted = new();

    /// <summary>Every PDF byte array the pipeline has asked to extract, oldest first.</summary>
    public IReadOnlyList<byte[]> Extracted => _extracted.ToArray();

    /// <summary>Scripts the next extraction result; results are consumed in order before the default.</summary>
    public FakeDocumentExtractor Enqueue(ExtractedDocument next)
    {
        _scripted.Enqueue(next);
        return this;
    }

    public Task<ExtractedDocument> ExtractAsync(byte[] pdf, CancellationToken ct)
    {
        _extracted.Enqueue(pdf);
        return Task.FromResult(_scripted.TryDequeue(out var next) ? next : result);
    }
}
