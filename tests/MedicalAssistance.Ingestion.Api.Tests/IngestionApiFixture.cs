using MedicalAssistance.Ingestion.Api.Ingestions;
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
    /// <summary>
    /// The real schema dimension, not a smaller test-only one: the
    /// <c>vector(n)</c> width is fixed by the migration, so a fake generator
    /// producing anything else would be rejected by the column. Tests therefore
    /// exercise the same schema that ships.
    /// </summary>
    public const int EmbeddingDimensions = IngestionDbContext.EmbeddingDimensions;

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

    /// <summary>
    /// The admin secret GDPR Erasure requires (ADR-0007). A different key from
    /// the everyday ones, so a test proving a leaked everyday key cannot erase
    /// has a genuinely separate credential to hold up.
    /// </summary>
    public const string AdminApiKey = "test-admin-key-erasure";

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
    /// <param name="sweepInterval">
    /// How often this instance re-scans for orphaned work. Left unset, the
    /// production default applies — far longer than any test runs, so an
    /// instance a test is using for something else never sweeps up work another
    /// test parked. A test about recovery sets its own.
    /// </param>
    public WebApplicationFactory<Program> CreateFactory(
        ScriptedChatClient chatClient, int workerCount = 4, TimeSpan? sweepInterval = null) =>
        new AuthenticatedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
            builder.UseSetting("Ingestion:WorkerCount", workerCount.ToString());
            if (sweepInterval is { } interval)
                builder.UseSetting("Ingestion:RecoverySweepInterval", interval.ToString());
            builder.UseSetting("Chunking:MinTokens", MinChunkTokens.ToString());
            builder.UseSetting("Chunking:MaxTokens", MaxChunkTokens.ToString());
            builder.UseSetting("Authentication:ApiKeys:0", ApiKey);
            builder.UseSetting("Authentication:ApiKeys:1", RotationApiKey);
            builder.UseSetting("Authentication:AdminApiKeys:0", AdminApiKey);
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
