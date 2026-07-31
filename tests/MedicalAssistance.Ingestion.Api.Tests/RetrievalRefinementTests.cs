using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Retrieval;
using MedicalAssistance.Ingestion.Api.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The query-refinement step (T44): config-gated and fail-open. When enabled it
/// rewrites the question into a cleaner search query using conversation context,
/// changing only the query vector; when disabled, or when refinement fails, the raw
/// question is used unchanged. The rewrite is driven by the DB-seeded QueryRefinement
/// agent (ADR-0008) and never touches the answer's grounding.
///
/// Each test places two chunks on orthogonal axes — one matching the raw question,
/// one matching the refined query — so which chunk leads the citations reveals which
/// query actually drove retrieval.
/// </summary>
public class RetrievalRefinementTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    private static readonly float[] MatchesRefined = [1f, 0f, 0f];
    private static readonly float[] MatchesRaw = [0f, 1f, 0f];
    private static readonly float[] Summary = [0f, 0f, 1f]; // orthogonal to both queries

    private const string InsulinLine = "The patient reports taking insulin injections every single day for their diabetes.";
    private const string PressureLine = "Blood pressure this morning measured one twenty over eighty which is entirely normal.";

    // Pronoun-heavy raw question (matches the pressure chunk); the refined form
    // (matches the insulin chunk) is what a good rewrite would produce.
    private const string RawQuestion = "And what about that other reading?";
    private const string RefinedQuery = "What was the patient's blood sugar and insulin?";

    [Fact]
    public async Task Disabled_by_default_retrieval_uses_the_raw_question()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb);
        var client = factory.CreateClient();

        emb.Pin(RawQuestion, MatchesRaw);
        emb.Pin(RefinedQuery, MatchesRefined);
        await SeedTwoChunksAsync(client, chat, emb, "refine-off");

        chat.EnqueueResponse("Answer over the record [E1].");
        var body = await AnswerAsync(client, "refine-off", RawQuestion);

        // No refinement ran: the raw question drove retrieval, so the raw-matching
        // pressure chunk leads.
        Assert.Equal(PressureLine, TopQuote(body));
        var refineInstructions = await SeededInstructionsAsync("QueryRefinement");
        Assert.DoesNotContain(chat.ReceivedPrompts, p => p.Contains(refineInstructions));
    }

    [Fact]
    public async Task Enabled_retrieval_uses_the_refined_query_built_from_the_db_prompt_and_context()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb)
            .WithWebHostBuilder(b => b.UseSetting(RefineRetrievalStep.EnabledConfigurationKey, "true"));
        var client = factory.CreateClient();

        emb.Pin(RawQuestion, MatchesRaw);
        emb.Pin(RefinedQuery, MatchesRefined);
        await SeedTwoChunksAsync(client, chat, emb, "refine-on");

        const string priorSummary = "Earlier the doctor asked about the patient's blood sugar and insulin.";
        // The refine agent replies with the refined query; then the answer agent replies.
        chat.EnqueueResponse(RefinedQuery);
        chat.EnqueueResponse("Answer over the record [E1].");

        var body = await AnswerAsync(client, "refine-on", RawQuestion, priorSummary,
            recentTurns: [new { role = "user", text = "Tell me about the blood sugar." }]);

        // The refined query drove retrieval, so the refined-matching insulin chunk leads.
        Assert.Equal(InsulinLine, TopQuote(body));

        // And the refine agent was built from the DB prompt and saw the raw question + context.
        var refineInstructions = await SeededInstructionsAsync("QueryRefinement");
        var refinePrompt = chat.ReceivedPrompts.Single(p => p.Contains(refineInstructions));
        Assert.Contains(RawQuestion, refinePrompt);
        Assert.Contains(priorSummary, refinePrompt);
    }

    [Fact]
    public async Task A_refinement_failure_falls_open_to_the_raw_question()
    {
        var chat = new ScriptedChatClient();
        var emb = new ControllableEmbeddingGenerator(IngestionApiFixture.EmbeddingDimensions);
        using var factory = fixture.CreateFactory(chat, embeddingGenerator: emb)
            .WithWebHostBuilder(b => b.UseSetting(RefineRetrievalStep.EnabledConfigurationKey, "true"));
        var client = factory.CreateClient();

        emb.Pin(RawQuestion, MatchesRaw);
        emb.Pin(RefinedQuery, MatchesRefined);
        await SeedTwoChunksAsync(client, chat, emb, "refine-fault");

        // Refinement throws; the answer agent still replies.
        chat.EnqueueFault();
        chat.EnqueueResponse("Answer over the record [E1].");

        var body = await AnswerAsync(client, "refine-fault", RawQuestion);

        // Failure must not fail the turn: the raw question is used, so the pressure
        // chunk leads and an answer is still returned.
        Assert.Equal(PressureLine, TopQuote(body));
        Assert.Equal("Answer over the record [E1].", body.GetProperty("answer").GetString());
    }

    private async Task SeedTwoChunksAsync(
        HttpClient client, ScriptedChatClient chat, ControllableEmbeddingGenerator emb, string patientId)
    {
        await IngestTranscriptAsync(client, chat, emb, patientId, "s-insulin", InsulinLine,
            "Diabetes.", "Insulin summary.", MatchesRefined);
        await IngestTranscriptAsync(client, chat, emb, patientId, "s-pressure", PressureLine,
            "Vitals.", "Pressure summary.", MatchesRaw);
    }

    private static string TopQuote(JsonElement body) =>
        body.GetProperty("citations").EnumerateArray().First().GetProperty("quote").GetString()!;

    private static async Task<JsonElement> AnswerAsync(
        HttpClient client, string patientId, string question, string? priorSummary = null, object[]? recentTurns = null)
    {
        var response = await client.PostAsJsonAsync($"/patients/{patientId}/chat/answer", new
        {
            doctorId = "dr-a",
            question,
            priorSummary,
            recentTurns,
        });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
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
        string patientId, string sessionId, string line, string blurb, string summary, float[] bodyVector)
    {
        emb.Pin($"{blurb}\n\n{line}", bodyVector);
        emb.Pin(summary, Summary);
        chat.EnqueueResponse(
            $$"""
            { "chunks": [ { "startLine": 0, "endLine": 0, "contextBlurb": {{JsonSerializer.Serialize(blurb)}} } ],
              "summary": {{JsonSerializer.Serialize(summary)}} }
            """);
        var response = await client.PostAsJsonAsync("/ingestions", new
        {
            documentType = "SessionTranscript",
            doctorId = "dr-a",
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
