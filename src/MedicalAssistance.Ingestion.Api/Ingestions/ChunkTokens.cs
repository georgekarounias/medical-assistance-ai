namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// A cheap, deterministic token estimate (~4 characters per token) used only by
/// the chunk-size guardrails. Its precision is deliberately unimportant: it
/// decides where a chunk boundary lands, never what text a chunk contains — so
/// an estimate that never calls a tokenizer, a model, or the network is the
/// right trade for a size heuristic.
/// </summary>
public static class ChunkTokens
{
    private const int CharactersPerToken = 4;

    /// <summary>Estimated token count of a piece of text.</summary>
    public static int Estimate(string text) => FromCharacters(text.Length);

    /// <summary>Estimated token count for a known character count, rounded up.</summary>
    public static int FromCharacters(int characterCount) =>
        (characterCount + CharactersPerToken - 1) / CharactersPerToken;
}
