namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// The first pipeline step (Order 10): establishes the mandatory patient_id scope
/// and copies the optional narrowing filters onto the context. There is no
/// permission call — the backend is trusted (ADR-0007); scope is a query
/// constraint, and patient_id is the security boundary every later step inherits.
/// </summary>
public sealed class ScopeRetrievalStep : IRetrievalStep
{
    public int Order => RetrievalStepOrder.Scope;

    public Task ExecuteAsync(RetrievalContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;

        // The one invariant this step exists to guard: a retrieval without a
        // patient boundary is a bug, not a slow path (ADR-0011). Fail loudly here
        // rather than let an unscoped search reach the store.
        if (string.IsNullOrWhiteSpace(request.PatientId))
            throw new InvalidOperationException(
                "Retrieval requires a patient scope; PatientId is the mandatory security boundary and must be set.");

        context.Scope = new RetrievalScope
        {
            PatientId = request.PatientId,
            DoctorId = request.Filters.DoctorId,
            DocumentType = request.Filters.DocumentType,
            From = request.Filters.From,
            To = request.Filters.To,
            SessionId = request.Filters.SessionId,
            Language = request.Filters.Language,
        };

        return Task.CompletedTask;
    }
}
