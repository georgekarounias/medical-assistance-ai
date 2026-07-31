namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// One stage of the retrieval pipeline. Steps are resolved from DI, sorted by
/// <see cref="Order"/>, and run in sequence against the shared
/// <see cref="RetrievalContext"/> — so a new stage (the deferred hybrid-search or
/// structured-analyte steps) slots in by registration alone, with no change to the
/// service that runs them.
/// </summary>
public interface IRetrievalStep
{
    /// <summary>
    /// Where this step runs in the sequence — lower runs first. Use the named
    /// values in <see cref="RetrievalStepOrder"/> so the pipeline's shape stays
    /// legible and new steps can slot cleanly between existing ones.
    /// </summary>
    int Order { get; }

    /// <summary>Runs this step's work against the shared context, reading what earlier steps set.</summary>
    Task ExecuteAsync(RetrievalContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The canonical order of the pipeline stages (design record, §"Retrieval
/// pipeline"). Spaced by tens so a future step can land between two without
/// renumbering. Scope is first and mandatory; the rest arrive across T41–T45.
/// </summary>
public static class RetrievalStepOrder
{
    /// <summary>Establish the mandatory patient_id boundary and optional filters.</summary>
    public const int Scope = 10;

    /// <summary>Optionally rewrite the question into a cleaner search query (T44).</summary>
    public const int Refine = 20;

    /// <summary>Embed the effective query with the same model/dimensions as ingestion (T41).</summary>
    public const int Embed = 30;

    /// <summary>ANN scan over the patient-scoped chunks (T41).</summary>
    public const int Search = 40;

    /// <summary>Drop hits below the confidence threshold; package survivors as evidence (T41/T45).</summary>
    public const int Package = 50;
}
