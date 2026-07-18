namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The Document Types this service accepts. The type is declared by the
/// uploader and never inferred from content (ADR-0004); this set grows by one
/// entry per Ingestion Strategy.
/// </summary>
public static class DocumentTypes
{
    /// <summary>A doctor–patient session transcript.</summary>
    public const string SessionTranscript = "SessionTranscript";

    /// <summary>Every Document Type that can be submitted today.</summary>
    public static readonly IReadOnlyList<string> Supported = [SessionTranscript];
}

/// <summary>
/// Validates a submission before it becomes an Ingestion. Rejecting at the door
/// is what keeps the pipeline honest: a payload that could never succeed never
/// becomes a row, so it can never resurface as a Failed ingestion that a doctor
/// has to interpret — and every problem with it is reported in one response,
/// field by field, rather than one round trip at a time.
/// </summary>
public static class IngestionRequestValidation
{
    /// <summary>
    /// Returns one entry per offending field, keyed by the JSON name the caller
    /// sent. An empty result means the request is valid.
    /// </summary>
    public static Dictionary<string, string[]> Validate(IngestionRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.DocumentType))
            errors["documentType"] = ["A document type is required."];
        else if (!DocumentTypes.Supported.Contains(request.DocumentType))
            errors["documentType"] =
                [$"'{request.DocumentType}' is not a supported document type. " +
                 $"Supported types: {string.Join(", ", DocumentTypes.Supported)}."];

        if (string.IsNullOrWhiteSpace(request.DoctorId))
            errors["doctorId"] = ["A doctor id is required."];

        if (string.IsNullOrWhiteSpace(request.PatientId))
            errors["patientId"] = ["A patient id is required."];

        if (string.IsNullOrWhiteSpace(request.Transcript))
            errors["transcript"] = ["A transcript with at least one non-empty line is required."];

        if (request.DocumentType == DocumentTypes.SessionTranscript)
            ValidateSessionIdentity(request, errors);

        return errors;
    }

    /// <summary>
    /// A transcript is identified by (sessionId, sequenceNumber) — the pair that
    /// later tells a Correction from a Continuation. Without both, a re-upload
    /// could not be recognised as replacing anything, so the pair is mandatory
    /// from the very first submission.
    /// </summary>
    private static void ValidateSessionIdentity(IngestionRequest request, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            errors["sessionId"] = ["A session id is required for SessionTranscript documents."];

        if (request.SequenceNumber is null)
            errors["sequenceNumber"] = ["A sequence number is required for SessionTranscript documents."];
        else if (request.SequenceNumber < 0)
            errors["sequenceNumber"] = ["A sequence number cannot be negative."];
    }
}
