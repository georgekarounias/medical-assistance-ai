using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// The service's own telemetry (T35): one <see cref="ActivitySource"/> for the
/// spans wrapped around every agent / embedding / extraction call, and one
/// <see cref="Meter"/> for the ingestion outcome and duration metrics. Both are
/// registered with the OpenTelemetry SDK in <c>Program.cs</c> by the shared
/// <see cref="Name"/>; nothing here decides where telemetry goes — export is the
/// SDK's job, gated on configuration.
///
/// Every attribute recorded through this type is an id or a count — never chunk or
/// transcript text. Telemetry leaving the process must not carry patient content
/// (ADR-0002/0006): a span says <em>how many</em> chunks were embedded and for
/// which ingestion, never <em>what</em> they said.
/// </summary>
public static class IngestionTelemetry
{
    /// <summary>The name both the <see cref="ActivitySource"/> and the <see cref="Meter"/> share, and that the SDK subscribes to.</summary>
    public const string Name = "MedicalAssistance.Ingestion";

    private static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    // Outcome as a counter tagged with the terminal result, so "how many ingestions
    // failed" and "how many completed" are one instrument sliced by a dimension
    // rather than two instruments to keep in step.
    private static readonly Counter<long> Outcomes =
        Meter.CreateCounter<long>("ingestion.outcomes", unit: "{ingestion}",
            description: "Ingestions that reached a terminal state, tagged by outcome.");

    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("ingestion.duration", unit: "ms",
            description: "Wall-clock time from a worker picking an ingestion up to its terminal state.");

    /// <summary>
    /// Starts a span around one unit of ingestion work — an agent call, an
    /// embedding batch, a PDF extraction, or the whole document run. Returns null
    /// when no listener is subscribed, so callers wrap the work in <c>using</c> and
    /// pay nothing when telemetry is off.
    /// </summary>
    public static Activity? StartActivity(string name) => Source.StartActivity(name);

    /// <summary>
    /// Records a terminal ingestion outcome and how long it took. <paramref name="outcome"/>
    /// is a low-cardinality label (<c>completed</c> / <c>failed</c>), so it is safe
    /// as a metric dimension; <paramref name="documentType"/> is the Document Type,
    /// never patient content.
    /// </summary>
    public static void RecordOutcome(string outcome, string documentType, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "outcome", outcome },
            { "document.type", documentType },
        };
        Outcomes.Add(1, tags);
        Duration.Record(elapsed.TotalMilliseconds, tags);
    }

    /// <summary>The <c>completed</c> outcome label.</summary>
    public const string OutcomeCompleted = "completed";

    /// <summary>The <c>failed</c> outcome label.</summary>
    public const string OutcomeFailed = "failed";
}
