using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Ingestions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The Orchestrator is a deterministic <c>documentType → strategy</c> lookup
/// (ADR-0004), and the same registry is the single source of truth for which
/// types the door accepts. Two properties follow, each proven here at the HTTP
/// seam: the worker dispatches a document to the strategy registered for its
/// type, and request validation accepts exactly the registered types — so
/// registering a strategy is the one act that adds a Document Type.
/// </summary>
public class StrategyRoutingTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    [Fact]
    public async Task The_worker_routes_a_document_to_the_strategy_registered_for_its_type()
    {
        var routed = new RoutedIngestions();

        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(routed);

                // Replace the real transcript strategy with a recording double for
                // the same Document Type. If the worker still resolved
                // TranscriptIngestionStrategy by hand instead of asking the
                // registry, the double would never run — and the concrete type is
                // no longer even registered, so that path could not resolve at all.
                services.RemoveAll<IIngestionStrategy>();
                services.AddScoped<IIngestionStrategy, RecordingTranscriptStrategy>();
            }));
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = DocumentTypes.SessionTranscript,
            doctorId = "doc-routing",
            patientId = "pat-routing",
            sessionId = "sess-routing",
            sequenceNumber = 1,
            transcript = "Doctor: Good morning.\nPatient: Hello.",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("ingestionId").GetGuid();

        var (status, detail) = await WaitForTerminalStatusAsync(client, ingestionId);
        Assert.True(status == "Completed", $"Expected Completed but ingestion ended as: {detail}");

        // The double carried this ingestion — proof the worker routed through the
        // registry rather than defaulting to a hard-wired transcript pipeline.
        Assert.Contains(ingestionId, routed.IngestionIds);
    }

    [Fact]
    public async Task A_document_type_is_accepted_at_the_door_only_because_its_strategy_is_registered()
    {
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                // One extra strategy for a brand-new type, alongside the transcript
                // strategy — the only act needed to make the type known.
                services.AddScoped<IIngestionStrategy, RegisteredOnlyStrategy>()));
        var client = factory.CreateClient();

        // The new type is missing a required field, so it stops at validation —
        // before intake would need the per-type identity that is T28's business.
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = RegisteredOnlyStrategy.DocumentTypeName,
            patientId = "pat-door",
            transcript = "body",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        // The missing field is reported...
        Assert.True(errors.TryGetProperty("doctorId", out _), $"Expected a doctorId error in: {errors}");

        // ...but the type itself is NOT rejected as unsupported: the registry made
        // it a known type by the single act of registering its strategy. Before the
        // registry drove validation, this type would have failed on documentType.
        Assert.False(
            errors.TryGetProperty("documentType", out _),
            $"'{RegisteredOnlyStrategy.DocumentTypeName}' should be accepted as a supported type, but: {errors}");
    }

    private static async Task<(string Status, string Detail)> WaitForTerminalStatusAsync(
        HttpClient client, Guid ingestionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var lastSeen = "<never fetched>";
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");
            lastSeen = status.GetRawText();
            var state = status.GetProperty("status").GetString()!;
            if (state is "Completed" or "Failed")
                return (state, lastSeen);
            await Task.Delay(100);
        }
        throw new TimeoutException($"Ingestion never reached a terminal status. Last response: {lastSeen}");
    }
}

/// <summary>
/// A recording stand-in for the transcript strategy. It records every ingestion
/// it is handed — the routing fingerprint — and reaches Completed through the
/// same shared store the real strategy uses, with no chunks of its own.
/// </summary>
public sealed class RecordingTranscriptStrategy(IngestionStore store, RoutedIngestions routed) : IIngestionStrategy
{
    public string DocumentType => DocumentTypes.SessionTranscript;

    public async Task IngestAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct)
    {
        routed.Record(ingestionId);
        var documentId = DocumentIdentity.For(request);
        await store.CompleteWithChunksAsync(
            ingestionId, documentId, request, [], instructionVersion: 0, chatModel: "routing-double",
            embeddingModel: null, analytes: null, analytesExtracted: null, documentSummary: null, ct);
    }
}

/// <summary>
/// A strategy for a Document Type that exists only in these tests, registered to
/// prove that registering a strategy is what makes a type known at the door. It
/// is never actually run, so ingesting through it is a defect if it happens.
/// </summary>
public sealed class RegisteredOnlyStrategy : IIngestionStrategy
{
    /// <summary>A Document Type no production strategy claims.</summary>
    public const string DocumentTypeName = "RoutingProbe";

    public string DocumentType => DocumentTypeName;

    public Task IngestAsync(Guid ingestionId, IngestionRequest request, CancellationToken ct) =>
        throw new NotSupportedException("The registered-only probe strategy was not expected to run.");
}

/// <summary>Records which ingestions a strategy double was handed — shared as a singleton with the test.</summary>
public sealed class RoutedIngestions
{
    private readonly ConcurrentBag<Guid> _ids = [];

    public void Record(Guid ingestionId) => _ids.Add(ingestionId);

    public IReadOnlyCollection<Guid> IngestionIds => _ids;
}
