namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// Patient-scoped retrieval over the authoritative <c>chunks</c> store. Internal
/// in v1 — the answer path (T42/T43) calls it directly; it is not HTTP-exposed
/// until a second consumer needs raw evidence (design record, §Contract).
/// </summary>
public interface IRetrievalService
{
    /// <summary>Runs the ordered pipeline for one request and returns the ranked evidence.</summary>
    Task<RetrievalResult> SearchAsync(RetrievalRequest request, CancellationToken cancellationToken = default);
}
