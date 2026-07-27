using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// When a local archive root is configured, every submitted document is saved to a
/// filesystem folder structure — {root}/{doctorId}/{patientId}/{documentType}/
/// {documentId}/{ingestionId}.{ext} plus a metadata.json manifest — before a worker
/// touches it. A landing zone for local inspection, independent of the durable
/// database payload; when no root is configured it is off entirely (every other
/// test runs that way).
/// </summary>
public class DocumentArchiveTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    [Fact]
    public async Task A_submitted_document_is_saved_to_the_configured_folder_structure_before_ingestion()
    {
        var root = Path.Combine(Path.GetTempPath(), "medassist-archive", Guid.NewGuid().ToString("N"));
        try
        {
            // Park the worker: archiving happens at submit, so the file is on disk
            // by the time the 202 returns — no need to let the ingestion complete.
            await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("DocumentArchive:LocalRootPath", root);
                builder.UseSetting("Ingestion:WorkerCount", "0");
            });
            var client = factory.CreateClient();

            const string transcript = "Doctor: Good morning.\nPatient: Hello.";
            var response = await client.PostAsJsonAsync("/ingestions", new
            {
                documentType = "SessionTranscript",
                doctorId = "doc-1",
                patientId = "pat-archive",
                sessionId = "sess-archive",
                sequenceNumber = 1,
                language = "en",
                transcript,
            });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("ingestionId").GetGuid();

            var folder = Path.Combine(
                root, "doc-1", "pat-archive", "SessionTranscript", "doc-1#pat-archive#sess-archive#1");

            var bodyFile = Path.Combine(folder, $"{ingestionId}.txt");
            Assert.True(File.Exists(bodyFile), $"Expected the archived body at {bodyFile}");
            Assert.Equal(transcript, await File.ReadAllTextAsync(bodyFile));

            var metadataFile = Path.Combine(folder, "metadata.json");
            Assert.True(File.Exists(metadataFile), $"Expected the manifest at {metadataFile}");
            var metadata = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(metadataFile));
            Assert.Equal(ingestionId, metadata.GetProperty("ingestionId").GetGuid());
            Assert.Equal("doc-1#pat-archive#sess-archive#1", metadata.GetProperty("documentId").GetString());
            Assert.Equal("SessionTranscript", metadata.GetProperty("documentType").GetString());
            Assert.Equal("sess-archive", metadata.GetProperty("sessionId").GetString());
            Assert.Equal($"{ingestionId}.txt", metadata.GetProperty("bodyFile").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
