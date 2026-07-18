using System.Threading.Channels;
using MedicalAssistance.Ingestion.Api.Ingestions;
using Microsoft.AspNetCore.Mvc;

namespace MedicalAssistance.Ingestion.Api.Controllers;

/// <summary>
/// Accepts clinical Documents for ingestion and reports Ingestion status.
/// Called exclusively by the existing backend — the doctor's app never talks
/// to this service directly.
/// </summary>
[ApiController]
[Route("ingestions")]
[Produces("application/json")]
public sealed class IngestionsController(IngestionStore store, Channel<Guid> queue) : ControllerBase
{
    /// <summary>Submits a clinical Document for ingestion.</summary>
    /// <remarks>
    /// Processing is asynchronous: the document is durably recorded, queued, and
    /// this call returns immediately with 202. Poll <c>GET /ingestions/{id}</c>
    /// for progress. The transcript is chunked semantically by an LLM that only
    /// proposes boundaries — stored chunk text is always verbatim.
    /// </remarks>
    /// <param name="request">The document payload with its declared type, clinical identifiers, and content.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="202">The document was accepted and queued; the body carries the ingestion id.</response>
    /// <response code="400">
    /// The payload is malformed or breaks the submission contract. The body is a
    /// problem document whose <c>errors</c> map names every offending field at
    /// once; no ingestion is created, so there is nothing to retry or clean up.
    /// </response>
    [HttpPost]
    [ProducesResponseType<IngestionAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit([FromBody] IngestionRequest request, CancellationToken ct)
    {
        // Validate before anything durable happens: an invalid submission must
        // leave no trace at all, not a Failed row discovered minutes later.
        var errors = IngestionRequestValidation.Validate(request);
        if (errors.Count > 0)
            return ValidationProblem(new ValidationProblemDetails(errors));

        var ingestionId = await store.CreateQueuedAsync(request, ct);
        await queue.Writer.WriteAsync(ingestionId, ct);
        return Accepted($"/ingestions/{ingestionId}", new IngestionAccepted { IngestionId = ingestionId });
    }

    /// <summary>Returns the current status of one Ingestion.</summary>
    /// <param name="id">The ingestion id returned by the submit call.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">The ingestion exists; the body carries its lifecycle state.</response>
    /// <response code="404">No ingestion with this id exists.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<IngestionStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(Guid id, CancellationToken ct) =>
        await store.GetStatusAsync(id, ct) is { } status ? Ok(status) : NotFound();
}
