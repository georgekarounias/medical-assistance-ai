namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The Ingestion Strategy for ImagingReport documents: a radiologist's findings.
/// The base64 PDF is decoded and its text extracted through the seam (ADR-0005),
/// then that text runs through the same shared prose pipeline as a transcript —
/// chunks, blurbs and a summary — with chunk kind 'imagingReport'.
///
/// Every chunk carries the required imageLink in its sourceRef, so a finding is one
/// tap from the actual image in the doctor's existing viewer. The pixels themselves
/// are never ingested: a text RAG cannot quote them, and ingesting them would mean
/// vision models and DICOM handling this service deliberately avoids (ADR-0005).
///
/// Identity is the backend-assigned reportId, so a re-POST is a Correction.
/// </summary>
public sealed class ImagingReportStrategy(
    IDocumentExtractor extractor, ProseIngestionPipeline pipeline) : IIngestionStrategy
{
    /// <inheritdoc />
    public string DocumentType => DocumentTypes.ImagingReport;

    /// <inheritdoc />
    public async Task IngestAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct)
    {
        // Validation has confirmed a present, size-capped, valid-base64 PDF and an
        // image link, so neither the decode nor the link can be missing here.
        var pdf = Convert.FromBase64String(request.PdfContent!);
        var extracted = await extractor.ExtractAsync(pdf, ct);

        await pipeline.RunAsync(
            ingestionId,
            request,
            body: extracted.Text,
            bodyChunkKind: "imagingReport",
            agentInstructionName: AgentNames.ImagingReportChunker,
            promptHeader: "Imaging report lines:",
            ct,
            imageLink: request.ImageLink);
    }
}
