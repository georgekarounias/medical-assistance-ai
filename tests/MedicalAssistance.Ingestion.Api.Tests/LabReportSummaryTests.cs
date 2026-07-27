using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// A LabReport is rendered by code, not chunked by an agent, so it has no chunking
/// step that produces a summary the way the prose types do. One is written from the
/// rendered panels instead — stored on the ingestion (and so folded into the patient
/// overview), but never as a chunk: the vector store stays verbatim (ADR-0006). It is
/// best-effort, so a summariser failure leaves the field null without failing a report
/// whose panels already succeeded.
/// </summary>
public class LabReportSummaryTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
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

    private static readonly string SomePdf = Convert.ToBase64String([0x25, 0x50, 0x44, 0x46]);

    [Fact]
    public async Task A_lab_report_is_given_a_summary_written_from_its_panels()
    {
        var client = WithExtractor(new FakeDocumentExtractor(Cbc)).CreateClient();
        fixture.ChatClient.EnqueueResponse(
            """{"tables":[{"tableIndex":0,"nameColumn":0,"valueColumn":1,"referenceColumn":2,"flagColumn":3,"analytes":[]}]}""");
        fixture.ChatClient.EnqueueSummaryResponse("Complete blood count with a low haemoglobin of 13.2 g/dL.");

        var ingestionId = await IngestAsync(client, patientId: "lab-sum-1", reportId: "cbc-sum-1", SomePdf);

        // The summary is exposed on the ingestion, exactly as the prose types' is.
        var status = await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");
        Assert.Equal("Complete blood count with a low haemoglobin of 13.2 g/dL.", status.GetProperty("summary").GetString());

        // It was written from the rendered panel — the verbatim values were the input.
        Assert.Contains(
            fixture.ChatClient.ReceivedSummaryPrompts,
            prompt => prompt.Contains("Hemoglobin: 13.2 g/dL 13.5-17.5 LOW"));
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
}
