namespace MedicalAssistance.Ingestion.Api.Chat;

/// <summary>
/// A cheap language signal for choosing the answer's language: el when the question
/// contains Greek letters, en otherwise. Deliberately minimal for T42 — the two
/// languages this record is written in are Greek and English, and retrieval stays
/// cross-language regardless. T47 exercises the cross-language behaviour in depth.
/// </summary>
internal static class QuestionLanguage
{
    // The Greek and Coptic Unicode block (U+0370–U+03FF) — modern Greek letters
    // (alpha–omega, upper and lower) live in its upper half, so the whole block is
    // the signal.
    private const char GreekBlockStart = 'Ͱ';
    private const char GreekBlockEnd = 'Ͽ';

    public static string Detect(string question) =>
        question.Any(c => c is >= GreekBlockStart and <= GreekBlockEnd) ? "el" : "en";
}
