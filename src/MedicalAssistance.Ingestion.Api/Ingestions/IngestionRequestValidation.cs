namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Names of the Document Types the service knows. The type is declared by the
/// uploader and never inferred from content (ADR-0004). Which types are actually
/// accepted is the registered strategies' business, not a list here: the set the
/// door accepts is <see cref="IngestionStrategyRegistry.SupportedTypes"/>, so a
/// type is added by registering its strategy and nowhere else.
/// </summary>
public static class DocumentTypes
{
    /// <summary>A doctor–patient session transcript.</summary>
    public const string SessionTranscript = "SessionTranscript";
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
    /// sent. An empty result means the request is valid. <paramref name="supportedTypes"/>
    /// is the set the registry serves — the authority on which Document Types the
    /// door accepts, passed in so validation and routing can never disagree.
    /// </summary>
    public static Dictionary<string, string[]> Validate(
        IngestionRequest request, IReadOnlyCollection<string> supportedTypes)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.DocumentType))
            errors["documentType"] = ["A document type is required."];
        else if (!supportedTypes.Contains(request.DocumentType))
            errors["documentType"] =
                [$"'{request.DocumentType}' is not a supported document type. " +
                 $"Supported types: {string.Join(", ", supportedTypes)}."];

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
    /// A transcript is identified by doctor, patient, session and sequence
    /// number together. The doctor and patient are already required of every
    /// document; these two complete the key, and they are what later tells a
    /// Correction from a Continuation. Without them a re-upload could not be
    /// recognised as replacing anything, so both are mandatory from the very
    /// first submission.
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
