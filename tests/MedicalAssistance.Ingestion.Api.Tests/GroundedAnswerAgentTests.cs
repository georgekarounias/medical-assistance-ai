using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The grounded-answer agent (T43): a Microsoft Agent Framework agent built from the
/// DB-seeded <c>GroundedChat</c> instructions (ADR-0008), answering only from the
/// supplied [E#] evidence, in the question's language, non-streaming.
///
/// These tests assert what is observable at the seam: that the generation call is
/// built from the DB-owned prompt and carries the retrieved evidence as [E#] items,
/// that the answer's language follows the question, and that conversation context
/// frames the question without ever becoming evidence (ADR-0010). Whether a real
/// model then obeys "assert only what the evidence supports" is measured by the
/// golden sets (T51), not assertable against a scripted client.
/// </summary>
public class GroundedAnswerAgentTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private static readonly float[] Query = [1f, 0f];
    private static readonly float[] Near = [0.98f, 0.02f];
    private static readonly float[] Orthogonal = [0f, 1f];

    [Fact]
    public async Task The_generation_call_uses_the_db_seeded_prompt_and_supplies_the_evidence_as_labelled_items()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "Does the patient take insulin?";
        emb.Pin(question, Query);
        const string line = "The patient reports taking insulin injections every single day for their diabetes.";
        await IngestTranscriptAsync(client, chat, emb, "t43-alice", "dr-a", "a-s1", line, "Diabetes.", "Summary one.");

        chat.EnqueueResponse("Yes — the patient takes insulin daily [E1].");
        await client.PostAsJsonAsync("/patients/t43-alice/chat/answer", new { doctorId = "dr-a", question });

        // The generation call is the one carrying the evidence block this generator builds.
        var generationPrompt = chat.ReceivedPrompts.Single(p => p.Contains("Evidence Items:"));

        // Built from the DB-owned instructions — no code-side copy (ADR-0008).
        var seeded = await SeededInstructionsAsync("GroundedChat");
        Assert.Contains(seeded, generationPrompt);

        // The retrieved chunk is presented to the model as [E1], and the question is asked.
        Assert.Contains("[E1] " + line, generationPrompt);
        Assert.Contains(question, generationPrompt);
    }

    [Fact]
    public async Task The_answer_language_follows_the_question()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        // A Greek question over an English record — retrieval is cross-language, and
        // the answer is written in the question's language (ADR-0012).
        const string greekQuestion = "Παίρνει ινσουλίνη ο ασθενής;";
        emb.Pin(greekQuestion, Query);
        const string line = "The patient reports taking insulin injections every single day for their diabetes.";
        await IngestTranscriptAsync(client, chat, emb, "t43-nikos", "dr-a", "n-s1", line, "Diabetes.", "Summary.");

        chat.EnqueueResponse("Ναι, ο ασθενής παίρνει ινσουλίνη καθημερινά [E1].");
        var response = await client.PostAsJsonAsync(
            "/patients/t43-nikos/chat/answer", new { doctorId = "dr-a", question = greekQuestion });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("el", body.GetProperty("language").GetString());

        // The question reaches the model as asked, in Greek.
        Assert.Contains(greekQuestion, chat.ReceivedPrompts.Single(p => p.Contains("Evidence Items:")));
    }

    [Fact]
    public async Task Conversation_context_frames_the_question_but_is_never_an_evidence_item()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        const string question = "And is it well controlled?";
        emb.Pin(question, Query);
        const string line = "The patient reports taking insulin injections every single day for their diabetes.";
        await IngestTranscriptAsync(client, chat, emb, "t43-carol", "dr-a", "c-s1", line, "Diabetes.", "Summary.");

        const string priorSummary = "Earlier the doctor asked about the patient's diabetes medication.";
        chat.EnqueueResponse("Based on the record, control is not stated [E1].");
        await client.PostAsJsonAsync("/patients/t43-carol/chat/answer", new
        {
            doctorId = "dr-a",
            question,
            priorSummary,
            recentTurns = new[] { new { role = "user", text = "Tell me about the insulin." } },
        });

        var generationPrompt = chat.ReceivedPrompts.Single(p => p.Contains("Evidence Items:"));
        var evidenceSection = generationPrompt[generationPrompt.IndexOf("Evidence Items:", StringComparison.Ordinal)..];

        // Context is in the prompt to interpret the question…
        Assert.Contains(priorSummary, generationPrompt);
        Assert.Contains("Tell me about the insulin.", generationPrompt);

        // …but the evidence block holds only the retrieved chunk — memory is never a
        // source of fact (ADR-0010).
        Assert.Contains("[E1] " + line, evidenceSection);
        Assert.DoesNotContain(priorSummary, evidenceSection);
        Assert.DoesNotContain("Tell me about the insulin.", evidenceSection);
    }

    private async Task<string> SeededInstructionsAsync(string agentName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT instructions FROM agent_instructions WHERE name = $1", connection);
        command.Parameters.AddWithValue(agentName);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task IngestTranscriptAsync(
        HttpClient client, ScriptedChatClient chat, ControllableEmbeddingGenerator emb,
        string patientId, string doctorId, string sessionId, string line, string blurb, string summary)
    {
        emb.Pin($"{blurb}\n\n{line}", Near);
        emb.Pin(summary, Orthogonal);
        chat.EnqueueResponse(
            $$"""
            { "chunks": [ { "startLine": 0, "endLine": 0, "contextBlurb": {{JsonSerializer.Serialize(blurb)}} } ],
              "summary": {{JsonSerializer.Serialize(summary)}} }
            """);
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId,
            patientId,
            sessionId,
            sequenceNumber = 1,
            language = "en",
            transcript = line,
        });
        response.EnsureSuccessStatusCode();
        var ingestionId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ingestionId").GetGuid();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var status = (await client.GetFromJsonAsync<JsonElement>($"/ingestions/{ingestionId}"))
                .GetProperty("status").GetString()!;
            if (status == "Completed")
                return;
            Assert.NotEqual("Failed", status);
            await Task.Delay(50);
        }
        Assert.Fail($"Ingestion {ingestionId} never completed.");
    }
}
