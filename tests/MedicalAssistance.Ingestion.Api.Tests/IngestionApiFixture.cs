using MedicalAssistance.Ingestion.Api.Security;
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

    /// <summary>
    /// Chunk-size guardrail thresholds for tests — deliberately small so a
    /// handful of transcript lines can exercise merging and splitting, while
    /// the transcripts used by the other tests stay inside the band and keep
    /// asserting what they were written to assert.
    /// </summary>
    public const int MinChunkTokens = 12;

    /// <inheritdoc cref="MinChunkTokens" />
    public const int MaxChunkTokens = 40;

    /// <summary>The secret every client created here sends by default.</summary>
    public const string ApiKey = "test-api-key-primary";

    /// <summary>A second valid secret — the state the service is in mid-rotation.</summary>
    public const string RotationApiKey = "test-api-key-secondary";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public ScriptedChatClient ChatClient { get; } = new();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = CreateFactory(ChatClient);
        _ = Factory.Server; // WebApplicationFactory is lazy — force startup (schema + seeding) now.
    }

    /// <summary>
    /// Boots an additional application instance against the same database —
    /// used to observe startup-time behavior (seeding, singleton loading), and
    /// with <paramref name="workerCount"/> set to zero, to park submissions in
    /// Queued for as long as a test needs them there.
    /// </summary>
    public WebApplicationFactory<Program> CreateFactory(ScriptedChatClient chatClient, int workerCount = 4) =>
        new AuthenticatedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
            builder.UseSetting("Ingestion:WorkerCount", workerCount.ToString());
            builder.UseSetting("Embeddings:Dimensions", EmbeddingDimensions.ToString());
            builder.UseSetting("Chunking:MinTokens", MinChunkTokens.ToString());
            builder.UseSetting("Chunking:MaxTokens", MaxChunkTokens.ToString());
            builder.UseSetting("Authentication:ApiKeys:0", ApiKey);
            builder.UseSetting("Authentication:ApiKeys:1", RotationApiKey);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IChatClient>(chatClient);
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                    new DeterministicEmbeddingGenerator(EmbeddingDimensions));
            });
        });

    /// <summary>
    /// Sends the API secret on every client this fixture hands out, so tests
    /// exercise the authenticated path the backend really uses. A test that
    /// wants an unauthenticated caller removes the header from its own client.
    /// </summary>
    private sealed class AuthenticatedFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureClient(HttpClient client)
        {
            client.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, ApiKey);
            base.ConfigureClient(client);
        }
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
