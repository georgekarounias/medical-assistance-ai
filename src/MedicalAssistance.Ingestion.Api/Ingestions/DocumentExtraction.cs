namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Turns a digitally generated PDF into machine-readable content: its text and its
/// tables as cell grids (ADR-0005). One provider-neutral interface — Azure Document
/// Intelligence is one implementation, a canned fake is another, and the stored
/// artifacts (text, cell grids) belong to no provider. Pixels are never returned;
/// a text RAG cannot quote them.
///
/// Digital PDFs only: no OCR path exists, so scanned or photographed documents are
/// out of scope until that decision is revisited (ADR-0005).
/// </summary>
public interface IDocumentExtractor
{
    /// <summary>Extracts the text and table cell grids of one PDF.</summary>
    Task<ExtractedDocument> ExtractAsync(byte[] pdf, CancellationToken ct);
}

/// <summary>A PDF's extracted content: its full text plus any tables, each a grid of cell strings.</summary>
/// <param name="Text">The document's text, in reading order.</param>
/// <param name="Tables">The tables found, each preserved as a cell grid (naive text extraction shreds them).</param>
public sealed record ExtractedDocument(string Text, IReadOnlyList<ExtractedTable> Tables);

/// <summary>
/// One extracted table as a cell grid: a list of rows, each a list of cell strings
/// left to right. The shape lab analyte mapping later reads columns from (T31),
/// which is why the structure is preserved rather than flattened to text.
/// </summary>
/// <param name="Rows">The table's rows, top to bottom; each row is its cells, left to right.</param>
public sealed record ExtractedTable(IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// The default extractor when no provider is configured: bootable, but fails loudly
/// on first use. A real Azure Document Intelligence adapter replaces it via
/// configuration; a fake replaces it via DI in tests.
/// </summary>
internal sealed class UnconfiguredDocumentExtractor : IDocumentExtractor
{
    public Task<ExtractedDocument> ExtractAsync(byte[] pdf, CancellationToken ct) =>
        throw new InvalidOperationException(
            "No document extractor is configured. Configure Azure Document Intelligence, or inject a fake in tests.");
}
