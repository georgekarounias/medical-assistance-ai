using MedicalAssistance.Ingestion.Api.Ingestions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// Plain-OpenAI provider wiring: the service supports OpenAI (or any
/// OpenAI-compatible endpoint) for chat and embeddings the same way it supports
/// Azure OpenAI — provider choice is configuration, not architecture. These are
/// pure registration tests (no database, no Docker): registration only constructs
/// clients, so no network call happens, and the resolved seam reports the model it
/// was configured with through the standard Microsoft.Extensions.AI metadata.
/// </summary>
public class OpenAiProviderWiringTests
{
    [Fact]
    public void OpenAI_chat_configuration_registers_a_chat_client_for_the_configured_model()
    {
        var provider = BuildProvider(new()
        {
            ["OpenAIChat:ApiKey"] = "sk-test",
            ["OpenAIChat:Model"] = "gpt-4.1-mini",
        });

        var chatClient = provider.GetService<IChatClient>();
        Assert.NotNull(chatClient);

        var metadata = (ChatClientMetadata?)chatClient.GetService(typeof(ChatClientMetadata));
        Assert.Equal("gpt-4.1-mini", metadata?.DefaultModelId);
    }

    [Fact]
    public void OpenAI_embedding_configuration_registers_a_generator_for_the_configured_model()
    {
        var provider = BuildProvider(new()
        {
            ["OpenAIEmbeddings:ApiKey"] = "sk-test",
            ["OpenAIEmbeddings:Model"] = "text-embedding-3-small",
        });

        var generator = provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
        Assert.NotNull(generator);

        var metadata = (EmbeddingGeneratorMetadata?)generator.GetService(typeof(EmbeddingGeneratorMetadata));
        Assert.Equal("text-embedding-3-small", metadata?.DefaultModelId);
    }

    [Fact]
    public void No_OpenAI_configuration_registers_nothing_so_the_placeholder_survives()
    {
        var provider = BuildProvider(new());

        Assert.Null(provider.GetService<IChatClient>());
        Assert.Null(provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    // Registers only the OpenAI providers over an otherwise empty container, so a
    // resolved seam proves the OpenAI wiring put it there (in the app the same call
    // runs after the Unconfigured* placeholders and before the Azure providers, so
    // OpenAI beats the placeholder and Azure — when also configured — wins last).
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddOpenAiProviders(configuration);
        return services.BuildServiceProvider();
    }
}
