using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Tests.Fakes;

/// <summary>
/// Adapter for the IChatClient seam: replays scripted responses so tests
/// control exactly what "the LLM" says, with no network involved.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly ConcurrentQueue<string> _responses = new();

    public List<string> ReceivedPrompts { get; } = [];

    public void EnqueueResponse(string response) => _responses.Enqueue(response);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Agent instructions may arrive as ChatOptions.Instructions (how
        // ChatClientAgent sends them) or as a system message — record both.
        ReceivedPrompts.Add(string.Join("\n",
            new[] { options?.Instructions }
                .Concat(messages.Select(m => m.Text))
                .Where(s => !string.IsNullOrEmpty(s))));
        if (!_responses.TryDequeue(out var next))
            throw new InvalidOperationException("ScriptedChatClient has no scripted response left to return.");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, next)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming is not used by the ingestion pipeline.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata("scripted", defaultModelId: "scripted-model")
            : null;

    public void Dispose()
    {
    }
}
