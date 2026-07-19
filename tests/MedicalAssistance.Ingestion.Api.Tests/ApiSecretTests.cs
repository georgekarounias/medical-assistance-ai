using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedicalAssistance.Ingestion.Api.Security;

namespace MedicalAssistance.Ingestion.Api.Tests;

/// <summary>
/// The shared-secret gate (ADR-0007). Patient data sits behind this one header,
/// so every endpoint has to demand it — and two secrets are valid at once, which
/// is what lets the backend rotate keys without a coordinated deployment.
/// </summary>
public class ApiSecretTests(IngestionApiFixture fixture) : IClassFixture<IngestionApiFixture>
{
    [Fact]
    public async Task Every_endpoint_refuses_a_caller_with_no_secret()
    {
        var client = CreateClientWith(apiKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/ingestions", Payload())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/ingestions/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync($"/ingestions/{Guid.NewGuid()}/retry", null)).StatusCode);
    }

    [Fact]
    public async Task A_caller_with_the_wrong_secret_is_refused()
    {
        var client = CreateClientWith("not-the-secret");

        var response = await client.PostAsJsonAsync("/ingestions", Payload());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_rejected_submission_never_reaches_the_pipeline()
    {
        var promptsBefore = fixture.ChatClient.ReceivedPrompts.Count;
        var client = CreateClientWith("not-the-secret");

        await client.PostAsJsonAsync("/ingestions", Payload());

        // Authentication runs before anything durable happens: an unauthorised
        // upload leaves no ingestion behind and spends nothing.
        await Task.Delay(300);
        Assert.Equal(promptsBefore, fixture.ChatClient.ReceivedPrompts.Count);
    }

    [Theory]
    [InlineData(IngestionApiFixture.ApiKey)]
    [InlineData(IngestionApiFixture.RotationApiKey)]
    public async Task Both_configured_secrets_are_accepted_so_rotation_needs_no_downtime(string apiKey)
    {
        var client = CreateClientWith(apiKey);

        // 404 rather than 401: the caller got past the gate and the id is simply
        // unknown, which is what "this key works" looks like from outside.
        var response = await client.GetAsync($"/ingestions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("t")]
    [InlineData("test-api-key-primar")]                    // one character short
    [InlineData("test-api-key-primaryy")]                  // one character long
    [InlineData("test-api-key-primary-with-more-after")]   // a valid key as a prefix
    [InlineData("XXXX-XXX-XXX-XXXXXXX")]                   // right length, wrong content
    public async Task A_secret_of_any_length_is_refused_unless_it_matches_exactly(string apiKey)
    {
        // Lengths either side of a real key, and one that contains a real key.
        // The comparison works on SHA-256 digests, so every candidate is 32 bytes
        // by the time it is checked and how long the answer takes cannot narrow
        // down the length of the secret. That property is structural and not
        // something a timing assertion could test without being flaky, so what
        // is asserted here is the behaviour it must not have broken.
        var client = CreateClientWith(apiKey);

        var response = await client.GetAsync($"/ingestions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_published_contract_tells_the_caller_which_header_to_send()
    {
        var client = fixture.Factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");

        var scheme = document.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty(ApiKeyAuthentication.SchemeName);
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal(ApiKeyAuthentication.HeaderName, scheme.GetProperty("name").GetString());
    }

    private HttpClient CreateClientWith(string? apiKey)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ApiKeyAuthentication.HeaderName);
        if (apiKey is not null)
            client.DefaultRequestHeaders.Add(ApiKeyAuthentication.HeaderName, apiKey);
        return client;
    }

    private static object Payload() => new
    {
        documentType = "SessionTranscript",
        doctorId = "doc-1",
        patientId = "pat-unauthorised",
        sessionId = "sess-unauthorised",
        sequenceNumber = 1,
        language = "en",
        transcript = "Doctor: Good morning.\nPatient: I have headaches most mornings lately.",
    };
}
