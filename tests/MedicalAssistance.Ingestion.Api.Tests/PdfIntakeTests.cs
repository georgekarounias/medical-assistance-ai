using MedicalAssistance.Ingestion.Api.Ingestions;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// Base64 PDF intake (ADR-0005): a payload is refused at the door if it is missing,
/// not valid base64, or larger than the configured cap — so an oversized or
/// malformed PDF never reaches extraction, and the service never holds a document
/// bigger than it agreed to.
/// </summary>
public class PdfIntakeTests
{
    [Fact]
    public void A_missing_pdf_is_rejected()
    {
        Assert.NotNull(PdfIntake.Validate(null, PdfIntake.DefaultMaxBytes));
        Assert.NotNull(PdfIntake.Validate("   ", PdfIntake.DefaultMaxBytes));
    }

    [Fact]
    public void Content_that_is_not_valid_base64_is_rejected()
    {
        var error = PdfIntake.Validate("not-@-valid-base64!!", PdfIntake.DefaultMaxBytes);
        Assert.NotNull(error);
        Assert.Contains("base64", error);
    }

    [Fact]
    public void A_pdf_over_the_cap_is_rejected_and_one_within_it_is_accepted()
    {
        var tenBytes = Convert.ToBase64String(new byte[10]);

        var tooBig = PdfIntake.Validate(tenBytes, maxBytes: 5);
        Assert.NotNull(tooBig);
        Assert.Contains("limit", tooBig);

        // The cap is inclusive: a document exactly at the limit is accepted.
        Assert.Null(PdfIntake.Validate(tenBytes, maxBytes: 10));
    }
}
