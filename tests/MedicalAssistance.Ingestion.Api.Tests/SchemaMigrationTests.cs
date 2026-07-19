using MedicalAssistance.Ingestion.Api.Ingestions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The schema is owned by migrations, not by EnsureCreated.
///
/// EnsureCreated builds the schema only when the database is absent and no-ops
/// otherwise, so a column or index added to the model after a database was first
/// created never reached it — while the suite, which gets a fresh container every
/// run, stayed green and reported nothing. These tests fail on the two ways that
/// can come back: the schema not being migration-managed, and the model drifting
/// ahead of the migrations that are supposed to describe it.
/// </summary>
public class SchemaMigrationTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    [Fact]
    public async Task The_schema_is_applied_by_migrations_and_none_are_left_pending()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.NotEmpty(applied);
        Assert.Contains(applied, migration => migration.EndsWith("InitialSchema", StringComparison.Ordinal));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public void The_model_has_no_changes_that_no_migration_describes()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();

        // MigrateAsync already refuses to run against a drifted model, so an
        // unscaffolded change takes the service down at startup rather than
        // reaching a database. This asserts the same thing directly, because the
        // exception it throws names the setting that switches it off
        // (PendingModelChangesWarning) — and someone taking that advice would
        // restore the original silent-drift behaviour. This test would not go
        // quiet with it.
        Assert.False(
            db.Database.HasPendingModelChanges(),
            "The model has changed since the last migration. Run: dotnet ef migrations add <Name> " +
            "--project src/MedicalAssistance.Ingestion.Api --output-dir Ingestions/Migrations");
    }

    [Fact]
    public async Task Every_column_the_model_declares_exists_in_the_database()
    {
        // Reads the live schema rather than the model, so it can see the exact
        // drift EnsureCreated used to hide: attempts and document_date were added
        // to the model long after the first databases were built.
        var columns = await ReadColumnsAsync("ingestions");

        Assert.Contains("attempts", columns);
        Assert.Contains("document_date", columns);
        Assert.Contains("content_hash", columns);

        var chunkColumns = await ReadColumnsAsync("chunks");
        Assert.Contains("embedding", chunkColumns);
        Assert.Contains("document_id", chunkColumns);
    }

    [Fact]
    public async Task The_embedding_column_is_the_width_the_model_declares()
    {
        // The dimension is baked into the migration's DDL, so a model constant
        // that drifts from it would only surface as a failed insert at runtime.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a " +
            "WHERE a.attrelid = 'chunks'::regclass AND a.attname = 'embedding'",
            connection);

        Assert.Equal($"vector({IngestionDbContext.EmbeddingDimensions})", (string)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public void A_worker_count_that_could_starve_the_connection_pool_refuses_to_start()
    {
        // Each worker can hold two connections at once — the one carrying its
        // advisory lock for the whole run, and one for its database work — so a
        // high worker count against a small pool deadlocks: workers wait for
        // connections only they can release. The service says so on the way up
        // rather than hanging later with nothing in the logs to explain it.
        var startup = Assert.Throws<InvalidOperationException>(() =>
        {
            using var factory = fixture.CreateFactory(new Fakes.ScriptedChatClient(), workerCount: 40);
            factory.WithWebHostBuilder(builder =>
                    builder.UseSetting("ConnectionStrings:Postgres", $"{fixture.ConnectionString};Maximum Pool Size=20"))
                .CreateClient();
        });

        Assert.Contains("WorkerCount", startup.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MaxPoolSize", startup.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<string>> ReadColumnsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = $1", connection);
        command.Parameters.AddWithValue(table);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        return columns;
    }
}
