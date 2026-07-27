using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The Ingestion Strategy for LabReport documents. The base64 PDF is decoded and
/// handed to the extraction seam (Azure Document Intelligence in production, a fake
/// in tests — ADR-0005), then two tiers run:
///
/// <list type="bullet">
/// <item>Tier 1 — code renders each extracted table into a searchable Panel
/// Rendition with no LLM (ADR-0006). This must succeed or the ingestion fails
/// honestly: a report that extracts to nothing renderable is Failed, not silently
/// empty.</item>
/// <item>Tier 2 — a mapping agent classifies columns and names analytes, then code
/// copies the values from the cells it pointed at and verifies each appears
/// verbatim in the source grid. All-or-nothing: any unverifiable row stores zero
/// analyte rows and flags <c>analytesExtracted=false</c>, so a trend query never
/// sees a partial panel. A failed Tier 2 does not fail the ingestion — the panels
/// are still stored and searchable, and the report can be re-processed.</item>
/// </list>
///
/// Its identity is the backend-assigned reportId, so a re-POST is a Correction; the
/// commit replaces the previous version's panels and analyte rows together (T31).
/// </summary>
public sealed class LabReportStrategy(
    IDocumentExtractor extractor,
    IChatClient chatClient,
    AgentInstructionProvider instructionProvider,
    DocumentChunkCommitter committer) : IIngestionStrategy
{
    /// <inheritdoc />
    public string DocumentType => DocumentTypes.LabReport;

    /// <inheritdoc />
    public async Task IngestAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct)
    {
        // Validation has already confirmed the payload is a present, size-capped,
        // valid-base64 PDF, so this decode cannot throw on a submitted document.
        var pdf = Convert.FromBase64String(request.PdfContent!);
        var extracted = await extractor.ExtractAsync(pdf, ct);

        // Tier 1: deterministic panels. Must produce something, or fail honestly.
        var panels = LabReportRenderer.Render(extracted);
        if (panels.Count == 0)
            throw new InvalidOperationException(
                "The lab report produced no readable panels; extraction returned no tables or text.");

        // Tier 2: the agent maps, code copies and verifies. A missing or unverifiable
        // mapping is not a failure of the ingestion — it stores zero analyte rows and
        // flags the report, which is queryable and re-processable.
        var (analytes, instructionVersion, chatModel) = await TryMapAnalytesAsync(extracted, ct);
        var analytesExtracted = analytes is not null;

        // A LabReport has no chunking agent to produce a summary the way the prose
        // types do, so one is written here from the rendered panels — for the
        // ingestion's summary field and the patient's rolling overview. Best-effort,
        // like Tier 2: a summariser failure leaves the field null but never fails an
        // ingestion whose panels already succeeded. The summary is the field only,
        // never a chunk — the vector store stays verbatim (ADR-0006).
        var summary = await TrySummarizeAsync(panels, ct);

        await committer.CommitAsync(
            ingestionId, request, panels, instructionVersion, chatModel,
            analytes, analytesExtracted, documentSummary: summary, ct);
    }

    private async Task<string?> TrySummarizeAsync(IReadOnlyList<AssembledChunk> panels, CancellationToken ct)
    {
        try
        {
            var (instructions, _) = instructionProvider.Get(AgentNames.LabReportSummarizer);
            var summarizer = chatClient.AsAIAgent(name: AgentNames.LabReportSummarizer, instructions: instructions);

            var report = string.Join("\n\n", panels.Select(panel => panel.VerbatimText));
            var response = await summarizer.RunAsync($"Summarise this laboratory report:\n\n{report}", cancellationToken: ct);

            var summary = response.Text.Trim();
            return summary.Length == 0 ? null : summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort: the panels are already committed and searchable.
            return null;
        }
    }

    private async Task<(IReadOnlyList<VerifiedAnalyte>? Analytes, int Version, string ChatModel)> TryMapAnalytesAsync(
        ExtractedDocument extracted, CancellationToken ct)
    {
        var (instructions, version) = instructionProvider.Get(AgentNames.LabAnalyteMapper);
        var chatModel = (chatClient.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.DefaultModelId
            ?? "unknown";
        var mapper = chatClient.AsAIAgent(name: AgentNames.LabAnalyteMapper, instructions: instructions);

        try
        {
            var response = await mapper.RunAsync(LabAnalyteMapping.BuildPrompt(extracted), cancellationToken: ct);

            // Verification is code's job, not the model's — a bad answer yields null
            // (no rows), exactly like the all-or-nothing rule wants.
            return (LabAnalyteMapping.TryVerify(extracted, response.Text), version, chatModel);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Tier 2 is best-effort (tiered honesty): a mapping-agent failure stores
            // zero analyte rows and flags the report, but never fails an ingestion
            // whose Tier 1 panels already succeeded and are searchable.
            return (null, version, chatModel);
        }
    }
}
