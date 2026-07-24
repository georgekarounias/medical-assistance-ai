namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The Orchestrator (ADR-0004): a deterministic <c>Document Type → Ingestion
/// Strategy</c> lookup, built from the registered strategies. Routing is a
/// dictionary and never an AI decision — a misclassification in a medical
/// pipeline would send a document down the wrong path with no one watching.
///
/// It is also the single source of truth for which types the service accepts:
/// request validation rejects anything whose type has no strategy, so a Document
/// Type becomes both routable and submittable by exactly one act — registering
/// its strategy. There is no second list to keep in step, and so none to drift.
/// </summary>
public sealed class IngestionStrategyRegistry
{
    private readonly Dictionary<string, IIngestionStrategy> _byType;

    /// <summary>
    /// Builds the lookup from every registered strategy. Two strategies claiming
    /// the same Document Type is a wiring mistake, not a runtime condition, so it
    /// fails loudly here rather than letting one silently shadow the other.
    /// </summary>
    public IngestionStrategyRegistry(IEnumerable<IIngestionStrategy> strategies)
    {
        _byType = new Dictionary<string, IIngestionStrategy>(StringComparer.Ordinal);
        foreach (var strategy in strategies)
        {
            if (!_byType.TryAdd(strategy.DocumentType, strategy))
                throw new InvalidOperationException(
                    $"Two ingestion strategies both claim document type '{strategy.DocumentType}'. " +
                    "Each Document Type must map to exactly one strategy (ADR-0004).");
        }
    }

    /// <summary>Every Document Type that has a registered strategy — the set the front door accepts.</summary>
    public IReadOnlyCollection<string> SupportedTypes => _byType.Keys;

    /// <summary>
    /// The strategy for a Document Type. The type is validated at the door against
    /// <see cref="SupportedTypes"/>, so an unknown type reaching here is a defect —
    /// a supported type with no strategy, or a path that skipped validation — and
    /// it throws rather than guessing a default.
    /// </summary>
    public IIngestionStrategy For(string documentType) =>
        _byType.TryGetValue(documentType, out var strategy)
            ? strategy
            : throw new UnknownDocumentTypeException(documentType);
}

/// <summary>
/// A document reached routing carrying a Document Type no strategy handles. This
/// is impossible through the validated front door, so it signals a bug rather
/// than a caller mistake — the worker records it as a Failed ingestion.
/// </summary>
public sealed class UnknownDocumentTypeException(string documentType)
    : Exception($"No ingestion strategy is registered for document type '{documentType}'.");
