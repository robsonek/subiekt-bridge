using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// Constant-time porównanie token-a w bytes (UTF-8). Używa <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// na buforze padded do max(provided, expected). Length-based early-return byłby
    /// timing oracle ujawniający długość tokena (atakujący mógłby bruteforce'ować długość
    /// bardziej deterministycznie).
    /// </summary>
    private static bool CryptographicEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        var maxLen = Math.Max(bytesA.Length, bytesB.Length);

        // Pad obu do tej samej długości - FixedTimeEquals wymaga równych span'ów.
        Span<byte> paddedA = stackalloc byte[maxLen];
        Span<byte> paddedB = stackalloc byte[maxLen];
        bytesA.CopyTo(paddedA);
        bytesB.CopyTo(paddedB);

        var equal = CryptographicOperations.FixedTimeEquals(paddedA, paddedB);
        // Length mismatch musi też dać false - po padding-u, dłuższy token miał dane na pozycjach
        // które krótszy ma jako zera. Jeśli a.Length != b.Length, FixedTimeEquals zwróci false
        // (chyba że przypadkowo padding zerami zgadza się z dalszą częścią dłuższego - niemożliwe
        // dla "rzeczywistych" tokenów ASCII/Base64 nie kończących się \0).
        return equal && bytesA.Length == bytesB.Length;
    }
}
