using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// LabReport Tier 1: a base64 PDF is extracted through the seam (a fake here), then
/// code renders each table into a searchable Panel Rendition with no LLM — one
/// chunk per panel, values copied verbatim (ADR-0006). Intake is size-capped
/// (ADR-0005), and a report's identity is its reportId, so a re-POST is a
/// Correction.
/// </summary>
public class LabReportIngestionTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private static readonly ExtractedDocument CompleteBloodCount = new(
        "Complete Blood Count",
        [
            new ExtractedTable(
            [
                ["Analyte", "Value", "Reference", "Flag"],
                ["Hemoglobin", "13.2 g/dL", "13.5-17.5", "LOW"],
                ["WBC", "6.1 10^9/L", "4.0-11.0", ""],
            ]),
        ]);

    // Any valid base64; the fake extractor ignores the bytes and returns its script.
    private static readonly string SomePdf = Convert.ToBase64String([0x25, 0x50, 0x44, 0x46]);

    [Fact]
    public async Task A_lab_report_is_rendered_into_a_searchable_panel_with_values_copied_verbatim()
    {
        var extractor = new FakeDocumentExtractor(CompleteBloodCount);
        await using var factory = WithExtractor(extractor);
        var client = factory.CreateClient();

        var ingestionId = await IngestAsync(client, patientId: "pat-lab-1", reportId: "cbc-1", SomePdf);

        var chunks = await ReadChunksAsync(ingestionId);
        var panel = Assert.Single(chunks);
        Assert.Equal("labPanel", panel.Kind);
        Assert.Contains("Hemoglobin: 13.2 g/dL 13.5-17.5 LOW", panel.VerbatimText);
        Assert.Contains("WBC: 6.1 10^9/L 4.0-11.0", panel.VerbatimText);
    }

    [Fact]
    public async Task A_pdf_over_the_configured_limit_is_rejected_at_the_door()
    {
        // Closes T29's end-to-end size cap: LabReport is the first PDF-backed type,
        // so the intake rule is now reachable over HTTP.
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting(PdfIntake.MaxBytesConfigurationKey, "5"));
        var client = factory.CreateClient();

        var overLimit = Convert.ToBase64String(new byte[10]);
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "LabReport",
            doctorId = "doc-1",
            patientId = "pat-lab-big",
            reportId = "big-1",
            pdfContent = overLimit,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("pdfContent", out _), $"Expected a pdfContent error in: {errors}");
    }

    [Fact]
    public async Task Re_posting_a_report_id_with_a_different_pdf_supersedes_the_previous_version()
    {
        var corrected = new ExtractedDocument(
            "Complete Blood Count (corrected)",
            [new ExtractedTable([["Analyte", "Value"], ["Hemoglobin", "14.0 g/dL"]])]);
        var extractor = new FakeDocumentExtractor(CompleteBloodCount).Enqueue(CompleteBloodCount).Enqueue(corrected);

        await using var factory = WithExtractor(extractor);
        var client = factory.CreateClient();
        const string patientId = "pat-lab-corrected";
        const string reportId = "cbc-correctable";

        var originalId = await IngestAsync(client, patientId, reportId, SomePdf);
        // A different PDF (different bytes → different content hash) is a Correction.
        var correctedId = await IngestAsync(client, patientId, reportId, Convert.ToBase64String([1, 2, 3, 4, 5]));

        Assert.NotEqual(originalId, correctedId);
        Assert.Equal("Superseded", await ReadStatusAsync(client, originalId));
        Assert.Equal(0, await CountChunksOfAsync(originalId));

        var live = await ReadDocumentTextsAsync(patientId, reportId);
        Assert.Contains(live, text => text.Contains("Hemoglobin: 14.0 g/dL"));
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
        await WaitForStatusAsync(client, ingestionId, "Completed");
        return ingestionId;
    }

    private static async Task<string> ReadStatusAsync(HttpClient client, Guid ingestionId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}")).GetProperty("status").GetString()!;

    private static async Task WaitForStatusAsync(HttpClient client, Guid ingestionId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var lastSeen = "<never fetched>";
        while (DateTime.UtcNow < deadline)
        {
            lastSeen = await ReadStatusAsync(client, ingestionId);
            if (lastSeen == expected)
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Ingestion {ingestionId} never reached {expected}. Last: {lastSeen}");
    }

    private async Task<List<(string Kind, string VerbatimText)>> ReadChunksAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT chunk_kind, verbatim_text FROM chunks WHERE ingestion_id = $1 ORDER BY chunk_index", connection);
        command.Parameters.AddWithValue(ingestionId);

        var chunks = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            chunks.Add((reader.GetString(0), reader.GetString(1)));
        return chunks;
    }

    private async Task<List<string>> ReadDocumentTextsAsync(string patientId, string reportId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT verbatim_text FROM chunks WHERE document_id = $1 ORDER BY chunk_index", connection);
        command.Parameters.AddWithValue($"doc-1#{patientId}#{reportId}");

        var texts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            texts.Add(reader.GetString(0));
        return texts;
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
