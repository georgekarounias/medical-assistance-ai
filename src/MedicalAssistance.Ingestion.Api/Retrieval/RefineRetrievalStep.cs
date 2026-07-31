using System.Text;
using MedicalAssistance.Ingestion.Api.Ingestions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Retrieval;

/// <summary>
/// The Refine step (Order 20): optionally rewrites the question into a cleaner
/// search query, using the conversation context to resolve pronouns and references.
/// It touches only <see cref="RetrievalContext.EffectiveQuery"/> — the query vector,
/// never the answer's grounding.
///
/// Config-gated and fail-open (T44): when disabled, or when refinement fails or
/// returns nothing usable, the raw question stays the effective query. Refinement is
/// a recall aid, never a hard dependency — a failure here must never fail the turn.
/// Its prompt is DB-seeded like every other agent (ADR-0008).
/// </summary>
public sealed class RefineRetrievalStep(
    IChatClient chatClient,
    AgentInstructionProvider instructionProvider,
    IConfiguration configuration,
    ILogger<RefineRetrievalStep> logger) : IRetrievalStep
{
    /// <summary>Config key that turns refinement on; off by default (opt-in recall improvement).</summary>
    public const string EnabledConfigurationKey = "Retrieval:QueryRefinement:Enabled";

    public int Order => RetrievalStepOrder.Refine;

    public async Task ExecuteAsync(RetrievalContext context, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue(EnabledConfigurationKey, false))
            return;

        try
        {
            var (instructions, _) = instructionProvider.Get(AgentNames.QueryRefinement);
            var agent = chatClient.AsAIAgent(name: AgentNames.QueryRefinement, instructions: instructions);
            var response = await agent.RunAsync(BuildPrompt(context), cancellationToken: cancellationToken);

            var refined = response.Text.Trim();
            if (!string.IsNullOrWhiteSpace(refined))
                context.EffectiveQuery = refined;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall back to the raw question. No patient text in the log (ADR-0009) —
            // only that refinement fell back, which is a recall note, not an error.
            logger.LogWarning(ex, "Query refinement failed; falling back to the raw question.");
        }
    }

    private static string BuildPrompt(RetrievalContext context)
    {
        var request = context.Request;
        var prompt = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(request.PriorSummary))
            prompt.Append("Conversation summary: ").AppendLine(request.PriorSummary);
        if (request.RecentTurns is { Count: > 0 })
            foreach (var turn in request.RecentTurns)
                prompt.Append(turn.Role).Append(": ").AppendLine(turn.Text);
        prompt.AppendLine();

        prompt.Append("Question: ").AppendLine(request.Question);
        return prompt.ToString();
    }
}
