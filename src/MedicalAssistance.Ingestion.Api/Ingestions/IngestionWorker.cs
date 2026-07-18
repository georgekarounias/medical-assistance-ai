using System.Threading.Channels;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Consumes queued Ingestions with bounded parallelism (<c>Ingestion:WorkerCount</c>,
/// default 4 — the real throughput ceiling is AI-provider rate limits). Each
/// ingestion runs in its own DI scope; failures are caught and persisted as
/// Failed with the error message, never lost silently.
///
/// Every pickup is counted, and an Ingestion that has used up its attempts
/// (<c>Ingestion:MaxAttempts</c>, default 3) is failed instead of run again —
/// otherwise a document that crashes the process would be handed straight back
/// to the next startup, forever.
/// </summary>
public sealed class IngestionWorker(
    Channel<Guid> queue,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = configuration.GetValue("Ingestion:WorkerCount", 4);
        return Task.WhenAll(Enumerable.Range(0, workerCount).Select(_ => RunWorkerAsync(stoppingToken)));
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        var maxAttempts = configuration.GetValue("Ingestion:MaxAttempts", 3);

        await foreach (var ingestionId in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IngestionStore>();

                // Counting the attempt before doing the work is what makes the
                // cap hold for crashes: a run that takes the process down never
                // gets to report anything afterwards.
                if (!await store.TryClaimAsync(ingestionId, maxAttempts, ct))
                {
                    logger.LogError(
                        "Ingestion {IngestionId} gave up after {MaxAttempts} attempts", ingestionId, maxAttempts);
                    await store.MarkFailedAsync(
                        ingestionId,
                        $"Gave up after {maxAttempts} attempts without completing. " +
                        "Resubmit the document to try again with a fresh set of attempts.",
                        ct);
                    continue;
                }

                var request = await store.LoadRequestAsync(ingestionId, ct);
                var strategy = scope.ServiceProvider.GetRequiredService<TranscriptIngestionStrategy>();
                await strategy.IngestAsync(ingestionId, request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Ingestion {IngestionId} failed", ingestionId);
                await using var failureScope = scopeFactory.CreateAsyncScope();
                await failureScope.ServiceProvider.GetRequiredService<IngestionStore>()
                    .MarkFailedAsync(ingestionId, exception.Message, CancellationToken.None);
            }
        }
    }
}
