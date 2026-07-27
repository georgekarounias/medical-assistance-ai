using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Security;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// LabReport Tier 2 end-to-end: verified analyte rows are stored beside the panels,
/// all-or-nothing (a bad mapping stores zero rows and flags the report), and a
/// document's analyte rows are removed with its chunks on Correction, un-ingest and
/// erasure — never left behind (T31).
/// </summary>
public class LabReportTier2Tests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
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
        {"tables":[{"tableIndex":0,"nameColumn":0,"valueColumn":1,"referenceColumn":2,"flagColumn":3,
        "analytes":[{"rowIndex":1,"canonicalName":"Hemoglobin"},{"rowIndex":2,"canonicalName":"Leukocytes"}]}]}
        """;

    private static readonly string SomePdf = Convert.ToBase64String([0x25, 0x50, 0x44, 0x46]);

    [Fact]
    public async Task Verified_analytes_are_stored_with_values_copied_verbatim_and_the_report_is_flagged()
    {
        var client = WithExtractor(new FakeDocumentExtractor(Cbc)).CreateClient();
        fixture.ChatClient.EnqueueResponse(GoodMapping);

        var ingestionId = await IngestAsync(client, patientId: "pat-t2-1", reportId: "cbc-1", SomePdf);

        Assert.True(await ReadAnalytesExtractedAsync(ingestionId));
        var analytes = await ReadAnalytesAsync(ingestionId);
        Assert.Equal(2, analytes.Count);
        Assert.Equal(("Hemoglobin", "13.2 g/dL", "LOW"), (analytes[0].Canonical, analytes[0].Value, analytes[0].Flag));
        Assert.Equal("Leukocytes", analytes[1].Canonical);
        Assert.Equal("6.1 10^9/L", analytes[1].Value);
    }

    [Fact]
    public async Task An_unverifiable_mapping_stores_zero_rows_and_flags_the_report_without_failing_it()
    {
        var client = WithExtractor(new FakeDocumentExtractor(Cbc)).CreateClient();
        // Points the value column past the grid: not one row can be verified.
        fixture.ChatClient.EnqueueResponse(
            """{"tables":[{"tableIndex":0,"nameColumn":0,"valueColumn":9,"analytes":[{"rowIndex":1,"canonicalName":"Hemoglobin"}]}]}""");

        var ingestionId = await IngestAsync(client, patientId: "pat-t2-bad", reportId: "cbc-bad", SomePdf);

        // Tier 1 succeeded, so the ingestion Completed and the panel is searchable...
        Assert.Equal(1, await CountChunksOfAsync(ingestionId));
        // ...but Tier 2 stored nothing, all-or-nothing, and said so.
        Assert.False(await ReadAnalytesExtractedAsync(ingestionId));
        Assert.Empty(await ReadAnalytesAsync(ingestionId));
    }

    [Fact]
    public async Task A_correction_replaces_the_previous_versions_analyte_rows()
    {
        var corrected = new ExtractedDocument(
            "CBC (corrected)",
            [new ExtractedTable([["Analyte", "Value"], ["Hemoglobin", "14.0 g/dL"]])]);
        var extractor = new FakeDocumentExtractor(Cbc).Enqueue(Cbc).Enqueue(corrected);
        var client = WithExtractor(extractor).CreateClient();
        const string patientId = "pat-t2-corrected";
        const string reportId = "cbc-correctable";

        fixture.ChatClient.EnqueueResponse(GoodMapping);
        var originalId = await IngestAsync(client, patientId, reportId, SomePdf);

        fixture.ChatClient.EnqueueResponse(
            """{"tables":[{"tableIndex":0,"nameColumn":0,"valueColumn":1,"analytes":[{"rowIndex":1,"canonicalName":"Hemoglobin"}]}]}""");
        var correctedId = await IngestAsync(client, patientId, reportId, Convert.ToBase64String([1, 2, 3, 4, 5]));

        // The old version's analyte rows are gone; the document holds only the new one.
        Assert.NotEqual(originalId, correctedId);
        Assert.Empty(await ReadAnalytesAsync(originalId));
        var live = await ReadDocumentAnalytesAsync(patientId, reportId);
        var only = Assert.Single(live);
        Assert.Equal("14.0 g/dL", only.Value);
    }

    [Fact]
    public async Task Un_ingesting_a_lab_report_removes_its_analyte_rows()
    {
        var client = WithExtractor(new FakeDocumentExtractor(Cbc)).CreateClient();
        fixture.ChatClient.EnqueueResponse(GoodMapping);
        const string patientId = "pat-t2-uningest";
        const string reportId = "cbc-uningest";

        await IngestAsync(client, patientId, reportId, SomePdf);
        Assert.NotEmpty(await ReadDocumentAnalytesAsync(patientId, reportId));

        var documentId = $"doc-1#{patientId}#{reportId}";
        var response = await client.DeleteAsync($"/documents/{Uri.EscapeDataString(documentId)}?removedBy=doc-remover");
        response.EnsureSuccessStatusCode();

        Assert.Empty(await ReadDocumentAnalytesAsync(patientId, reportId));
    }

    [Fact]
    public async Task Erasing_a_patient_removes_their_analyte_rows()
    {
        var client = WithExtractor(new FakeDocumentExtractor(Cbc)).CreateClient();
        fixture.ChatClient.EnqueueResponse(GoodMapping);
        const string patientId = "pat-t2-erased";

        await IngestAsync(client, patientId, reportId: "cbc-erased", SomePdf);
        Assert.NotEmpty(await ReadDocumentAnalytesAsync(patientId, "cbc-erased"));

        var admin = fixture.Factory.CreateClient();
        admin.DefaultRequestHeaders.Remove(ApiKeyAuthentication.HeaderName);
        admin.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, IngestionApiFixture.AdminApiKey);
        var response = await admin.DeleteAsync($"/patients/{patientId}/data?erasedBy=admin");
        response.EnsureSuccessStatusCode();

        Assert.Empty(await ReadDocumentAnalytesAsync(patientId, "cbc-erased"));
    }

    private WebApplicationFactory<Program> WithExtractor(FakeDocumentExtractor extractor) =>
        fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IDocumentExtractor>(extractor)));

    private async Task<Guid> IngestAsync(HttpClient client, string patientId, string reportId, string pdfContent)
    {
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "LabReport",
            doctorId = "doc-1",
            patientId,
            reportId,
            pdfContent,
        });
        response.EnsureSuccessStatusCode();
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("ingestionId").GetGuid();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = (await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}"))
                .GetProperty("status").GetString();
            if (status == "Completed")
                return ingestionId;
            if (status == "Failed")
                Assert.Fail($"Lab ingestion {ingestionId} failed unexpectedly.");
            await Task.Delay(50);
        }
        throw new TimeoutException($"Lab ingestion {ingestionId} never completed.");
    }

    private async Task<bool?> ReadAnalytesExtractedAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT analytes_extracted FROM ingestions WHERE id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (bool)value;
    }

    private Task<List<(string Canonical, string Value, string? Flag)>> ReadAnalytesAsync(Guid ingestionId) =>
        QueryAnalytesAsync("WHERE ingestion_id = $1", ingestionId);

    private Task<List<(string Canonical, string Value, string? Flag)>> ReadDocumentAnalytesAsync(
        string patientId, string reportId) =>
        QueryAnalytesAsync("WHERE document_id = $1", $"doc-1#{patientId}#{reportId}");

    private async Task<List<(string Canonical, string Value, string? Flag)>> QueryAnalytesAsync(
        string where, object key)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT canonical_name, value, flag FROM analyte_results {where} ORDER BY row_index", connection);
        command.Parameters.AddWithValue(key);

        var rows = new List<(string, string, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return rows;
    }

    private async Task<long> CountChunksOfAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM chunks WHERE ingestion_id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
