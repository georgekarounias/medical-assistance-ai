namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// The Package step (Order 50): drops hits below the Confidence Threshold, so what
/// survives is evidence the record actually supports — not merely the nearest rows
/// the ANN scan could find. "The search returned something" is not "the record
/// supports this" (ADR-0012); an empty survivor set is a first-class outcome the
/// answer path turns into an honest insufficient-evidence refusal.
///
/// The threshold is configurable and calibrated against the golden sets (T51). Its
/// default here is a permissive floor — it only drops hits that are not even
/// positively related to the query — because the load-bearing value is a measured
/// one, not a guess; this step delivers the mechanism, T51 sets the number.
/// </summary>
public sealed class PackageRetrievalStep(IConfiguration configuration) : IRetrievalStep
{
    /// <summary>Config key for the minimum similarity a hit must clear to count as evidence.</summary>
    public const string ConfidenceThresholdConfigurationKey = "Retrieval:ConfidenceThreshold";

    /// <summary>The permissive default floor: keep anything not negatively correlated. T51 raises this to its calibrated value.</summary>
    public const double DefaultConfidenceThreshold = 0.0;

    public int Order => RetrievalStepOrder.Package;

    public Task ExecuteAsync(RetrievalContext context, CancellationToken cancellationToken)
    {
        var threshold = configuration.GetValue(ConfidenceThresholdConfigurationKey, DefaultConfidenceThreshold);
        context.Evidence = context.Evidence.Where(evidence => evidence.Score >= threshold).ToList();
        return Task.CompletedTask;
    }
}
