using MedicalAssistance.Ingestion.Api.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The status hub. It exists so the backend can watch ingestions progress in
/// real time instead of polling, and it is gated by the same shared secret as
/// the REST surface — a hub that anyone could connect to would undo the gate
/// the rest of the service stands behind.
/// </summary>
public class StatusHubTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string HubPath = "hubs/ingestion-status";

    [Fact]
    public async Task The_backend_connects_to_the_hub_with_the_shared_secret()
    {
        await using var connection = BuildConnection(IngestionApiFixture.ApiKey);

        await connection.StartAsync();

        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task A_handshake_without_the_secret_is_refused()
    {
        await using var connection = BuildConnection(apiKey: null);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        Assert.Contains("401", failure.Message);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task A_handshake_with_the_wrong_secret_is_refused()
    {
        await using var connection = BuildConnection("not-the-secret");

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        Assert.Contains("401", failure.Message);
    }

    [Fact]
    public async Task The_rotation_secret_opens_the_hub_too()
    {
        // Rotation has to cover the hub as well; a key swap that quietly broke
        // real-time status would be found by a doctor, not by an alert.
        await using var connection = BuildConnection(IngestionApiFixture.RotationApiKey);

        await connection.StartAsync();

        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    private HubConnection BuildConnection(string? apiKey)
    {
        var server = fixture.Factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, HubPath), options =>
            {
                // The in-process test server speaks HTTP, not sockets, so the
                // handshake runs over long polling — the negotiate and poll
                // requests still carry (or lack) the secret exactly as they would.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                if (apiKey is not null)
                    options.Headers.Add(ApiKeyAuthentication.HeaderName, apiKey);
            })
            .Build();
    }
}
