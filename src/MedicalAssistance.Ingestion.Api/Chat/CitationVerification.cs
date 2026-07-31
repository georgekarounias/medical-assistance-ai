using System.Text.RegularExpressions;

namespace MedicalAssistance.Ingestion.Api.Chat;

/// <summary>
/// Raised when a generated answer cites an [E#] label that was not supplied this
/// turn. It fails the turn (ADR-0012): the answer is discarded, not returned, and
/// there is no corrective retry — deliberately unlike the ingestion chunker's one
/// retry. Its message names only the offending labels, never the answer text, so a
/// 5xx cannot leak the unverified content.
/// </summary>
public sealed class CitationVerificationException(IReadOnlyList<string> unsuppliedLabels)
    : Exception($"The generated answer cited evidence labels that were not supplied this turn: " +
                $"{string.Join(", ", unsuppliedLabels)}.")
{
    /// <summary>The cited labels that were never supplied.</summary>
    public IReadOnlyList<string> UnsuppliedLabels { get; } = unsuppliedLabels;
}

/// <summary>
/// Grounding enforcement (T46): checks that every [E#] the answer cites was actually
/// supplied this turn, then reconciles the returned citations to exactly those the
/// answer references. A cited label that was never supplied is a fabricated
/// reference and fails the turn — nothing ungrounded is ever emitted (ADR-0012).
/// Because the supplied evidence comes straight from this turn's retrieval, each
/// still resolves to its authoritative row by construction.
/// </summary>
public static partial class CitationVerification
{
    // Matches an inline citation like [E1] or [e12]; the digits are the label number.
    [GeneratedRegex(@"\[[Ee](\d+)\]")]
    private static partial Regex CitationLabel();

    /// <summary>
    /// Verifies the answer's citations against the supplied evidence and returns the
    /// cited subset, in supplied (score) order. Throws
    /// <see cref="CitationVerificationException"/> if the answer cites any label that
    /// was not supplied.
    /// </summary>
    public static IReadOnlyList<ChatCitation> Verify(string answer, IReadOnlyList<ChatCitation> supplied)
    {
        var citedLabels = CitationLabel().Matches(answer)
            .Select(match => "E" + match.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var suppliedLabels = supplied.Select(citation => citation.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unsupplied = citedLabels.Where(label => !suppliedLabels.Contains(label)).ToList();
        if (unsupplied.Count > 0)
            throw new CitationVerificationException(unsupplied);

        // Reconcile: only the evidence the answer actually cited is returned as a
        // citation, in the order retrieval ranked it.
        return supplied.Where(citation => citedLabels.Contains(citation.Label)).ToList();
    }
}
