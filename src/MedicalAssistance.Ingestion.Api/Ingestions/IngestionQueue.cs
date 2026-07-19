using System.Threading.Channels;
using MedicalAssistance.Ingestion.Api.Realtime;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The one way work enters the pipeline.
///
/// Queueing and announcing belong together. They were separate, and both retry
/// routes queued without announcing — so a doctor watching a recovery saw a
/// progress bar that did not move until Chunking, at the very moment they were
/// most likely to be watching it. Everything that enqueues now goes through
/// here, so a route added later cannot quietly forget the announcement.
/// </summary>
public sealed class IngestionQueue(
    Channel<Guid> channel,
    IngestionStore store,
    IngestionStatusPublisher publisher)
{
    /// <summary>
    /// Announces that an Ingestion is Queued and hands it to a worker.
    ///
    /// The doctor and patient come from the stored record, so a caller holding
    /// nothing but an id — every rerun — announces exactly what a fresh
    /// submission does.
    ///
    /// Announced before it is queued, never after: a worker can pick the id up
    /// the instant it is written, and publishing afterwards leaves a race where
    /// Chunking is announced before the Queued that precedes it.
    /// </summary>
    public async Task EnqueueAsync(Guid ingestionId, CancellationToken ct = default)
    {
        if (await store.GetIdentityAsync(ingestionId, ct) is { } identity)
            await publisher.PublishAsync(ingestionId, identity, IngestionStages.Queued, ct: ct);

        await channel.Writer.WriteAsync(ingestionId, ct);
    }
}
