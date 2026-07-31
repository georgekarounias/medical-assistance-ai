using MedicalAssistance.Ingestion.Api.Retrieval;

namespace MedicalAssistance.Ingestion.Api.Chat;

/// <summary>
/// The stateless orchestration behind the chat endpoint: map the request onto a
/// patient-scoped retrieval, generate an answer over the evidence, and package the
/// evidence as citations. It stores nothing (ADR-0010) — same patient, same
/// question, same record in, same answer out.
/// </summary>
public interface IGroundedAnswerService
{
    /// <summary>Answers one turn for the patient named in the route.</summary>
    Task<ChatAnswerResponse> AnswerAsync(string patientId, ChatAnswerRequest request, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class GroundedAnswerService(
    IRetrievalService retrieval, IGroundedAnswerGenerator generator) : IGroundedAnswerService
{
    private const int DefaultTopK = 8;

    // A citation carries a quote, not the whole chunk — enough to show the doctor
    // what the answer rests on without shipping a full document back per hit.
    private const int MaxQuoteLength = 500;

    public async Task<ChatAnswerResponse> AnswerAsync(
        string patientId, ChatAnswerRequest request, CancellationToken cancellationToken)
    {
        // The controller guarantees a non-blank question before we get here.
        var question = request.Question!;
        var language = QuestionLanguage.Detect(question);

        var retrievalRequest = new RetrievalRequest
        {
            PatientId = patientId,
            Question = question,
            DoctorId = request.DoctorId,
            TopK = request.TopK ?? DefaultTopK,
            Filters = new RetrievalFilters
            {
                DoctorId = request.Filters?.DoctorId,
                DocumentType = request.Filters?.DocumentType,
                From = request.Filters?.From,
                To = request.Filters?.To,
                SessionId = request.Filters?.SessionId,
                Language = request.Filters?.Language,
            },
            // Conversation context flows into retrieval for query refinement only (T44);
            // it never becomes evidence and is never stored (ADR-0010).
            RecentTurns = request.RecentTurns?.Select(t => new ConversationTurn(t.Role, t.Text)).ToList(),
            PriorSummary = request.PriorSummary,
        };

        var result = await retrieval.SearchAsync(retrievalRequest, cancellationToken);

        var citations = result.Evidence
            .Select((evidence, index) => new ChatCitation
            {
                Label = $"E{index + 1}",
                ChunkId = evidence.ChunkId,
                DocumentId = evidence.DocumentId,
                DocumentType = evidence.DocumentType,
                SessionId = evidence.SessionId,
                DocumentDate = evidence.DocumentDate,
                SourceRef = evidence.SourceRef,
                Quote = Bound(evidence.VerbatimText),
                Score = evidence.Score,
            })
            .ToList();

        var answer = await generator.GenerateAsync(
            new GroundedAnswerContext(question, language, result.Evidence, request.RecentTurns, request.PriorSummary),
            cancellationToken);

        // Refusal (T45) and citation verification (T46) are not wired yet: this
        // returns the generated answer with every retrieved item as a citation. T46
        // will reconcile citations to those the answer actually references.
        return new ChatAnswerResponse
        {
            Answer = answer,
            Refused = false,
            RetrievalUsed = true,
            Language = language,
            Citations = citations,
        };
    }

    private static string Bound(string text) =>
        text.Length <= MaxQuoteLength ? text : text[..MaxQuoteLength];
}
