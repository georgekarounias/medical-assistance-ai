using Microsoft.AspNetCore.SignalR;

namespace MedicalAssistance.Ingestion.Api.Realtime;

/// <summary>
/// Real-time ingestion status for the existing backend — the only client that
/// ever connects here (ADR-0007). It relays what it receives to doctors' devices
/// over its own channel; the phone app never talks to this service.
///
/// The hub is deliberately empty. Nothing is invoked on it: status flows one
/// way, server to client, and the events carry <c>doctorId</c> so the backend
/// can fan them out. There are no per-doctor groups either — with a single
/// subscriber there is nothing to route, and inventing groups would duplicate
/// the routing the backend already owns while forcing this service to track who
/// is online.
///
/// Events are a convenience, never the source of truth: a client that misses
/// them reads <c>GET /ingestions/{id}</c> and loses nothing.
/// </summary>
public sealed class IngestionStatusHub : Hub;
