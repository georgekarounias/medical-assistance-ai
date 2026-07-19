using MedicalAssistance.Ingestion.Api.Ingestions;
using Microsoft.AspNetCore.SignalR;

namespace MedicalAssistance.Ingestion.Api.Realtime;

/// <summary>
/// The named steps an Ingestion passes through, as reported to the doctor.
///
/// Finer-grained than the REST lifecycle on purpose: <see cref="Chunking"/>,
/// <see cref="Embedding"/> and <see cref="Storing"/> are all
/// <c>Processing</c> as far as <c>GET /ingestions/{id}</c> is concerned. A
/// progress bar needs to say where the work actually is; a status endpoint only
/// needs to say whether it is done.
/// </summary>
public static class IngestionStages
{
    /// <summary>Accepted and durably recorded, waiting for a worker.</summary>
    public const string Queued = "Queued";

    /// <summary>The chunking agent is proposing boundaries.</summary>
    public const string Chunking = "Chunking";

    /// <summary>Chunk texts are being embedded.</summary>
    public const string Embedding = "Embedding";

    /// <summary>Chunks and status are being committed.</summary>
    public const string Storing = "Storing";

    /// <summary>The document is ingested and searchable.</summary>
    public const string Completed = "Completed";

    /// <summary>The run ended without ingesting the document; the reason travels with the event.</summary>
    public const string Failed = "Failed";
}

/// <summary>One progress announcement about one Ingestion.</summary>
public sealed record IngestionStatusEvent
{
    /// <summary>The ingestion this is about — the id the caller already holds.</summary>
    public required Guid IngestionId { get; init; }

    /// <summary>
    /// The Document being ingested. Present so a client can say which transcript
    /// it is reporting on without a second call to discover what an ingestion id
    /// refers to — and so no consumer rebuilds the identifier from its parts,
    /// which would leave each of them to break quietly if it ever changed.
    /// </summary>
    public required string DocumentId { get; init; }

    /// <summary>The Session the document belongs to (transcripts and session-linked notes).</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The doctor who submitted the document. Present on every event because the
    /// backend routes on it: this service has no idea which devices are online.
    /// </summary>
    public required string DoctorId { get; init; }

    /// <summary>The patient the document is about.</summary>
    public required string PatientId { get; init; }

    /// <summary>Which step was reached — see <see cref="IngestionStages"/>.</summary>
    public required string Stage { get; init; }

    /// <summary>Why it failed; set only on <see cref="IngestionStages.Failed"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>When the step was reached, in UTC.</summary>
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Announces ingestion progress to the backend over the status hub.
///
/// Publishing can never affect an ingestion. Every send is awaited so ordering
/// holds, but a hub failure is logged and swallowed: a doctor losing sight of a
/// progress bar is a nuisance, while a transcript failing to ingest because a
/// notification could not be delivered would be a real fault. Clients that miss
/// events read <c>GET /ingestions/{id}</c>, which is always the truth.
/// </summary>
public sealed class IngestionStatusPublisher(
    IHubContext<IngestionStatusHub> hub,
    ILogger<IngestionStatusPublisher> logger)
{
    /// <summary>The client-side method name the backend subscribes to.</summary>
    public const string ClientMethod = "IngestionStatusChanged";

    /// <summary>
    /// Announces that an Ingestion reached a stage.
    ///
    /// Takes the whole identity rather than loose ids so that every event names
    /// its document; a caller cannot announce a stage while leaving out what the
    /// stage is about.
    /// </summary>
    public async Task PublishAsync(
        Guid ingestionId,
        IngestionIdentity identity,
        string stage,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        var statusEvent = new IngestionStatusEvent
        {
            IngestionId = ingestionId,
            DocumentId = identity.DocumentId,
            DoctorId = identity.DoctorId,
            PatientId = identity.PatientId,
            SessionId = identity.SessionId,
            Stage = stage,
            ErrorMessage = errorMessage,
            OccurredAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await hub.Clients.All.SendAsync(ClientMethod, statusEvent, ct);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not publish {Stage} for ingestion {IngestionId}; the REST status is unaffected",
                stage,
                ingestionId);
        }
    }
}
