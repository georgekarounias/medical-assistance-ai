using MedicalAssistance.Ingestion.Api.Chat;
using Microsoft.AspNetCore.Mvc;

namespace MedicalAssistance.Ingestion.Api.Controllers;

/// <summary>
/// The grounded-chat surface: the one public endpoint of the retrieval feature.
/// Secret-authenticated by the fallback policy like every other endpoint, and
/// stateless — it stores no conversation (ADR-0010). The patient in the route is
/// the hard boundary every retrieval is scoped to.
/// </summary>
[ApiController]
[Route("patients")]
[Produces("application/json")]
public sealed class ChatController(IGroundedAnswerService answers) : ControllerBase
{
    /// <summary>Answers a question about one patient, grounded in that patient's own record.</summary>
    /// <remarks>
    /// Runs a patient-scoped retrieval over the stored chunks and returns a cited
    /// answer — or, once the safety net lands, an honest insufficient-evidence
    /// refusal. Conversation context (<c>recentTurns</c>, <c>priorSummary</c>) is
    /// accepted as input to interpret the question, but nothing is stored: the same
    /// inputs always produce the same answer.
    /// </remarks>
    /// <param name="patientId">The patient the question is about — the retrieval boundary.</param>
    /// <param name="request">The question, the asking doctor, and optional narrowing and context.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">A grounded answer with its citations (or, later, a refusal).</response>
    /// <response code="400">The question was missing or blank.</response>
    /// <response code="401">No valid secret was presented.</response>
    [HttpPost("{patientId}/chat/answer")]
    [ProducesResponseType<ChatAnswerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Answer(
        string patientId, [FromBody] ChatAnswerRequest? request, CancellationToken ct)
    {
        // Minimal gate for T42: a question is required. The full field-level contract
        // (topK range, malformed filters, garbled question) is T48.
        if (request is null || string.IsNullOrWhiteSpace(request.Question))
        {
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["question"] = ["A question is required."],
            }));
        }

        return Ok(await answers.AnswerAsync(patientId, request, ct));
    }
}
