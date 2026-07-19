using MedicalAssistance.Ingestion.Api.Ingestions;
using Microsoft.AspNetCore.Mvc;

namespace MedicalAssistance.Ingestion.Api.Controllers;

/// <summary>
/// Removing a Document from the record. The counterpart to the patient document
/// list: that view is where a wrong-patient upload is spotted, and this is how it
/// is taken out.
/// </summary>
[ApiController]
[Route("documents")]
[Produces("application/json")]
public sealed class DocumentsController(IngestionStore store) : ControllerBase
{
    /// <summary>Un-ingests a Document: removes its chunks and raw payload, leaving a Deleted tombstone.</summary>
    /// <remarks>
    /// The canonical case is a wrong-patient upload. Removal is complete — every
    /// chunk and the stored transcript are gone in one transaction — and
    /// accountable: the ingestion row survives as a <c>Deleted</c> tombstone
    /// recording who removed the document and when. A document that simply
    /// disappeared would be worse than the mistake this corrects.
    ///
    /// The <c>documentId</c> is the identifier the patient document list returns.
    /// It contains <c>#</c> separators, so a caller must percent-encode it in the
    /// path (<c>#</c> as <c>%23</c>).
    ///
    /// Removing even the tombstone is GDPR Erasure, a separate operation guarded
    /// by its own admin secret.
    /// </remarks>
    /// <param name="documentId">The Document to remove, as listed by <c>GET /patients/{patientId}/documents</c> (percent-encoded).</param>
    /// <param name="removedBy">
    /// Who is removing the document. Required: an un-ingestion has to be
    /// accountable, and this service has no user identity of its own — the acting
    /// user is named by the trusted backend (ADR-0007).
    /// </param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">The document was removed; the body carries the tombstone details.</response>
    /// <response code="400"><c>removedBy</c> was not supplied.</response>
    /// <response code="404">No live document with this id exists — the id is unknown, or it was already removed.</response>
    /// <response code="409">
    /// A version of this document is still Queued or Processing. Wait for it to
    /// finish (poll <c>GET /ingestions/{id}</c>) and remove it then.
    /// </response>
    [HttpDelete("{documentId}")]
    [ProducesResponseType<DocumentUnIngested>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UnIngest(
        string documentId, [FromQuery] string? removedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(removedBy))
        {
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["removedBy"] = ["removedBy is required: an un-ingestion must record who removed the document."],
            }));
        }

        var (outcome, deletedAt) = await store.TryUnIngestAsync(documentId, removedBy, ct);
        return outcome switch
        {
            UnIngestOutcome.Deleted => Ok(new DocumentUnIngested
            {
                DocumentId = documentId,
                RemovedBy = removedBy,
                DeletedAt = deletedAt!.Value,
            }),

            UnIngestOutcome.InFlight => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "This document is still being ingested.",
                detail: "A version of this document is Queued or Processing. It cannot be removed until that run " +
                        "reaches a terminal state; poll GET /ingestions/{id} and remove it once it has finished."),

            _ => NotFound(),
        };
    }
}
