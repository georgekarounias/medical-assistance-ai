namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>Shared handling of raw agent responses that carry JSON.</summary>
internal static class AgentResponse
{
    /// <summary>
    /// Unwraps a ```-fenced response, tolerating one that was never closed.
    ///
    /// A closing fence is not guaranteed: an answer cut off at the output-token
    /// limit has an opening fence and nothing else, and long inputs make that more
    /// likely rather than less. The opening fence is removed first and the closing
    /// one looked for only in what remains, so it can never find the opening fence
    /// and slice backwards — the defect B11 fixed. Never throws: whatever comes back
    /// is handed to the parser, and an unreadable response fails as unreadable
    /// rather than as a string index.
    /// </summary>
    public static string Unfence(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith("```"))
            return trimmed;

        // Everything after the opening fence line — which carries the optional
        // language tag, as in ```json.
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return string.Empty;
        var body = trimmed[(firstNewline + 1)..];

        // Closing fence if there is one; the whole body if there is not, so a
        // response whose fence the model merely forgot is still read.
        var closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closingFence < 0 ? body : body[..closingFence]).Trim();
    }
}
