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

builder.Services.AddControllers();
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
}

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Clinical Document Ingestion API v1");
});

app.MapControllers();

app.Run();

public partial class Program;
