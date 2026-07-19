using System.Threading.Channels;
using MedicalAssistance.Ingestion.Api.Realtime;
using Npgsql;

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
///
/// The queue is per-instance and in-memory, but the work it names is shared:
/// every instance's startup recovery sees every unfinished row, so a rolling
/// deploy hands the same ingestion to more than one of them. An advisory lock
/// held for the length of the run is what makes all but one put it back down.
/// </summary>
public sealed class IngestionWorker(
    Channel<Guid> queue,
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
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
                // Ownership first, and held on its own connection for the whole
                // run. Whoever gets it is the only instance working this
                // ingestion; the lock lives in the database session, so if this
                // process dies the claim dies with it and the next startup is
                // free to pick the work up.
                await using var connection = await dataSource.OpenConnectionAsync(ct);
                await using var ownership = await PostgresAdvisoryLock.TryAcquireAsync(
                    connection, PostgresAdvisoryLock.KeyFor(ingestionId), ct);

                if (ownership is null)
                {
                    // Another instance has it. Not an error, and not something to
                    // retry: that instance will carry it to a terminal state, and
                    // if it dies first the row is left unfinished for a later
                    // startup to find.
                    logger.LogDebug(
                        "Ingestion {IngestionId} is already being run by another instance", ingestionId);
                    continue;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IngestionStore>();

                // Counting the attempt before doing the work is what makes the
                // cap hold for crashes: a run that takes the process down never
                // gets to report anything afterwards.
                if (!await store.TryClaimAsync(ingestionId, maxAttempts, ct))
                {
                    logger.LogError(
                        "Ingestion {IngestionId} gave up after {MaxAttempts} attempts", ingestionId, maxAttempts);
                    await FailAsync(
                        ingestionId,
                        $"Gave up after {maxAttempts} attempts without completing. " +
                        "Resubmit the document to try again with a fresh set of attempts.");
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
                await FailAsync(ingestionId, exception.Message);
            }
        }
    }

    /// <summary>
    /// Records a failure and tells the doctor why. The announcement runs on its
    /// own scope and its own cancellation, because the reason a run failed may
    /// well be the reason its scope is unusable.
    /// </summary>
    private async Task FailAsync(Guid ingestionId, string reason)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IngestionStore>();
        await store.MarkFailedAsync(ingestionId, reason, CancellationToken.None);

        // The stored payload is the only place the doctor and patient are still
        // known here — the run that had them may have died mid-flight.
        try
        {
            var request = await store.LoadRequestAsync(ingestionId, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IngestionStatusPublisher>().PublishAsync(
                ingestionId, request.DoctorId, request.PatientId, IngestionStages.Failed, reason);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Could not announce the failure of ingestion {IngestionId}", ingestionId);
        }
    }
}
