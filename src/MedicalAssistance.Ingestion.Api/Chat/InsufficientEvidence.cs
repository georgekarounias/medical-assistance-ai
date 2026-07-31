namespace MedicalAssistance.Ingestion.Api.Chat;

/// <summary>
/// The insufficient-evidence refusal text — deterministic and code-owned, selected
/// by the question's language (ADR-0012). No model is called on the refusal path: an
/// honest "the record does not support an answer" must never itself be generated
/// prose that could drift or hallucinate. It is a plain, fixed sentence per language.
/// </summary>
internal static class InsufficientEvidence
{
    /// <summary>The refusal sentence for the given language (el/en), defaulting to English.</summary>
    public static string Message(string language) => language switch
    {
        "el" => "Δεν υπάρχουν επαρκή στοιχεία στον φάκελο του ασθενή για να απαντηθεί αυτή η ερώτηση.",
        _ => "There is not enough evidence in the patient's record to answer this question.",
    };
}
