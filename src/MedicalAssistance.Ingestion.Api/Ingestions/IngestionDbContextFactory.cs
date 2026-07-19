using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Builds a context for the <c>dotnet ef</c> tooling only — scaffolding a
/// migration reads the model, never the database.
///
/// It exists so the tooling does not have to start the application to find a
/// context: <c>Program.cs</c> refuses to run without a connection string and an
/// API secret, and then migrates and seeds on the way up. Scaffolding a
/// migration should not need any of that, and must never touch a real database
/// as a side effect of being asked what the model looks like.
/// </summary>
public sealed class IngestionDbContextFactory : IDesignTimeDbContextFactory<IngestionDbContext>
{
    /// <inheritdoc />
    public IngestionDbContext CreateDbContext(string[] args)
    {
        // Never connected to. Npgsql only has to parse it for the provider to
        // build the relational model that the migration is generated from.
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql("Host=localhost;Database=ingestion_design_time", npgsql => npgsql.UseVector())
            .Options;

        return new IngestionDbContext(options);
    }
}
