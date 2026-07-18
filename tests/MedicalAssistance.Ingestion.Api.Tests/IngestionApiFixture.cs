using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// Hosts the whole service in-process against a real pgvector Postgres container.
/// Tests cross only the public HTTP interface; the AI seams carry fakes.
/// </summary>
public sealed class IngestionApiFixture : IAsyncLifetime
{
    public const int EmbeddingDimensions = 8;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public ScriptedChatClient ChatClient { get; } = new();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
            builder.UseSetting("Embeddings:Dimensions", EmbeddingDimensions.ToString());
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IChatClient>(ChatClient);
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                    new DeterministicEmbeddingGenerator(EmbeddingDimensions));
            });
        });
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
