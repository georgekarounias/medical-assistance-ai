namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// LabReport Tier 1: turns an extracted PDF into searchable Panel Renditions with
/// no LLM anywhere (ADR-0006). Each extracted table is one Panel, rendered
/// deterministically into readable text — one chunk per Panel, never per analyte —
/// so a question like "what were her last blood results?" is answerable from the
/// actual report with zero hallucination risk by construction.
///
/// Values are copied verbatim from the extracted cells; the only thing code adds
/// is layout (the label separator and spacing), never a number or a word of
/// clinical content. Reading columns semantically ("this is the reference range")
/// is Tier 2's job, where an agent classifies columns and code verifies each value.
/// </summary>
public static class LabReportRenderer
{
    /// <summary>Renders each Panel of an extracted lab report into one chunk; empty when nothing is renderable.</summary>
    public static IReadOnlyList<AssembledChunk> Render(ExtractedDocument extracted)
    {
        var chunks = new List<AssembledChunk>();

        for (var table = 0; table < extracted.Tables.Count; table++)
        {
            var panelText = RenderPanel(extracted.Tables[table]);
            if (string.IsNullOrWhiteSpace(panelText))
                continue;

            chunks.Add(new AssembledChunk(
                Kind: "labPanel",
                VerbatimText: panelText,
                ContextBlurb: null,
                SourceRefJson: $$"""{"table":{{table}}}""",
                EmbeddingInput: panelText));
        }

        // No tables (or all empty) but there is plain text — a report whose layout
        // the extractor did not read as a grid. Keep it searchable as one chunk
        // rather than dropping the whole report.
        if (chunks.Count == 0 && !string.IsNullOrWhiteSpace(extracted.Text))
        {
            var text = extracted.Text.Trim();
            chunks.Add(new AssembledChunk("labPanel", text, null, """{"text":true}""", text));
        }

        return chunks;
    }

    private static string RenderPanel(ExtractedTable table) =>
        string.Join("\n", table.Rows.Select(RenderRow).Where(line => line.Length > 0));

    // "Hemoglobin: 13.2 g/dL 13.5-17.5 LOW" — the first non-empty cell labels the
    // row, the rest are its values, joined as they were extracted.
    private static string RenderRow(IReadOnlyList<string> cells)
    {
        var values = cells.Select(cell => (cell ?? string.Empty).Trim()).Where(cell => cell.Length > 0).ToList();
        return values.Count switch
        {
            0 => string.Empty,
            1 => values[0],
            _ => $"{values[0]}: {string.Join(" ", values.Skip(1))}",
        };
    }
}
