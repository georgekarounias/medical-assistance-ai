using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// ImagingReport: a radiologist's findings are extracted from the PDF and run
/// through the shared prose pipeline (chunks + blurbs + summary), and every chunk
/// carries the imageLink in its sourceRef so a finding is one tap from the image.
/// Pixels are never ingested (ADR-0005).
/// </summary>
public class ImagingReportIngestionTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string Findings =
        """
        Findings: The lungs are clear.
        No pleural effusion is seen.
        The heart size is normal.
        Impression: No acute process.
        """;

    private const string TwoChunkPlan =
        """
        {
          "chunks": [
            { "startLine": 0, "endLine": 1, "contextBlurb": "Lungs and pleura." },
            { "startLine": 2, "endLine": 3, "contextBlurb": "Heart and impression." }
          ],
          "summary": "Normal chest imaging; no acute process."
        }
        """;

    private const string ImageLink = "https://viewer.example/studies/xr-1";
    private static readonly string SomePdf = Convert.ToBase64String([0x25, 0x50, 0x44, 0x46]);

    [Fact]
    public async Task Findings_are_chunked_and_every_chunk_carries_the_image_link()
    {
        var extractor = new FakeDocumentExtractor(new ExtractedDocument(Findings, []));
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IDocumentExtractor>(extractor)));
        var client = factory.CreateClient();

        fixture.ChatClient.EnqueueResponse(TwoChunkPlan);
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "ImagingReport",
            doctorId = "doc-1",
            patientId = "pat-img-1",
            reportId = "xr-1",
            pdfContent = SomePdf,
            imageLink = ImageLink,
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("ingestionId").GetGuid();
        await WaitForCompletedAsync(client, ingestionId);

        var chunks = await ReadChunksAsync(ingestionId);
        // Two findings chunks plus a summary.
        Assert.Equal(2, chunks.Count(c => c.Kind == "imagingReport"));
        Assert.Single(chunks, c => c.Kind == "summary");
        Assert.Contains(
            "Findings: The lungs are clear.\nNo pleural effusion is seen.",
            chunks.Select(c => c.VerbatimText));

        // Every chunk — findings and summary alike — carries the image link.
        Assert.All(chunks, chunk => Assert.Contains(ImageLink, chunk.SourceRef ?? ""));
    }

    [Fact]
    public async Task An_imaging_report_without_an_image_link_is_rejected()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "ImagingReport",
            doctorId = "doc-1",
            patientId = "pat-img-noimg",
            reportId = "xr-noimg",
            pdfContent = SomePdf,
            // no imageLink
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("imageLink", out _), $"Expected an imageLink error in: {errors}");
    }

    private static async Task WaitForCompletedAsync(HttpClient client, Guid ingestionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = (await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}"))
                .GetProperty("status").GetString();
            if (status == "Completed")
                return;
            if (status == "Failed")
                Assert.Fail($"Imaging ingestion {ingestionId} failed unexpectedly.");
            await Task.Delay(50);
        }
        throw new TimeoutException($"Imaging ingestion {ingestionId} never completed.");
    }

    private async Task<List<(string Kind, string VerbatimText, string? SourceRef)>> ReadChunksAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT chunk_kind, verbatim_text, source_ref::text FROM chunks WHERE ingestion_id = $1 ORDER BY chunk_index",
            connection);
        command.Parameters.AddWithValue(ingestionId);

        var chunks = new List<(string, string, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            chunks.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return chunks;
    }
}
