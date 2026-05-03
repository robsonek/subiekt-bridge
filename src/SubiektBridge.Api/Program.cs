using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Authentication;
using Serilog;
using SubiektBridge.Api.Auth;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Sfera;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console()
    .WriteTo.File(
        path: "logs/subiekt-bridge-.log",
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

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Logger.LogInformation(
    "SubiektBridge starting. Platform: {Platform}, FakeSfera: {Fake}",
    RuntimeInformation.OSDescription,
    app.Services.GetRequiredService<BridgeOptions>().UseFakeSfera);

app.Run();
