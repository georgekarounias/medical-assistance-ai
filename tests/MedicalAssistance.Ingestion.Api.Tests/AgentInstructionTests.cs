using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

public class AgentInstructionTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private const string ValidPlan = """
        {
          "chunks": [ { "startLine": 0, "endLine": 1, "contextBlurb": "Greeting and complaint." } ],
          "summary": "Short session about headaches."
        }
        """;

    [Fact]
    public async Task Instructions_are_seeded_loaded_at_startup_and_stamped_on_ingestions()
    {
        // --- 1. A fresh boot seeds the default instructions, versioned. ---
        var (seededInstructions, seededVersion) = await ReadInstructionRowAsync("TranscriptChunker");
        Assert.Contains("segment doctor-patient session transcripts", seededInstructions);
        Assert.Equal(1, seededVersion);

        // --- 2. The running app uses them, and stamps the ingestion. ---
        var client = fixture.Factory.CreateClient();
        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var firstId = await PostTranscriptAndAwaitCompletedAsync(client, sequenceNumber: 1);

        Assert.Contains("segment doctor-patient session transcripts", fixture.ChatClient.ReceivedPrompts.Last());
        var (version1, model1) = await ReadIngestionStampAsync(firstId);
        Assert.Equal(1, version1);
        Assert.Equal("scripted-model", model1);

        // --- 3. Editing the row mid-flight changes nothing: loaded once, singleton. ---
        await UpdateInstructionRowAsync("TranscriptChunker",
            "You are MARKER-XYZ-V2. Return the same JSON chunk plan contract as always.", version: 2);

        fixture.ChatClient.EnqueueResponse(ValidPlan);
        var secondId = await PostTranscriptAndAwaitCompletedAsync(client, sequenceNumber: 2);
        Assert.DoesNotContain("MARKER-XYZ-V2", fixture.ChatClient.ReceivedPrompts.Last());
        var (version2, _) = await ReadIngestionStampAsync(secondId);
        Assert.Equal(1, version2);

        // --- 4. A new boot picks up the edited instructions and stamps their version. ---
        var restartedChat = new ScriptedChatClient();
        await using var restartedApp = fixture.CreateFactory(restartedChat);
        var restartedClient = restartedApp.CreateClient();
        restartedChat.EnqueueResponse(ValidPlan);
        var thirdId = await PostTranscriptAndAwaitCompletedAsync(restartedClient, sequenceNumber: 3);

        Assert.Contains("MARKER-XYZ-V2", restartedChat.ReceivedPrompts.Last());
        var (version3, model3) = await ReadIngestionStampAsync(thirdId);
        Assert.Equal(2, version3);
        Assert.Equal("scripted-model", model3);
    }

    private static async Task<Guid> PostTranscriptAndAwaitCompletedAsync(HttpClient client, int sequenceNumber)
    {
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "doc-1",
            patientId = "pat-instr",
            sessionId = "sess-instr",
            sequenceNumber,
            language = "en",
            transcript = "Doctor: Good morning.\nPatient: I have headaches.",
        });
        response.EnsureSuccessStatusCode();
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("ingestionId").GetGuid();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}");
            var state = status.GetProperty("status").GetString();
            if (state == "Completed")
                return ingestionId;
            if (state == "Failed")
                throw new InvalidOperationException($"Ingestion failed: {status.GetRawText()}");
            await Task.Delay(100);
        }
        throw new TimeoutException("Ingestion never completed.");
    }

    private async Task<(string Instructions, int Version)> ReadInstructionRowAsync(string name)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT instructions, version FROM agent_instructions WHERE name = $1", connection);
        command.Parameters.AddWithValue(name);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"No agent_instructions row for '{name}'.");
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private async Task UpdateInstructionRowAsync(string name, string instructions, int version)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE agent_instructions SET instructions = $2, version = $3 WHERE name = $1", connection);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(instructions);
        command.Parameters.AddWithValue(version);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<(int? InstructionVersion, string? ChatModel)> ReadIngestionStampAsync(Guid ingestionId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT instruction_version, chat_model FROM ingestions WHERE id = $1", connection);
        command.Parameters.AddWithValue(ingestionId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
