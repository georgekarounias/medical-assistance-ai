namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Intake rules for base64 PDF payloads (ADR-0005). A PDF is decoded once and
/// checked against a configured byte cap, so an oversized or malformed payload is
/// refused at the door with a field error rather than failing deep in extraction —
/// and the service is never made to hold a document larger than it agreed to.
///
/// The rule lives here so the first PDF-backed strategy (LabReport, T30) validates
/// through it rather than inventing its own limit.
/// </summary>
public static class PdfIntake
{
    /// <summary>The configuration key for the maximum decoded PDF size, in bytes.</summary>
    public const string MaxBytesConfigurationKey = "Extraction:MaxPdfBytes";

    /// <summary>Default maximum decoded PDF size (10 MB), overridable via <see cref="MaxBytesConfigurationKey"/>.</summary>
    public const int DefaultMaxBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Validates a base64 PDF payload: it must be present, valid base64, non-empty,
    /// and within <paramref name="maxBytes"/> once decoded. Returns an error message
    /// for the <c>pdfContent</c> field, or null when the payload is acceptable.
    /// </summary>
    public static string? Validate(string? base64Content, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(base64Content))
            return "A base64-encoded PDF is required for this document type.";

        byte[] bytes;
        try
        {
            // Decoding to measure the real size is deliberate: a base64 length only
            // approximates the byte count, and the cap is about the bytes stored.
            bytes = Convert.FromBase64String(base64Content);
        }
        catch (FormatException)
        {
            return "The pdfContent is not valid base64.";
        }

        if (bytes.Length == 0)
            return "The pdfContent decodes to an empty document.";
        if (bytes.Length > maxBytes)
            return $"The PDF is {bytes.Length:N0} bytes, over the {maxBytes:N0}-byte limit.";
        return null;
    }
}
