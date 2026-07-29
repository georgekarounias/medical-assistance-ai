using System.Diagnostics;
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
/// every instance's recovery sweep sees every unfinished row, so a rolling
/// deploy hands the same ingestion to more than one of them. An advisory lock
/// held for the length of the run is what makes all but one put it back down,
/// and the claim itself refuses anything already finished — the lock settles who
/// runs an ingestion now, not whether it still needs running at all.
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

    // Each concurrent run holds a connection of its own for the length of the
    // ingestion. Program.cs refuses to start if the configured worker count
    // could claim enough of the pool to starve request handling.
    private async Task RunWorkerAsync(CancellationToken ct)
    {
        var maxAttempts = configuration.GetValue("Ingestion:MaxAttempts", 3);

        await foreach (var ingestionId in queue.Reader.ReadAllAsync(ct))
        {
            // Every log line for this run carries the ingestion id (T35). The scope
            // flows through the shared async-local scope provider, so the pipeline,
            // committer and summariser loggers inherit it without being handed it —
            // an operator can filter one document's whole run by this id alone.
            using var logScope = logger.BeginScope(
                new Dictionary<string, object> { ["IngestionId"] = ingestionId });

            // Wall-clock from pickup to terminal state, recorded as the ingestion
            // duration metric whichever way the run ends. Document type is unknown
            // until the payload is loaded, so a failure before that is tagged unknown.
            var stopwatch = Stopwatch.StartNew();
            var documentType = "unknown";

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
                    // if it dies first its lock dies with it and the next
                    // recovery sweep — this instance's included — finds the row
                    // unfinished and unowned.
                    logger.LogDebug(
                        "Ingestion {IngestionId} is already being run by another instance", ingestionId);
                    continue;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IngestionStore>();

                // Counting the attempt before doing the work is what makes the
                // cap hold for crashes: a run that takes the process down never
                // gets to report anything afterwards.
                switch (await store.TryClaimAsync(ingestionId, maxAttempts, ct))
                {
                    case ClaimOutcome.AttemptsExhausted:
                        logger.LogError(
                            "Ingestion {IngestionId} gave up after {MaxAttempts} attempts",
                            ingestionId, maxAttempts);
                        await FailAsync(
                            ingestionId,
                            $"Gave up after {maxAttempts} attempts without completing. " +
                            "Resubmit the document to try again with a fresh set of attempts.");
                        IngestionTelemetry.RecordOutcome(
                            IngestionTelemetry.OutcomeFailed, documentType, stopwatch.Elapsed);
                        continue;

                    case ClaimOutcome.NotClaimable:
                        // It finished while this entry waited its turn. Recovery
                        // queues the same id on more than one instance on
                        // purpose, so a queue entry outliving its work is
                        // ordinary — and running it again would re-embed a
                        // stored document and supersede any correction made to
                        // it since. Not this worker's outcome to count.
                        logger.LogDebug(
                            "Ingestion {IngestionId} has already finished; nothing left to run", ingestionId);
                        continue;
                }

                var request = await store.LoadRequestAsync(ingestionId, ct);
                documentType = request.DocumentType;

                // The document type and patient join the log scope now they are
                // known, enriching every line the strategy and committer write.
                using var runScope = logger.BeginScope(new Dictionary<string, object>
                {
                    ["DocumentType"] = documentType,
                    ["PatientId"] = request.PatientId,
                });

                // The root span of the ingestion — its children are the agent,
                // embedding and extraction spans the strategy opens. Ids and type
                // only, never patient content (ADR-0002/0006).
                using (var activity = IngestionTelemetry.StartActivity("ingest.document"))
                {
                    activity?.SetTag("ingestion.id", ingestionId);
                    activity?.SetTag("document.type", documentType);

                    // Deterministic routing (ADR-0004): the document's declared type
                    // selects its strategy. The type was validated at the door against
                    // the same registry, so this always resolves for a submitted
                    // document — an unknown type here would be a bug, and says so.
                    var strategy = scope.ServiceProvider
                        .GetRequiredService<IngestionStrategyRegistry>()
                        .For(documentType);
                    await strategy.IngestAsync(ingestionId, request, ct);
                }

                // The document is committed and searchable; now refresh the patient's
                // rolling overview from the full current set. Best-effort by contract —
                // it swallows its own failures, so it can never turn a completed
                // ingestion into a failed one.
                await scope.ServiceProvider
                    .GetRequiredService<PatientSummaryService>()
                    .RegenerateAsync(request.PatientId, ct);

                IngestionTelemetry.RecordOutcome(
                    IngestionTelemetry.OutcomeCompleted, documentType, stopwatch.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Ingestion {IngestionId} failed", ingestionId);
                IngestionTelemetry.RecordOutcome(
                    IngestionTelemetry.OutcomeFailed, documentType, stopwatch.Elapsed);
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

        // Read back from the record rather than carried in: the run that had this
        // in hand may have died mid-flight. Its own columns, not the stored
        // payload, so announcing a failure never deserializes a whole transcript.
        try
        {
            if (await store.GetIdentityAsync(ingestionId, CancellationToken.None) is { } identity)
            {
                await scope.ServiceProvider.GetRequiredService<IngestionStatusPublisher>().PublishAsync(
                    ingestionId, identity, IngestionStages.Failed, reason);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Could not announce the failure of ingestion {IngestionId}", ingestionId);
        }
    }
}
