using System.Threading.Channels;
using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Realtime;
using MedicalAssistance.Ingestion.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Pgvector.Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

// Refuse to start without a secret rather than starting wide open: this service
// holds patient data and has no other gate in front of it (ADR-0007).
if (builder.Configuration.GetSection(ApiKeyAuthentication.KeysConfigurationPath).Get<string[]>()
    is not { Length: > 0 } configuredKeys || configuredKeys.All(string.IsNullOrWhiteSpace))
{
    throw new InvalidOperationException(
        $"{ApiKeyAuthentication.KeysConfigurationPath} must contain at least one API secret. " +
        "Configure two while rotating keys.");
}
// Each worker can hold two connections at once: the one carrying its advisory
// lock for the whole run, and one for the database work inside it. Workers are
// capped at half the pool so submissions, status polls and the recovery sweep —
// which need connections of their own — can never be starved by ingestion.
//
// Checked here rather than discovered under load: raising WorkerCount is the
// obvious thing to try when ingestion looks slow, and the failure it causes is
// the service hanging with nothing in the logs naming the cause.
const int connectionsPerWorker = 2;
var workerCount = builder.Configuration.GetValue("Ingestion:WorkerCount", 4);
var maxPoolSize = new NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize;
if (workerCount * connectionsPerWorker > maxPoolSize / 2)
{
    throw new InvalidOperationException(
        $"Ingestion:WorkerCount is {workerCount}, which can hold up to " +
        $"{workerCount * connectionsPerWorker} of the connection pool's {maxPoolSize} connections and " +
        "leaves too little for serving requests. Lower the worker count, or raise MaxPoolSize in " +
        "ConnectionStrings:Postgres.");
}

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
var dataSource = dataSourceBuilder.Build();

builder.Services.AddControllers(options =>
{
    // MVC would otherwise infer [Required] from non-nullable properties and
    // reject the payload itself, with CLR-cased keys. Submission rules live in
    // IngestionRequestValidation instead, so callers get one error contract —
    // camelCase field names, every problem reported at once. Malformed JSON is
    // still rejected by the framework, as it should be.
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSignalR();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Clinical Document Ingestion API",
        Version = "v1",
        Description =
            "Ingests clinical documents (session transcripts today; doctor notes, lab and imaging reports planned) " +
            "into a patient-scoped vector store for RAG. Called exclusively by the existing backend. " +
            "Submission is asynchronous: POST returns 202 with an ingestion id; poll GET /ingestions/{id} for status.",
    });
    options.IncludeXmlComments(
        Path.Combine(AppContext.BaseDirectory, "MedicalAssistance.Ingestion.Api.xml"),
        includeControllerXmlComments: true);

    options.AddSecurityDefinition(ApiKeyAuthentication.SchemeName, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = ApiKeyAuthentication.HeaderName,
        Description =
            "Shared secret issued to the backend (ADR-0007). Two keys are accepted at once, " +
            "so keys can be rotated without downtime.",
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference(ApiKeyAuthentication.SchemeName, document)] = [],
    });
});

builder.Services
    .AddAuthentication(ApiKeyAuthentication.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthentication.SchemeName, null);

builder.Services.AddAuthorization(options =>
{
    // Applied to every endpoint that does not opt out, so a new controller is
    // protected by default instead of by remembering an attribute.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Erasure needs the separate admin secret on top of being authenticated: a
    // leaked everyday key gets past the fallback policy but not this one, so it
    // can read and un-ingest but never erase a patient (ADR-0007). With no admin
    // key configured, nothing carries the claim and every erasure is refused —
    // fail-closed, which is the right default for the most destructive operation.
    options.AddPolicy(ApiKeyAuthentication.ErasurePolicyName, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(ApiKeyAuthentication.AdminClaimType, ApiKeyAuthentication.AdminClaimValue));
});

builder.Services.AddSingleton(dataSource);
builder.Services.AddDbContext<IngestionDbContext>(options =>
    options.UseNpgsql(dataSource, npgsql => npgsql.UseVector()));

builder.Services.TryAddSingleton<IChatClient>(new UnconfiguredChatClient());
builder.Services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new UnconfiguredEmbeddingGenerator());

builder.Services.AddSingleton<AgentInstructionProvider>();
builder.Services.AddSingleton<IngestionStatusPublisher>();
builder.Services.AddScoped<IngestionStore>();
builder.Services.AddScoped<IngestionQueue>();
builder.Services.AddScoped<TranscriptIngestionStrategy>();
builder.Services.AddSingleton(Channel.CreateUnbounded<Guid>());
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<IngestionRecoverySweep>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();

    // Schema changes arrive as migrations, never as EnsureCreated. EnsureCreated
    // builds the schema only when the database is absent and silently no-ops
    // otherwise, so every column and index added after a database was first
    // created would be missing from it — while every test, running against a
    // fresh container, stayed green and showed nothing.
    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
    await connection.OpenAsync();
    await using (await PostgresAdvisoryLock.AcquireAsync(
        connection, PostgresAdvisoryLock.SchemaMigrationKey))
    {
        await db.Database.MigrateAsync();

        // The vector extension may have been created by that migration, after
        // this pool's type catalog was loaded — reload so 'vector' is usable.
        await connection.ReloadTypesAsync();
    }
    await connection.CloseAsync();

    // Seed missing agent instructions from code defaults, then load them all
    // into the singleton provider — read once, restart to apply (ADR-0008).
    var seededNames = await db.AgentInstructions.Select(a => a.Name).ToListAsync();
    foreach (var (name, instructions) in AgentInstructionDefaults.Defaults)
    {
        if (!seededNames.Contains(name))
        {
            db.AgentInstructions.Add(new AgentInstruction
            {
                Name = name,
                Instructions = instructions,
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
    }
    await db.SaveChangesAsync();
    app.Services.GetRequiredService<AgentInstructionProvider>()
        .Load(await db.AgentInstructions.AsNoTracking().ToListAsync());
}

// Whatever the last process abandoned is picked up by IngestionRecoverySweep,
// whose first pass runs as this host starts. Recovery is not a startup step:
// the instance that abandons work is not always the instance that has to notice.

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Clinical Document Ingestion API v1");
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// The hub carries no authorization metadata of its own, so the fallback policy
// applies: the handshake needs the same secret every other endpoint needs.
app.MapHub<IngestionStatusHub>("/hubs/ingestion-status");

app.Run();

public partial class Program;
