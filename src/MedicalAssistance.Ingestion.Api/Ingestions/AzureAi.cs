using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Wires the real Azure providers when their configuration is present, in place of
/// the <c>Unconfigured*</c> placeholders: Azure OpenAI for chat and embeddings, and
/// Azure Document Intelligence for PDF layout extraction (ADR-0005). All are
/// EU-region services under one compliance umbrella; secrets come from user-secrets
/// or the estate's secret store, never source — provider choice is configuration,
/// not architecture.
///
/// Registration only constructs clients; no network call happens until an ingestion
/// runs. When a section is absent the placeholder stays, so the app boots with no
/// Azure account and a test injects fakes over the top. When present, the real
/// client is added after the placeholder and wins as the last registration.
/// </summary>
public static class AzureAi
{
    /// <summary>Config key for the embedding dimension the startup guard checks against the vector column.</summary>
    public const string EmbeddingDimensionsConfigurationKey = "AzureOpenAI:Embedding:Dimensions";

    /// <summary>Registers whichever of the Azure chat, embedding and extraction providers are configured.</summary>
    public static void AddAzureProviders(this IServiceCollection services, IConfiguration configuration)
    {
        AddAzureOpenAI(services, configuration.GetSection("AzureOpenAI"));
        AddAzureDocumentIntelligence(services, configuration.GetSection("DocumentIntelligence"));
    }

    private static void AddAzureOpenAI(IServiceCollection services, IConfigurationSection section)
    {
        var endpoint = section["Endpoint"];
        var apiKey = section["ApiKey"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            return;

        var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

        if (section["ChatDeployment"] is { Length: > 0 } chatDeployment)
            services.AddSingleton<IChatClient>(client.GetChatClient(chatDeployment).AsIChatClient());

        if (section["EmbeddingDeployment"] is { Length: > 0 } embeddingDeployment)
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                client.GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator());
    }

    private static void AddAzureDocumentIntelligence(IServiceCollection services, IConfigurationSection section)
    {
        var endpoint = section["Endpoint"];
        var apiKey = section["ApiKey"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            return;

        var client = new DocumentIntelligenceClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        services.AddSingleton<IDocumentExtractor>(new AzureDocumentIntelligenceExtractor(client));
    }
}

/// <summary>
/// The Azure Document Intelligence implementation of the extraction seam (ADR-0005):
/// the prebuilt-layout model returns text plus tables as cell grids, digital PDFs
/// only. Pixels are never returned. Provider lock-in stays shallow — the stored
/// artifacts (text, cell grids) are provider-neutral, so a different extractor is a
/// different implementation of this one interface.
/// </summary>
internal sealed class AzureDocumentIntelligenceExtractor(DocumentIntelligenceClient client) : IDocumentExtractor
{
    public async Task<ExtractedDocument> ExtractAsync(byte[] pdf, CancellationToken ct)
    {
        var options = new AnalyzeDocumentOptions("prebuilt-layout", BinaryData.FromBytes(pdf));
        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, options, ct);
        var result = operation.Value;

        var tables = result.Tables?.Select(BuildGrid).ToList() ?? [];
        return new ExtractedDocument(result.Content ?? string.Empty, tables);
    }

    // Document Intelligence returns a table as a flat list of cells with row/column
    // indices; reassemble the grid the extraction seam promises (rows of cells).
    private static ExtractedTable BuildGrid(DocumentTable table)
    {
        var grid = new List<List<string>>(table.RowCount);
        for (var row = 0; row < table.RowCount; row++)
            grid.Add([.. Enumerable.Repeat(string.Empty, table.ColumnCount)]);

        foreach (var cell in table.Cells)
            grid[cell.RowIndex][cell.ColumnIndex] = cell.Content ?? string.Empty;

        return new ExtractedTable(grid.Select(row => (IReadOnlyList<string>)row).ToList());
    }
}
