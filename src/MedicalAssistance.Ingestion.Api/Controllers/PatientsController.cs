using MedicalAssistance.Ingestion.Api.Ingestions;
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
}
