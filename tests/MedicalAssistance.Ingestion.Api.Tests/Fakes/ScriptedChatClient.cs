using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace MedicalAssistance.Ingestion.Api.Tests.Fakes;

/// <summary>
/// Adapter for the IChatClient seam: replays scripted responses so tests
/// control exactly what "the LLM" says, with no network involved.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly ConcurrentQueue<(string Response, TaskCompletionSource? Gate)> _responses = new();

    private readonly List<string> _receivedPrompts = [];

    /// <summary>
    /// Every prompt the pipeline has sent, newest last. A snapshot, because
    /// several ingestions can be in flight at once and tests read this while
    /// workers are writing it.
    /// </summary>
    public IReadOnlyList<string> ReceivedPrompts
    {
        get
        {
            lock (_receivedPrompts)
                return _receivedPrompts.ToArray();
        }
    }

    public void EnqueueResponse(string response) => _responses.Enqueue((response, null));

    /// <summary>
    /// Enqueues a response that does not come back until the returned handle is
    /// called — how a test holds an ingestion in Processing for as long as it
    /// needs to, without sleeping and hoping.
    /// </summary>
    public Action EnqueueBlockingResponse(string response)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _responses.Enqueue((response, gate));
        return () => gate.TrySetResult();
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Agent instructions may arrive as ChatOptions.Instructions (how
        // ChatClientAgent sends them) or as a system message — record both.
        var prompt = string.Join("\n",
            new[] { options?.Instructions }
                .Concat(messages.Select(m => m.Text))
                .Where(s => !string.IsNullOrEmpty(s)));
        lock (_receivedPrompts)
            _receivedPrompts.Add(prompt);
        if (!_responses.TryDequeue(out var next))
            throw new InvalidOperationException("ScriptedChatClient has no scripted response left to return.");

        if (next.Gate is not null)
            await next.Gate.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, next.Response));
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
