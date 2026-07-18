using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MedicalAssistance.Ingestion.Api.Security;

/// <summary>
/// The shared-secret scheme (ADR-0007). This service has exactly one caller —
/// the existing backend — so a header check is the whole of authentication here.
/// Authorization (which doctor may see or remove what) belongs to the backend,
/// which this service trusts; the payload carries the identifiers explicitly.
/// </summary>
public static class ApiKeyAuthentication
{
    /// <summary>Name of the authentication scheme.</summary>
    public const string SchemeName = "ApiKey";

    /// <summary>The request header carrying the shared secret.</summary>
    public const string HeaderName = "X-Api-Key";

    /// <summary>Configuration section holding the currently valid secrets.</summary>
    public const string KeysConfigurationPath = "Authentication:ApiKeys";
}

/// <summary>
/// Checks the shared secret on every request.
///
/// Several keys are valid at once, which is what makes rotation zero-downtime:
/// publish the new key alongside the old, move the backend across, then retire
/// the old one — no coordinated deployment, no window where calls are refused.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly byte[][] _validKeys;

    /// <summary>Creates the handler with the secrets currently configured.</summary>
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _validKeys = (configuration.GetSection(ApiKeyAuthentication.KeysConfigurationPath).Get<string[]>() ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(Encoding.UTF8.GetBytes)
            .ToArray();
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthentication.HeaderName, out var presented))
            return Task.FromResult(AuthenticateResult.Fail($"Missing {ApiKeyAuthentication.HeaderName} header."));

        var presentedBytes = Encoding.UTF8.GetBytes(presented.ToString());

        // Compared in fixed time, and every key is checked even once one has
        // matched: how long the answer takes must not narrow down the secret.
        var matched = false;
        foreach (var key in _validKeys)
            matched |= CryptographicOperations.FixedTimeEquals(presentedBytes, key);

        if (!matched)
            return Task.FromResult(AuthenticateResult.Fail("The presented API key is not valid."));

        var identity = new ClaimsIdentity(ApiKeyAuthentication.SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
