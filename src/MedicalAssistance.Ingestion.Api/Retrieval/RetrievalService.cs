namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// Runs the registered <see cref="IRetrievalStep"/>s in <see cref="IRetrievalStep.Order"/>
/// order against a fresh <see cref="RetrievalContext"/> per request. The service owns
/// no pipeline knowledge beyond "sort and run" — every stage is a registered step,
/// so the pipeline is extended by DI, not by editing this class (ADR-0011's
/// ordered-step registry).
/// </summary>
public sealed class RetrievalService(IEnumerable<IRetrievalStep> steps) : IRetrievalService
{
    // Sorted once at construction; the DI-registered set is fixed for the app's life.
    private readonly IReadOnlyList<IRetrievalStep> _orderedSteps = steps.OrderBy(step => step.Order).ToArray();

    public async Task<RetrievalResult> SearchAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var context = new RetrievalContext(request);
        foreach (var step in _orderedSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await step.ExecuteAsync(context, cancellationToken);
        }
        return new RetrievalResult(context.Evidence);
    }
}
