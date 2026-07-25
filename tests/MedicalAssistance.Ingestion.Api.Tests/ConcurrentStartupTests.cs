using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// On the way up the database is migrated — which now also seeds the agent
/// instructions (the SeedAgentInstructions migration), so there is one write path,
/// not two. A rolling deploy runs several instances against the same database at
/// once, so it has to be safe to run concurrently: migration is serialized by an
/// advisory lock and recorded in the migration history, so every migration —
/// schema and seed alike — runs exactly once and no instance dies on a duplicate
/// key (the race that was bug B17, back when seeding was a separate startup loop).
///
/// This gets its own fresh database rather than the shared fixture's, because the
/// race is only reachable before anything has been migrated.
/// </summary>
public sealed class ConcurrentStartupTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Several_instances_starting_at_once_against_a_fresh_database_all_come_up()
    {
        // Enough racers that if seeding were unserialized, at least one pair would
        // read the empty table before either committed and collide on the key.
        var factories = Enumerable.Range(0, 5).Select(_ => CreateFactory()).ToList();
        try
        {
            // Force each host to start — which runs the migration and the seed —
            // all at the same time. A duplicate-key failure in seeding surfaces
            // here as the faulting Server access throwing.
            await Task.WhenAll(factories.Select(factory => Task.Run(() => _ = factory.Server)));
        }
        finally
        {
            foreach (var factory in factories)
                await factory.DisposeAsync();
        }
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("Ingestion:WorkerCount", "0");
            builder.UseSetting("Authentication:ApiKeys:0", IngestionApiFixture.ApiKey);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IChatClient>(new ScriptedChatClient());
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                    new DeterministicEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions));
            });
        });
}
