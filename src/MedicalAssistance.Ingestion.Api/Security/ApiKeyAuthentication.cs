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
    private readonly byte[][] _validKeyHashes;

    /// <summary>Creates the handler with the secrets currently configured.</summary>
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        // Configuration is read per request rather than once, which is what lets
        // a rotated key take effect without a restart.
        _validKeyHashes = (configuration.GetSection(ApiKeyAuthentication.KeysConfigurationPath).Get<string[]>() ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .ToArray();
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthentication.HeaderName, out var presented))
            return Task.FromResult(AuthenticateResult.Fail($"Missing {ApiKeyAuthentication.HeaderName} header."));

        // Digests, not the keys themselves. FixedTimeEquals is only fixed-time
        // for operands of equal length — it returns immediately when they differ
        // — so comparing raw keys would let the time taken reveal how long the
        // real secret is. Two SHA-256 hashes are always 32 bytes, so there is
        // nothing left for the timing to expose.
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented.ToString()));

        // Every key is checked even once one has matched, for the same reason.
        var matched = false;
        foreach (var hash in _validKeyHashes)
            matched |= CryptographicOperations.FixedTimeEquals(presentedHash, hash);

        if (!matched)
            return Task.FromResult(AuthenticateResult.Fail("The presented API key is not valid."));

        var identity = new ClaimsIdentity(ApiKeyAuthentication.SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
