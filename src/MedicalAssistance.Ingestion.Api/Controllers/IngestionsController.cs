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
    ///
    /// Re-posting is safe. Content already ingested for a patient is never
    /// ingested twice: the response carries <c>duplicate: true</c> and the id of
    /// the ingestion that already holds it, and nothing is reprocessed. This
    /// holds even when the document is re-filed under a different sessionId or
    /// sequenceNumber, because those describe where a document sits in a
    /// session's numbering rather than what it contains. The same content for a
    /// different patient is a different document and is ingested normally.
    ///
    /// Re-posting content whose earlier ingestion failed retries that same
    /// ingestion. Different content for the same identity (sessionId +
    /// sequenceNumber) is a correction and is ingested as new work.
    /// </remarks>
    /// <param name="request">The document payload with its declared type, clinical identifiers, and content.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="202">
    /// The body carries the ingestion id to poll. Either the document was queued
    /// for processing, or <c>duplicate: true</c> says this exact content was
    /// already ingested and nothing was reprocessed.
    /// </response>
    /// <response code="400">
    /// The payload is malformed or breaks the submission contract. The body is a
    /// problem document whose <c>errors</c> map names every offending field at
    /// once; no ingestion is created, so there is nothing to retry or clean up.
    /// </response>
    /// <response code="409">
    /// An ingestion for this document is still Queued or Processing. The problem
    /// document carries its <c>ingestionId</c>: poll that one rather than
    /// resubmitting, and submit again only if it ends as Failed.
    /// </response>
    [HttpPost]
    [ProducesResponseType<IngestionAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Submit([FromBody] IngestionRequest request, CancellationToken ct)
    {
        // Validate before anything durable happens: an invalid submission must
        // leave no trace at all, not a Failed row discovered minutes later.
        var errors = IngestionRequestValidation.Validate(request);
        if (errors.Count > 0)
            return ValidationProblem(new ValidationProblemDetails(errors));

        // Nothing can be decided about a document that has not landed yet, and
        // two workers on one document would race to write its chunk set — so a
        // submission that is still in flight blocks its own resubmission.
        if (await store.FindInFlightAsync(request, ct) is { } inFlightId)
            return AlreadyInFlight(inFlightId);

        // Re-posting content that is already ingested is never new knowledge.
        // After success it is a no-op; after failure it is a retry of the very
        // same ingestion, so the id the caller already holds stays valid and a
        // poison document cannot multiply rows.
        // (While one is still Queued or Processing, T15 turns this into a 409.)
        switch (await store.FindIdenticalAsync(request, ct))
        {
            case { Succeeded: true } completed:
                return Accepted(LocationOf(completed.Id), new IngestionAccepted
                {
                    IngestionId = completed.Id,
                    Duplicate = true,
                });

            case { Failed: true } failed:
                await store.RequeueAsync(failed.Id, ct);
                await queue.Writer.WriteAsync(failed.Id, ct);
                return Accepted(LocationOf(failed.Id), new IngestionAccepted { IngestionId = failed.Id });
        }

        // The same content can also arrive re-filed under a different session or
        // sequence number. It is still the same knowledge about the same
        // patient, so it is skipped too, and the caller is pointed at the
        // ingestion that already holds it.
        if (await store.FindSameContentElsewhereAsync(request, ct) is { } alreadyIngested)
        {
            return Accepted(LocationOf(alreadyIngested.Id), new IngestionAccepted
            {
                IngestionId = alreadyIngested.Id,
                Duplicate = true,
            });
        }

        var ingestionId = await store.CreateQueuedAsync(request, ct);
        await queue.Writer.WriteAsync(ingestionId, ct);
        return Accepted(LocationOf(ingestionId), new IngestionAccepted { IngestionId = ingestionId });
    }

    /// <summary>Returns the current status of one Ingestion.</summary>
    /// <param name="id">The ingestion id returned by the submit call.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">
    /// The ingestion exists; the body carries its lifecycle state. <c>Superseded</c>
    /// means a later correction of the same document replaced this version's chunks.
    /// </response>
    /// <response code="404">No ingestion with this id exists.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<IngestionStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(Guid id, CancellationToken ct) =>
        await store.GetStatusAsync(id, ct) is { } status ? Ok(status) : NotFound();

    private static string LocationOf(Guid ingestionId) => $"/ingestions/{ingestionId}";

    /// <summary>
    /// 409 carrying the id of the ingestion already in flight, so the caller can
    /// poll the work that is running instead of being told only "no".
    /// </summary>
    private ObjectResult AlreadyInFlight(Guid ingestionId)
    {
        var conflict = Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "An ingestion for this document is already in progress.",
            detail: "The document was accepted earlier and has not finished yet. " +
                    "Poll it at GET /ingestions/{id}; submit again only if it ends as Failed.");
        ((ProblemDetails)conflict.Value!).Extensions["ingestionId"] = ingestionId;
        return conflict;
    }
}
