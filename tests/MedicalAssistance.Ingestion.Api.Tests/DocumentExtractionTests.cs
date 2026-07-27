using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The extraction seam (ADR-0005): a PDF becomes text plus tables as cell grids,
/// through one provider-neutral interface. This pins the contract the canned fake
/// and the future Azure adapter both honour — the first PDF-backed strategy (T30)
/// consumes it through the same interface.
/// </summary>
public class DocumentExtractionTests
{
    [Fact]
    public async Task The_fake_extractor_returns_its_canned_text_and_table_cell_grid()
    {
        var canned = new ExtractedDocument(
            "Hemoglobin 13.2 g/dL",
            [new ExtractedTable([["Analyte", "Value"], ["Hemoglobin", "13.2"]])]);
        var extractor = new FakeDocumentExtractor(canned);

        var extracted = await extractor.ExtractAsync([1, 2, 3], CancellationToken.None);

        Assert.Equal("Hemoglobin 13.2 g/dL", extracted.Text);
        var table = Assert.Single(extracted.Tables);
        Assert.Equal("Hemoglobin", table.Rows[1][0]);
        Assert.Equal("13.2", table.Rows[1][1]);

        // The bytes it was handed are exactly the ones it recorded — the seam does
        // not transform the input, only the output.
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(extractor.Extracted));
    }
}
