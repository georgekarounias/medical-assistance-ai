using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Wires plain OpenAI (or any OpenAI-compatible endpoint) for chat and embeddings
/// when its configuration is present, in place of the <c>Unconfigured*</c>
/// placeholders — the same shape as <see cref="AzureAi"/>, so provider choice stays
/// configuration, not architecture. Chat and embeddings are gated independently
/// (<c>OpenAIChat</c>, <c>OpenAIEmbeddings</c>): either can be OpenAI while the other
/// is Azure or unset, and an <c>OpenAIChat:BaseUrl</c> points a compatible gateway.
///
/// Registration only constructs clients; no network call happens until an ingestion
/// runs. In the app this runs after the placeholders and before <see cref="AzureAi"/>,
/// so a configured OpenAI client beats the placeholder as a later registration, and a
/// configured Azure client — added last — still wins when both are set. Secrets come
/// from user-secrets or the estate's secret store, never source.
/// </summary>
public static class OpenAiProviders
{
    /// <summary>Registers whichever of the OpenAI chat and embedding providers are configured.</summary>
    public static void AddOpenAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        AddChat(services, configuration.GetSection("OpenAIChat"));
        AddEmbeddings(services, configuration.GetSection("OpenAIEmbeddings"));
    }

    private static void AddChat(IServiceCollection services, IConfigurationSection section)
    {
        if (section["ApiKey"] is not { Length: > 0 } apiKey)
            return;

        var client = CreateClient(apiKey, section["BaseUrl"]);
        var model = section["Model"] is { Length: > 0 } m ? m : "gpt-4.1-mini";
        services.AddSingleton<IChatClient>(client.GetChatClient(model).AsIChatClient());
    }

    private static void AddEmbeddings(IServiceCollection services, IConfigurationSection section)
    {
        if (section["ApiKey"] is not { Length: > 0 } apiKey)
            return;

        var client = CreateClient(apiKey, section["BaseUrl"]);
        var model = section["Model"] is { Length: > 0 } m ? m : "text-embedding-3-small";
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            client.GetEmbeddingClient(model).AsIEmbeddingGenerator());
    }

    // A BaseUrl lets the same wiring target an OpenAI-compatible gateway; absent it,
    // the client talks to api.openai.com. Only the endpoint is configurable here —
    // provider-neutral stored artifacts keep the lock-in shallow either way.
    private static OpenAIClient CreateClient(string apiKey, string? baseUrl)
    {
        var credential = new ApiKeyCredential(apiKey);
        if (baseUrl is { Length: > 0 } && Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint))
            return new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = endpoint });

        return new OpenAIClient(credential);
    }
}
