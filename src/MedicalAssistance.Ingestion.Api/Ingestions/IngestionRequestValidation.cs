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

    /// <summary>A typed clinical note written by the doctor about a patient.</summary>
    public const string DoctorNote = "DoctorNote";
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

        // The body and the identity are per-type: a transcript has dialog and a
        // (sessionId, sequenceNumber) key, a note has monologue text and a noteId.
        // Only the matching type's fields are required, so a note is never asked
        // for a transcript and vice versa.
        switch (request.DocumentType)
        {
            case DocumentTypes.SessionTranscript:
                ValidateTranscript(request, errors);
                break;
            case DocumentTypes.DoctorNote:
                ValidateNote(request, errors);
                break;
        }

        return errors;
    }

    /// <summary>
    /// A transcript is identified by doctor, patient, session and sequence number
    /// together. The doctor and patient are already required of every document;
    /// the session and sequence complete the key and are what later tells a
    /// Correction from a Continuation, so both are mandatory from the first
    /// submission — without them a re-upload could not be recognised as replacing
    /// anything. The transcript body itself is required too.
    /// </summary>
    private static void ValidateTranscript(IngestionRequest request, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            errors["sessionId"] = ["A session id is required for SessionTranscript documents."];

        if (request.SequenceNumber is null)
            errors["sequenceNumber"] = ["A sequence number is required for SessionTranscript documents."];
        else if (request.SequenceNumber < 0)
            errors["sequenceNumber"] = ["A sequence number cannot be negative."];

        if (string.IsNullOrWhiteSpace(request.Transcript))
            errors["transcript"] = ["A transcript with at least one non-empty line is required."];
    }

    /// <summary>
    /// A note is identified by its backend-assigned noteId — the whole of its
    /// identity, so it is mandatory from the first submission for the same reason a
    /// transcript's session key is: without it a re-POST could not be recognised as
    /// a Correction. The sessionId is optional (a note may or may not link to an
    /// encounter). The note body itself is required.
    /// </summary>
    private static void ValidateNote(IngestionRequest request, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(request.NoteId))
            errors["noteId"] = ["A note id is required for DoctorNote documents."];

        if (string.IsNullOrWhiteSpace(request.Text))
            errors["text"] = ["A note with at least one non-empty line of text is required."];
    }
}
