using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Serilog;
using SubiektBridge.Api.Auth;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Sfera;

var builder = WebApplication.CreateBuilder(args);

// Native Windows Service integration. No-op gdy proces NIE jest uruchomiony jako serwis
// (np. dotnet run lokalnie) - pozwala na ten sam binarka działa interaktywnie i jako serwis.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "SubiektBridge";
});

// Auto-generate self-signed cert jeśli config wskazuje plik PFX który nie istnieje.
// Eliminuje kłopot "Unable to configure HTTPS endpoint - dev certificate missing"
// na świeżym Windowsie/Server 2016+ bez zainstalowanego dotnet dev-certs.
EnsureSelfSignedCertificate(builder.Configuration);

// Logi do absolute path obok exe - Windows Service ma WorkingDirectory=C:\Windows\System32,
// wiec relative "logs/" trafialo poza C:\SubiektBridge\ i folder logs/ byl pusty.
var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "subiekt-bridge-.log");
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console()
    .WriteTo.File(
        path: logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30));

// Konfiguracja
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));
builder.Services.Configure<SubiektOptions>(builder.Configuration.GetSection(SubiektOptions.SectionName));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BridgeOptions>>().Value);
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SubiektOptions>>().Value);

// Sfera session - Fake na dev (macOS/Linux), Real na Windows w produkcji.
builder.Services.AddSingleton<ISferaSession>(sp =>
{
    var options = sp.GetRequiredService<BridgeOptions>();
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SferaSelector");

    if (options.UseFakeSfera)
    {
        logger.LogWarning("Using FakeSferaSession (Bridge:UseFakeSfera = true). Tylko dev/testy!");
        return new FakeSferaSession();
    }

    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        throw new PlatformNotSupportedException(
            "RealSferaSession (COM/InsERT.GT) działa wyłącznie na Windowsie. " +
            "Na macOS/Linux ustaw Bridge:UseFakeSfera = true.");
    }

    var subiektOptions = sp.GetRequiredService<SubiektOptions>();
    var realLogger = sp.GetRequiredService<ILogger<RealSferaSession>>();
    return new RealSferaSession(subiektOptions, realLogger);
});

builder.Services.AddSingleton<IdempotencyStore>();

// Autoryzacja po X-Bridge-Token.
builder.Services.AddAuthentication(BridgeTokenAuthOptions.Scheme)
    .AddScheme<BridgeTokenAuthOptions, BridgeTokenAuthHandler>(
        BridgeTokenAuthOptions.Scheme, _ => { });

builder.Services.AddAuthorization();

builder.Services.AddControllers();

var app = builder.Build();

// Dorzucamy RemoteIp + UserAgent do każdego "Request finished" wpisu - bez tego
// nie wiadomo kto poluje endpointy (np. /api/v1/health w pętli). Domyślny template
// Serilog.AspNetCore loguje tylko metodę/path/status/elapsed.
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms (from {RemoteIp}, UA: {UserAgent})";

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        // RemoteIpAddress może być null krótkotrwale (np. przy connection teardown).
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "-";

        // X-Forwarded-For honor — gdyby Bridge stał za reverse-proxy.
        // Domyślnie Bridge słucha bezpośrednio na :988, ale klient mógł postawić nginx/Caddy.
        if (httpContext.Request.Headers.TryGetValue("X-Forwarded-For", out var xff)
            && !Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(xff))
        {
            remoteIp = xff.ToString();
        }

        diagnosticContext.Set("RemoteIp", remoteIp);

        var ua = httpContext.Request.Headers.UserAgent.ToString();
        diagnosticContext.Set("UserAgent", string.IsNullOrEmpty(ua) ? "-" : ua);
    };
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Logger.LogInformation(
    "SubiektBridge starting. Platform: {Platform}, FakeSfera: {Fake}",
    RuntimeInformation.OSDescription,
    app.Services.GetRequiredService<BridgeOptions>().UseFakeSfera);

app.Run();

// ============================================================================
// Helpers
// ============================================================================

static void EnsureSelfSignedCertificate(IConfiguration config)
{
    // Czytamy ścieżkę z standardowego klucza ASP.NET Core: Kestrel:Endpoints:Https:Certificate:Path
    var certPath = config["Kestrel:Endpoints:Https:Certificate:Path"];
    if (string.IsNullOrEmpty(certPath))
    {
        return; // Config używa innego endpointu (HTTP) lub innego mechanizmu cert (KeyVault, etc.)
    }

    var fullPath = Path.IsPathRooted(certPath)
        ? certPath
        : Path.Combine(Directory.GetCurrentDirectory(), certPath);

    if (File.Exists(fullPath))
    {
        return; // Cert już istnieje (poprzedni start albo ręcznie skonfigurowany)
    }

    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var password = config["Kestrel:Endpoints:Https:Certificate:Password"] ?? string.Empty;
    var hostname = Environment.MachineName;

    using var rsa = RSA.Create(2048);
    var dn = new X500DistinguishedName($"CN=SubiektBridge-{hostname}");
    var req = new CertificateRequest(dn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    // Subject Alternative Names: hostname + localhost + 127.0.0.1
    // Bez SAN nowoczesne klienty (curl, Chromium) odrzucają cert z SUBJECT_ALT_NAME_INVALID.
    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddDnsName(hostname);
    sanBuilder.AddDnsName("localhost");
    sanBuilder.AddIpAddress(IPAddress.Loopback);
    sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
    req.CertificateExtensions.Add(sanBuilder.Build());

    // Server Authentication EKU - cert nadaje się do TLS server side.
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: true));

    using var cert = req.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddDays(-1),  // valid od wczoraj (clock skew)
        DateTimeOffset.UtcNow.AddYears(5));

    File.WriteAllBytes(fullPath, cert.Export(X509ContentType.Pfx, password));

    Console.WriteLine($"Wygenerowano self-signed cert: {fullPath} (CN=SubiektBridge-{hostname}, ważny 5 lat)");
}
