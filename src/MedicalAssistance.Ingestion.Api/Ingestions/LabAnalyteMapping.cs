using System.Text;
using System.Text.Json;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>One analyte whose value was copied from the source grid and verified verbatim (ADR-0006).</summary>
/// <param name="CanonicalName">Agent-assigned canonical name (the one mapping the agent is allowed to produce).</param>
/// <param name="VerbatimName">The name as printed, copied from the source cell.</param>
/// <param name="Value">The value, copied verbatim from the source cell.</param>
/// <param name="Unit">Unit as printed, copied — null if no unit column.</param>
/// <param name="ReferenceRange">Reference range as printed, copied — null if none.</param>
/// <param name="Flag">Flag as printed, copied — null if none.</param>
/// <param name="TableIndex">Provenance: which extracted table.</param>
/// <param name="RowIndex">Provenance: which row of it.</param>
public sealed record VerifiedAnalyte(
    string CanonicalName, string VerbatimName, string Value,
    string? Unit, string? ReferenceRange, string? Flag, int TableIndex, int RowIndex);

/// <summary>
/// LabReport Tier 2 (ADR-0006): the agent maps, code copies, code verifies. The
/// mapping agent only classifies columns and names analytes — it never emits a
/// value. Code reads the values from the cells the agent pointed at and verifies
/// each appears verbatim in the source grid, so a generative model can never put a
/// number a doctor will read into the store.
///
/// All-or-nothing (tiered honesty): if any mapped row cannot be verified — a
/// column or row index out of range, a missing value, an unreadable response —
/// <see cref="TryVerify"/> returns null and the strategy stores zero analyte rows
/// with <c>analytesExtracted=false</c>, so a trend query never sees a partial panel.
/// </summary>
public static class LabAnalyteMapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Builds the mapping prompt: the extracted tables as indexed cell grids for the agent to classify.</summary>
    public static string BuildPrompt(ExtractedDocument extracted)
    {
        var prompt = new StringBuilder(
            "Classify the columns of each table and name each data row's analyte. " +
            "Tables (rows and columns are 0-indexed):\n");
        for (var table = 0; table < extracted.Tables.Count; table++)
        {
            prompt.Append($"Table {table}:\n");
            var rows = extracted.Tables[table].Rows;
            for (var row = 0; row < rows.Count; row++)
            {
                var cells = rows[row].Select((cell, column) => $"[{column}]={cell}");
                prompt.Append($"  Row {row}: {string.Join(" | ", cells)}\n");
            }
        }
        return prompt.ToString();
    }

    /// <summary>
    /// Verifies the agent's mapping against the source grid and returns the analytes
    /// whose values were copied and confirmed verbatim — or null if any row fails,
    /// because the rows are all-or-nothing.
    /// </summary>
    public static IReadOnlyList<VerifiedAnalyte>? TryVerify(ExtractedDocument extracted, string agentResponse)
    {
        AnalyteMappingResponse? mapping;
        try
        {
            mapping = JsonSerializer.Deserialize<AnalyteMappingResponse>(AgentResponse.Unfence(agentResponse), Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (mapping?.Tables is not { Count: > 0 })
            return null;

        var verified = new List<VerifiedAnalyte>();
        foreach (var table in mapping.Tables)
        {
            if (table.TableIndex < 0 || table.TableIndex >= extracted.Tables.Count)
                return null;
            var grid = extracted.Tables[table.TableIndex].Rows;
            if (table.Analytes is not { Count: > 0 })
                return null;

            foreach (var analyte in table.Analytes)
            {
                if (analyte.RowIndex < 0 || analyte.RowIndex >= grid.Count)
                    return null;
                if (string.IsNullOrWhiteSpace(analyte.CanonicalName))
                    return null;

                var row = grid[analyte.RowIndex];
                var value = Cell(row, table.ValueColumn);
                // A row with no value is not a verifiable analyte; and the value has
                // to be a genuine cell of the source grid, never something invented.
                if (value is null || !AppearsVerbatim(extracted, value))
                    return null;

                var name = Cell(row, table.NameColumn);
                verified.Add(new VerifiedAnalyte(
                    CanonicalName: analyte.CanonicalName.Trim(),
                    VerbatimName: name ?? analyte.CanonicalName.Trim(),
                    Value: value,
                    Unit: Cell(row, table.UnitColumn),
                    ReferenceRange: Cell(row, table.ReferenceColumn),
                    Flag: Cell(row, table.FlagColumn),
                    TableIndex: table.TableIndex,
                    RowIndex: analyte.RowIndex));
            }
        }

        return verified.Count > 0 ? verified : null;
    }

    // The trimmed cell at a column, or null when the column is absent or empty. A
    // required column that points past the row's cells therefore fails the row.
    private static string? Cell(IReadOnlyList<string> row, int? column)
    {
        if (column is not { } index || index < 0 || index >= row.Count)
            return null;
        var value = (row[index] ?? string.Empty).Trim();
        return value.Length == 0 ? null : value;
    }

    // The value must be a genuine cell somewhere in the extracted grids — the
    // verbatim check that makes a copied value provably source-derived, not fabricated.
    private static bool AppearsVerbatim(ExtractedDocument extracted, string value) =>
        extracted.Tables.Any(table => table.Rows.Any(row => row.Any(cell => (cell ?? string.Empty).Trim() == value)));

    private sealed record AnalyteMappingResponse(List<TableMapping> Tables);

    private sealed record TableMapping(
        int TableIndex, int NameColumn, int ValueColumn,
        int? UnitColumn, int? ReferenceColumn, int? FlagColumn, List<AnalyteRowMapping> Analytes);

    private sealed record AnalyteRowMapping(int RowIndex, string CanonicalName);
}
