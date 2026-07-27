using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// Provider wiring (T33): every chunk records the embedding model that produced its
/// vector — the write/read contract that makes an embedding-model change a managed
/// re-embedding rather than silent search corruption — and the service refuses to
/// start if a configured embedding dimension disagrees with the vector column,
/// which is fixed by migration.
/// </summary>
public class ProviderWiringTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    [Fact]
    public async Task Every_chunk_is_stamped_with_the_embedding_model_that_produced_it()
    {
        var client = fixture.Factory.CreateClient();
        fixture.ChatClient.EnqueueResponse(
            """{ "chunks": [ { "startLine": 0, "endLine": 1, "contextBlurb": "x" } ], "summary": "y" }""");

        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "doc-1",
            patientId = "pat-embed-stamp",
            sessionId = "sess-embed",
            sequenceNumber = 1,
            transcript = "Doctor: Good morning there.\nPatient: Hello doctor.",
        });
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("ingestionId").GetGuid();
        await WaitForCompletedAsync(client, ingestionId);

        var models = await ReadEmbeddingModelsAsync(ingestionId);
        Assert.NotEmpty(models);
        Assert.All(models, model => Assert.Equal(DeterministicEmbeddingGenerator.ModelId, model));
    }

    [Fact]
    public void A_configured_embedding_dimension_that_mismatches_the_column_refuses_to_start()
    {
        var startup = Assert.Throws<InvalidOperationException>(() =>
        {
            using var factory = fixture.Factory.WithWebHostBuilder(builder =>
                builder.UseSetting(AzureAi.EmbeddingDimensionsConfigurationKey, "8"));
            factory.CreateClient();
        });

        Assert.Contains("dimension", startup.Message, StringComparison.OrdinalIgnoreCase);
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
                Assert.Fail($"Ingestion {ingestionId} failed unexpectedly.");
            await Task.Delay(50);
        }
        throw new TimeoutException($"Ingestion {ingestionId} never completed.");
    }

    private async Task<List<string?>> ReadEmbeddingModelsAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT embedding_model FROM chunks WHERE ingestion_id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);

        var models = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            models.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return models;
    }
}
