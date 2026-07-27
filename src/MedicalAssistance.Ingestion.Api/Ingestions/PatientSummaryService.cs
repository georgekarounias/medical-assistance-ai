using System.Text;
using Microsoft.Extensions.AI;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Keeps one rolling overview per patient in step with their documents: after an
/// ingestion completes, it folds the patient's per-document summaries into a single
/// evolving overview (ADR-0008 agent, DB-owned instructions) and stores it.
///
/// Derived and best-effort. The document is already committed and searchable by the
/// time this runs, so a summariser failure is logged and swallowed — it never fails
/// an ingestion, and the next ingestion for the patient regenerates from the full
/// current set of documents anyway. Regeneration is serialized per patient with a
/// single-key advisory lock so two ingestions of the same patient finishing at once
/// cannot race a stale overview over a fuller one.
/// </summary>
public sealed class PatientSummaryService(
    NpgsqlDataSource dataSource,
    IngestionStore store,
    IChatClient chatClient,
    AgentInstructionProvider instructionProvider,
    ILogger<PatientSummaryService> logger)
{
    /// <summary>
    /// Regenerates and stores the patient's rolling overview from their live
    /// documents. Never throws for a summariser or model failure — the ingestion
    /// that triggered it has already succeeded.
    /// </summary>
    public async Task RegenerateAsync(string patientId, CancellationToken ct)
    {
        try
        {
            // The lock lives on its own connection for the length of the critical
            // section; the store's reads and the write use the pooled context.
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var _ = await PostgresAdvisoryLock.AcquireAsync(
                connection, PostgresAdvisoryLock.PatientSummaryKey(patientId), ct);

            var documents = await store.ListCompletedDocumentSummariesAsync(patientId, ct);
            if (documents.Count == 0)
                return;

            var (instructions, version) = instructionProvider.Get(AgentNames.PatientSummarizer);
            var chatModel = (chatClient.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.DefaultModelId;
            var agent = chatClient.AsAIAgent(name: AgentNames.PatientSummarizer, instructions: instructions);

            var response = await agent.RunAsync(BuildPrompt(documents), cancellationToken: ct);
            var overview = response.Text.Trim();
            if (overview.Length == 0)
            {
                logger.LogWarning("PatientSummarizer returned an empty overview for patient {PatientId}", patientId);
                return;
            }

            await store.UpsertPatientSummaryAsync(patientId, overview, documents.Count, chatModel, version, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Best-effort: the ingestion is already committed and searchable.
            logger.LogError(exception, "Rolling summary regeneration failed for patient {PatientId}", patientId);
        }
    }

    // The per-document summaries as a dated timeline. A document whose type produces
    // no summary (a LabReport) still appears as a line so the overview knows it
    // exists, even though it carries no prose to fold in.
    private static string BuildPrompt(IReadOnlyList<PatientDocumentSummary> documents)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "Below are dated summaries of every document held for one patient, oldest first. " +
            "Write a single concise clinical overview of the patient across all of them.");
        builder.AppendLine();
        foreach (var document in documents)
        {
            var date = document.DocumentDate?.ToString("yyyy-MM-dd") ?? "undated";
            var text = string.IsNullOrWhiteSpace(document.Summary)
                ? "(no summary for this document type)"
                : document.Summary;
            builder.AppendLine($"- [{date}] {document.DocumentType}: {text}");
        }

        return builder.ToString();
    }
}
