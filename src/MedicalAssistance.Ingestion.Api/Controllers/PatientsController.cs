using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedicalAssistance.Ingestion.Api.Controllers;

/// <summary>
/// The patient-scoped view of the record: what this service holds about one
/// patient, and — from Un-ingest onward — how to take something out of it.
/// </summary>
[ApiController]
[Route("patients")]
[Produces("application/json")]
public sealed class PatientsController(IngestionStore store) : ControllerBase
{
    /// <summary>Lists every Document this service holds for a patient.</summary>
    /// <remarks>
    /// One row per document in the state its most recent ingestion left it, most
    /// recent clinical date first. Corrections and earlier failed attempts
    /// collapse into the document they belong to, so a count here is a count of
    /// transcripts rather than of uploads.
    ///
    /// Documents whose ingestion failed are included on purpose: a doctor who
    /// believes a session was recorded when it was not would ask questions about
    /// a transcript that does not exist.
    ///
    /// A document belongs to the doctor who filed it, so a patient seen by two
    /// doctors has both doctors' transcripts here. Pass <c>doctorId</c> to see
    /// one doctor's.
    /// </remarks>
    /// <param name="patientId">The patient whose documents to list.</param>
    /// <param name="doctorId">
    /// Optional. Narrows the list to documents filed by this doctor. A filter,
    /// not a permission check — this service does not decide who may see what
    /// (ADR-0007). Omit it for the patient's whole record.
    /// </param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">The patient's documents; an empty list if none are held.</response>
    [HttpGet("{patientId}/documents")]
    [ProducesResponseType<IReadOnlyList<PatientDocument>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDocuments(
        string patientId, [FromQuery] string? doctorId, CancellationToken ct) =>
        Ok(await store.ListPatientDocumentsAsync(patientId, doctorId, ct));

    /// <summary>Erases everything the service holds about a patient — the GDPR right to be forgotten.</summary>
    /// <remarks>
    /// Removes every chunk and every ingestion row for the patient, the Deleted
    /// tombstones un-ingest leaves included, and writes one irreversible
    /// erasure-log entry recording who erased the patient and when. That log is
    /// the one thing erasure keeps: its subject is gone, so the act itself has to
    /// stay accountable.
    ///
    /// Guarded by the separate admin secret (ADR-0007): a leaked everyday key can
    /// read and un-ingest, but only the admin key satisfies this endpoint, so it
    /// alone can erase a patient. Present the admin secret in the same
    /// <c>X-Api-Key</c> header.
    ///
    /// It guarantees an end state rather than acting on a precondition — a patient
    /// the service holds nothing about is erased of zero rows and still logged —
    /// so an erasure request always succeeds with a count, and repeating it is
    /// safe.
    /// </remarks>
    /// <param name="patientId">The patient whose data to erase.</param>
    /// <param name="erasedBy">
    /// Who is performing the erasure. Required: erasure is a logged administrative
    /// act, and this service has no user identity of its own — the actor is named
    /// by the trusted backend (ADR-0007).
    /// </param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">The patient's data was erased; the body carries the counts removed.</response>
    /// <response code="400"><c>erasedBy</c> was not supplied.</response>
    /// <response code="401">No secret was presented.</response>
    /// <response code="403">The presented secret is valid but is not the admin secret erasure requires.</response>
    [HttpDelete("{patientId}/data")]
    [Authorize(Policy = ApiKeyAuthentication.ErasurePolicyName)]
    [ProducesResponseType<PatientDataErased>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> EraseData(
        string patientId, [FromQuery] string? erasedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(erasedBy))
        {
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["erasedBy"] = ["erasedBy is required: an erasure must record who performed it."],
            }));
        }

        var erasedAt = DateTimeOffset.UtcNow;
        var (ingestionsErased, chunksErased) = await store.ErasePatientDataAsync(patientId, erasedBy, ct);
        return Ok(new PatientDataErased
        {
            PatientId = patientId,
            ErasedBy = erasedBy,
            ErasedAt = erasedAt,
            IngestionsErased = ingestionsErased,
            ChunksErased = chunksErased,
        });
    }
}
