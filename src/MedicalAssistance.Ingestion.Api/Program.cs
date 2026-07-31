using System.Threading.Channels;
using MedicalAssistance.Ingestion.Api.Chat;
using MedicalAssistance.Ingestion.Api.Ingestions;
using MedicalAssistance.Ingestion.Api.Realtime;
using MedicalAssistance.Ingestion.Api.Retrieval;
using MedicalAssistance.Ingestion.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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

// Dimension guard (T33): the vector column's width is fixed by the migration that
// created it (IngestionDbContext.EmbeddingDimensions). If a real embedding provider
// is configured with a different dimension, the mismatch would otherwise surface
// only as failed inserts at runtime — so refuse to start. Changing the embedding
// dimension is a schema migration that re-embeds what is stored, not a config
// change, because existing vectors do not resize.
if (builder.Configuration.GetValue<int?>(AzureAi.EmbeddingDimensionsConfigurationKey) is { } configuredDimensions
    && configuredDimensions != IngestionDbContext.EmbeddingDimensions)
{
    throw new InvalidOperationException(
        $"{AzureAi.EmbeddingDimensionsConfigurationKey} is {configuredDimensions}, but the vector column is " +
        $"{IngestionDbContext.EmbeddingDimensions}-dimensional (fixed by migration). They must match — changing " +
        "the embedding dimension is a re-embedding migration, not a configuration change.");
}

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
var dataSource = dataSourceBuilder.Build();

// Observability (T35): traces, metrics and logs through OpenTelemetry. The
// service instruments itself and the frameworks it sits on; where the signals go
// is left to the OTLP exporter, added only when an endpoint is configured (the
// standard OTEL_EXPORTER_OTLP_ENDPOINT). That gating keeps local runs and the
// test host from opening exporter connections to a collector that is not there,
// while a deployment turns telemetry on with one environment variable.
//
// The spans and metrics this service emits carry ids and counts only — never a
// word of the patient's transcript or a rendered lab panel (ADR-0002/0006).
var otlpConfigured = !string.IsNullOrWhiteSpace(
    builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("MedicalAssistance.Ingestion.Api"))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(IngestionTelemetry.Name)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            // Database spans come from the Npgsql driver EF Core sits on, so one
            // instrumentation covers every query the store issues.
            .AddNpgsql();
        if (otlpConfigured)
            tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(IngestionTelemetry.Name)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        if (otlpConfigured)
            metrics.AddOtlpExporter();
    });

// Logs carry the ingestion-run scope (IncludeScopes) so the ingestion id the
// worker opens is attached to every line, exported alongside the traces.
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeScopes = true;
    if (otlpConfigured)
        logging.AddOtlpExporter();
});

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

// The ingestion-strategy registry (ADR-0004). Every strategy is registered as an
// IIngestionStrategy; the registry keys them by their declared Document Type and
// is the single authority both routing (the worker) and request validation
// consult. A new Document Type is one more AddScoped line here — nothing else.
// The prose strategies (transcript, note) are thin adapters over one shared
// pipeline; they differ only in text source, chunk kind, and agent instructions.
builder.Services.AddScoped<DocumentChunkCommitter>();
builder.Services.AddScoped<PatientSummaryService>();
builder.Services.AddScoped<ProseIngestionPipeline>();
builder.Services.AddScoped<IIngestionStrategy, TranscriptIngestionStrategy>();
builder.Services.AddScoped<IIngestionStrategy, DoctorNoteStrategy>();
builder.Services.AddScoped<IIngestionStrategy, LabReportStrategy>();
builder.Services.AddScoped<IIngestionStrategy, ImagingReportStrategy>();
builder.Services.AddScoped<IngestionStrategyRegistry>();

// The retrieval pipeline (ADR-0010/0011): an ordered-step registry mirroring the
// strategy registry above. Every stage is registered as an IRetrievalStep; the
// service sorts them by Order and runs them in sequence, so a new stage (the
// deferred hybrid-search or structured-analyte steps) is one more AddScoped line.
// Internal in v1 — the answer path calls SearchAsync directly, no HTTP surface yet.
// The Scope step (Order 10) sets the mandatory patient_id boundary first; embed and
// search steps join in T41.
builder.Services.AddScoped<IRetrievalStep, ScopeRetrievalStep>();
builder.Services.AddScoped<IRetrievalStep, RefineRetrievalStep>();
builder.Services.AddScoped<IRetrievalStep, EmbedRetrievalStep>();
builder.Services.AddScoped<IRetrievalStep, SearchRetrievalStep>();
builder.Services.AddScoped<IRetrievalService, RetrievalService>();

// The grounded-chat answer path (ADR-0010/0012): the stateless orchestration behind
// POST /patients/{id}/chat/answer — retrieve, generate over the evidence, package
// citations. Generation is a seam so the DB-seeded agent (T43) and the safety net
// (refusal T45, verification T46) can land without reshaping the endpoint.
builder.Services.AddScoped<IGroundedAnswerGenerator, GroundedAnswerGenerator>();
builder.Services.AddScoped<IGroundedAnswerService, GroundedAnswerService>();

// The extraction seam (ADR-0005): one provider-neutral interface for turning a
// PDF into text + table cell grids. Unconfigured by default so the app boots with
// no Azure account and fails loudly only if a PDF is actually processed; a real
// Azure Document Intelligence adapter replaces it by configuration, a fake by DI.
builder.Services.TryAddSingleton<IDocumentExtractor>(new UnconfiguredDocumentExtractor());

// Real providers replace the placeholders above when their configuration is present
// — provider choice is configuration, not architecture. Plain OpenAI (chat +
// embedding) is registered first, then the Azure providers (chat + embedding;
// ADR-0005 extraction). Order is deterministic: both beat the placeholder as later
// registrations, and Azure — added last — wins when both a plain-OpenAI and an Azure
// section are configured for the same seam.
builder.Services.AddOpenAiProviders(builder.Configuration);
builder.Services.AddAzureProviders(builder.Configuration);

// The document archive: a local landing zone that saves each submitted document
// to a filesystem folder structure before ingestion, active only when a root path
// is configured (for local testing). Off by default — the database payload is the
// system of record either way.
var documentArchiveRoot = builder.Configuration.GetValue<string>("DocumentArchive:LocalRootPath");
if (!string.IsNullOrWhiteSpace(documentArchiveRoot))
    builder.Services.AddSingleton<IIngestedDocumentArchive>(sp =>
        new LocalFileSystemDocumentArchive(
            documentArchiveRoot, sp.GetRequiredService<ILogger<LocalFileSystemDocumentArchive>>()));
else
    builder.Services.AddSingleton<IIngestedDocumentArchive, NullDocumentArchive>();

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
        // Migrations own the schema and the agent-instruction seed alike (ADR-0008):
        // the SeedAgentInstructions migration writes the starting prompts, so a
        // fresh database comes up with them and there is no application-side seeding
        // to keep concurrency-safe. The migration lock and the migration history
        // already make every migration run exactly once across a rolling deploy, so
        // the duplicate-key race the old startup seed loop had to guard against (B17)
        // cannot arise.
        await db.Database.MigrateAsync();

        // The vector extension may have been created by that migration, after
        // this pool's type catalog was loaded — reload so 'vector' is usable.
        await connection.ReloadTypesAsync();
    }
    await connection.CloseAsync();

    // Load the instructions into the singleton provider — read once, restart to
    // apply (ADR-0008). A read into this instance's own memory, so it needs no
    // lock and each instance does it independently.
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
