using System.Text;
using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Retrieval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Chat;

/// <summary>What generation needs: the question, its language, the evidence, and the (input-only) conversation context.</summary>
public sealed record GroundedAnswerContext(
    string Question,
    string Language,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<ChatTurn>? RecentTurns,
    string? PriorSummary);

/// <summary>
/// The generation seam of the answer path. Kept behind an interface so the safety
/// and DB-seeding work can land without reshaping the orchestration: T43 replaces
/// the implementation with the DB-seeded grounded agent (ADR-0008) — one fixed
/// clinical voice, answer only from the [E#] evidence, in the question's language —
/// and T46 adds citation verification around it.
/// </summary>
public interface IGroundedAnswerGenerator
{
    /// <summary>Writes the answer for one turn from the supplied evidence.</summary>
    Task<string> GenerateAsync(GroundedAnswerContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The grounded-answer generator: a Microsoft Agent Framework agent over the shared
/// <see cref="IChatClient"/>, built with the DB-seeded <c>GroundedChat</c> prompt
/// (ADR-0008) — one fixed clinical voice, answer only from the [E#] evidence, in the
/// question's language. The instruction text is owned by the database and edited
/// there; code holds no copy. The safety net around it (refusal T45, verification
/// T46) is not here yet.
/// </summary>
public sealed class GroundedAnswerGenerator(
    IChatClient chatClient, AgentInstructionProvider instructionProvider) : IGroundedAnswerGenerator
{
    public async Task<string> GenerateAsync(GroundedAnswerContext context, CancellationToken cancellationToken)
    {
        // Instructions come from the singleton loaded at startup (ADR-0008): a prompt
        // edit takes effect on the next restart, never mid-flight.
        var (instructions, _) = instructionProvider.Get(AgentNames.GroundedChat);
        var agent = chatClient.AsAIAgent(name: AgentNames.GroundedChat, instructions: instructions);
        var response = await agent.RunAsync(BuildPrompt(context), cancellationToken: cancellationToken);
        return response.Text.Trim();
    }

    private static string BuildPrompt(GroundedAnswerContext context)
    {
        var prompt = new StringBuilder();

        // Conversation context frames the question; it is never presented as evidence.
        if (!string.IsNullOrWhiteSpace(context.PriorSummary))
            prompt.Append("Conversation summary: ").AppendLine(context.PriorSummary).AppendLine();
        if (context.RecentTurns is { Count: > 0 })
        {
            foreach (var turn in context.RecentTurns)
                prompt.Append(turn.Role).Append(": ").AppendLine(turn.Text);
            prompt.AppendLine();
        }

        prompt.Append("Question: ").AppendLine(context.Question).AppendLine();

        prompt.AppendLine("Evidence Items:");
        for (var i = 0; i < context.Evidence.Count; i++)
            prompt.Append("[E").Append(i + 1).Append("] ").AppendLine(context.Evidence[i].VerbatimText);

        return prompt.ToString();
    }
}
