using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// Finds work nobody is doing and puts it back on the queue — once when this
/// instance starts, and every <c>Ingestion:RecoverySweepInterval</c> (default 5
/// minutes) for as long as it runs.
///
/// Recovery used to happen only at startup, which was enough while every
/// instance queued every unfinished ingestion and raced for it. Now that an
/// instance puts down work another instance owns, the owner dying leaves that
/// ingestion belonging to a process that no longer exists: the instances that
/// stepped aside have already stepped aside, and nothing brings them back to it.
/// An upload could then sit at Processing, with a progress bar moving nowhere,
/// for as long as the fleet stayed up. Recovery has to be something the service
/// keeps doing rather than something it did once.
///
/// Abandoned is not a guess about elapsed time. An ingestion is being worked on
/// exactly while someone holds its advisory lock, and a lock belongs to a
/// database session — so a process that dies stops holding it, immediately and
/// without having to admit anything. Unfinished and unlocked therefore means
/// abandoned, with no lease to expire and no timeout to be wrong about. That is
/// also why every instance can sweep at once: the worst an extra pass costs is a
/// queue entry that some worker picks up, fails to lock, and drops.
/// </summary>
public sealed class IngestionRecoverySweep(
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    ILogger<IngestionRecoverySweep> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue("Ingestion:RecoverySweepInterval", TimeSpan.FromMinutes(5));
        if (interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Ingestion:RecoverySweepInterval must be greater than zero; it is {interval}. " +
                "Recovery cannot be turned off by setting it to nothing.");
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            // The first pass runs immediately: this is also the startup recovery,
            // and whatever the last process abandoned should not wait an interval.
            do
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // One bad pass must not end recovery for the life of the
                    // process. A background service that throws takes the host
                    // down with it, and losing recovery permanently because the
                    // database blinked is a far worse trade than sweeping again.
                    logger.LogError(exception, "Recovery sweep failed; trying again in {Interval}", interval);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var unfinished = await scope.ServiceProvider.GetRequiredService<IngestionStore>().FindUnfinishedAsync(ct);
        if (unfinished.Count == 0)
            return;

        // Read the rows first and the locks second. An ingestion claimed in
        // between reads as unowned and is queued again, which costs one worker a
        // lock it does not get; the other order would let work claimed and
        // released between the reads look owned and be skipped, which costs it
        // an entire interval.
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var owned = await PostgresAdvisoryLock.HeldKeysAsync(connection, ct);

        var queue = scope.ServiceProvider.GetRequiredService<IngestionQueue>();
        var abandoned = unfinished.Where(id => !owned.Contains(PostgresAdvisoryLock.KeyFor(id))).ToList();
        foreach (var ingestionId in abandoned)
            await queue.EnqueueAsync(ingestionId, ct);

        if (abandoned.Count > 0)
            logger.LogInformation("Requeued {Count} ingestions that no instance was running", abandoned.Count);
    }
}
