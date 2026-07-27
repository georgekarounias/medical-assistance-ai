using MedicalAssistance.Ingestion.Api.Ingestions;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// LabReport Tier 2 verification (ADR-0006): the agent maps, code copies, code
/// verifies. Values are read from the cells the agent pointed at and must appear
/// verbatim in the source grid; if any mapped row cannot be verified, the whole
/// set is rejected (all-or-nothing) so a trend query never sees a partial panel.
/// </summary>
public class LabAnalyteMappingTests
{
    private static readonly ExtractedDocument Cbc = new(
        "Complete Blood Count",
        [
            new ExtractedTable(
            [
                ["Analyte", "Value", "Reference", "Flag"],
                ["Hemoglobin", "13.2 g/dL", "13.5-17.5", "LOW"],
                ["WBC", "6.1 10^9/L", "4.0-11.0", ""],
            ]),
        ]);

    private const string GoodMapping =
        """
        {"tables":[{"tableIndex":0,"nameColumn":0,"valueColumn":1,"unitColumn":null,"referenceColumn":2,"flagColumn":3,
        "analytes":[{"rowIndex":1,"canonicalName":"Hemoglobin"},{"rowIndex":2,"canonicalName":"Leukocytes"}]}]}
        """;

    [Fact]
    public void Values_are_copied_verbatim_from_the_cells_the_agent_pointed_at()
    {
        var analytes = LabAnalyteMapping.TryVerify(Cbc, GoodMapping);

        Assert.NotNull(analytes);
        Assert.Equal(2, analytes.Count);

        var hemoglobin = analytes[0];
        Assert.Equal("Hemoglobin", hemoglobin.CanonicalName);
        Assert.Equal("Hemoglobin", hemoglobin.VerbatimName);
        Assert.Equal("13.2 g/dL", hemoglobin.Value);          // verbatim from the cell
        Assert.Equal("13.5-17.5", hemoglobin.ReferenceRange);
        Assert.Equal("LOW", hemoglobin.Flag);

        var leukocytes = analytes[1];
        Assert.Equal("Leukocytes", leukocytes.CanonicalName); // agent-assigned canonical name
        Assert.Equal("WBC", leukocytes.VerbatimName);         // name as printed
        Assert.Equal("6.1 10^9/L", leukocytes.Value);
        Assert.Null(leukocytes.Flag);                         // that flag cell was empty
    }

    [Fact]
    public void An_out_of_range_value_column_rejects_the_whole_set()
    {
        var mapping =
            """{"tables":[{"tableIndex":0,"nameColumn":0,"valueColumn":9,"analytes":[{"rowIndex":1,"canonicalName":"Hemoglobin"}]}]}""";
        Assert.Null(LabAnalyteMapping.TryVerify(Cbc, mapping));
    }

    [Fact]
    public void A_row_index_past_the_table_rejects_the_whole_set()
    {
        var mapping =
            """{"tables":[{"tableIndex":0,"nameColumn":0,"valueColumn":1,"analytes":[{"rowIndex":99,"canonicalName":"X"}]}]}""";
        Assert.Null(LabAnalyteMapping.TryVerify(Cbc, mapping));
    }

    [Fact]
    public void An_unreadable_or_empty_mapping_yields_no_rows()
    {
        Assert.Null(LabAnalyteMapping.TryVerify(Cbc, "not json at all"));
        Assert.Null(LabAnalyteMapping.TryVerify(Cbc, """{"tables":[]}"""));
    }
}
