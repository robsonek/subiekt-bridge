using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SubiektBridge.Api.Configuration;

namespace SubiektBridge.Api.Auth;

/// <summary>
/// Statyczna autoryzacja po nagłówku X-Bridge-Token. Brak rotacji - klient (Laravel)
/// trzyma ten sam token w env. IP whitelist robimy na Windows Firewall.
/// </summary>
public sealed class BridgeTokenAuthOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "BridgeToken";
}

public sealed class BridgeTokenAuthHandler : AuthenticationHandler<BridgeTokenAuthOptions>
{
    private readonly BridgeOptions _bridge;

    public BridgeTokenAuthHandler(
        IOptionsMonitor<BridgeTokenAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<BridgeOptions> bridgeOptions)
        : base(options, logger, encoder)
    {
        _bridge = bridgeOptions.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Health endpoint jest publiczny (LB/monitoring) - autoryzowany przez whitelistę
        // ścieżek w Program.cs, ale gdyby trafił tu, akceptujemy bez tokenu.
        if (Request.Path.StartsWithSegments("/api/v1/health"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!Request.Headers.TryGetValue("X-Bridge-Token", out var headerValue))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Bridge-Token header"));
        }

        var provided = headerValue.ToString();
        var expected = _bridge.Token;

        if (string.IsNullOrEmpty(expected))
        {
            Logger.LogError("Bridge token nie skonfigurowany (Bridge:Token jest pusty).");
            return Task.FromResult(AuthenticateResult.Fail("Bridge token nie skonfigurowany"));
        }

        if (!CryptographicEquals(provided, expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid X-Bridge-Token"));
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "marketplace-manage"),
        }, BridgeTokenAuthOptions.Scheme);

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            BridgeTokenAuthOptions.Scheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>Constant-time string compare żeby uniknąć timing attacks na token.</summary>
    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }
}
