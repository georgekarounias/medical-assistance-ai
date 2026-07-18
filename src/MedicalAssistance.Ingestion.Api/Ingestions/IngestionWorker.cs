using System.Threading.Channels;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Consumes queued Ingestions with bounded parallelism (<c>Ingestion:WorkerCount</c>,
/// default 4 — the real throughput ceiling is AI-provider rate limits). Each
/// ingestion runs in its own DI scope; failures are caught and persisted as
/// Failed with the error message, never lost silently.
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
        await foreach (var ingestionId in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IngestionStore>();
                await store.MarkProcessingAsync(ingestionId, ct);
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
