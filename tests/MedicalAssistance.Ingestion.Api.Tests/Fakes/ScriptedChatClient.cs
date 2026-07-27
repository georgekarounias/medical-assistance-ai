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

    // Summariser agents run outside the chunker/mapper flow: the patient summariser
    // after every ingestion, the lab summariser during every LabReport. They are
    // background work most tests do not script, so they get their own channel — their
    // calls never draw from, nor starve, the chunker/mapper responses queued above,
    // and an unscripted summariser call returns a harmless default instead of
    // throwing. Detected by a stable phrase from each agent's instructions.
    private static readonly string[] SummarizerInstructionMarkers =
        ["rolling clinical overview", "clinical summary of a laboratory report"];
    private readonly ConcurrentQueue<string> _summaryResponses = new();
    private readonly List<string> _receivedSummaryPrompts = [];

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
    /// Scripts the next patient-summariser response. Optional: an unscripted
    /// summariser call returns a default overview, so most tests need not set one.
    /// </summary>
    public void EnqueueSummaryResponse(string response) => _summaryResponses.Enqueue(response);

    /// <summary>
    /// Every prompt the patient summariser has sent, kept apart from
    /// <see cref="ReceivedPrompts"/> so the summariser's post-ingestion call — which
    /// happens after every ingestion — never disturbs tests that count chunker calls.
    /// </summary>
    public IReadOnlyList<string> ReceivedSummaryPrompts
    {
        get
        {
            lock (_receivedSummaryPrompts)
                return _receivedSummaryPrompts.ToArray();
        }
    }

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
        // Summariser calls are recorded and served apart from the chunker/mapper
        // queue: they run after every ingestion, so counting them among the chunker
        // prompts — or drawing a chunker response — would disturb every test that
        // ingests. Serve a scripted overview if one was set, otherwise a default;
        // never throw, never steal.
        if (SummarizerInstructionMarkers.Any(marker => prompt.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            lock (_receivedSummaryPrompts)
                _receivedSummaryPrompts.Add(prompt);

            var overview = _summaryResponses.TryDequeue(out var scripted)
                ? scripted
                : "Rolling patient overview (scripted default).";
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, overview));
        }

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
