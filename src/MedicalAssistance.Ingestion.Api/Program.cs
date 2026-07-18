using System.Threading.Channels;
using MedicalAssistance.Ingestion.Api.Ingestions;
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
});

builder.Services.AddDbContext<IngestionDbContext>(options =>
    options.UseNpgsql(dataSource, npgsql => npgsql.UseVector()));

builder.Services.TryAddSingleton<IChatClient>(new UnconfiguredChatClient());
builder.Services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new UnconfiguredEmbeddingGenerator());

builder.Services.AddSingleton<AgentInstructionProvider>();
builder.Services.AddScoped<IngestionStore>();
builder.Services.AddScoped<TranscriptIngestionStrategy>();
builder.Services.AddSingleton(Channel.CreateUnbounded<Guid>());
builder.Services.AddHostedService<IngestionWorker>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
    await db.Database.EnsureCreatedAsync();

    // The vector extension may have been created just now, after this pool's
    // type catalog was loaded — reload so the 'vector' type is usable.
    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
    await connection.OpenAsync();
    await connection.ReloadTypesAsync();
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

    // Whatever the last process was working on when it stopped is queued again.
    // A crash or a deploy must not turn an accepted upload into a progress bar
    // that never moves; the attempt cap is what keeps this from looping.
    var queue = app.Services.GetRequiredService<Channel<Guid>>();
    var unfinished = await scope.ServiceProvider.GetRequiredService<IngestionStore>().FindUnfinishedAsync();
    foreach (var ingestionId in unfinished)
        await queue.Writer.WriteAsync(ingestionId);

    if (unfinished.Count > 0)
        app.Logger.LogInformation("Requeued {Count} unfinished ingestions after startup", unfinished.Count);
}

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Clinical Document Ingestion API v1");
});

app.MapControllers();

app.Run();

public partial class Program;
