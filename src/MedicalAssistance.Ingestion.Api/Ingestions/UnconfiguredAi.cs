using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

// Placeholder adapters at the AI seams. They keep the app bootable with no AI
// provider configured and fail loudly on first use; real Azure OpenAI adapters
// replace them via configuration, test fakes replace them via DI.

internal sealed class UnconfiguredChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("No chat provider is configured. Set the AzureOpenAI configuration section.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("No chat provider is configured. Set the AzureOpenAI configuration section.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class UnconfiguredEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("No embedding provider is configured. Set the AzureOpenAI configuration section.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
