using Serilog;
using Serilog.Events;

namespace SubiektBridge.Api.Configuration;

/// <summary>
/// Poziomy logowania mostu. Framework ASP.NET Core loguje na poziomie Information 5-6 linii na KAŻDE żądanie (Request starting,
/// Executing endpoint, Route matched, Executing ObjectResult, Executed action/endpoint, Request finished), a most ma już własną,
/// jedną linię podsumowania z UseSerilogRequestLogging ("HTTP GET ... responded 200 in X ms (from ..., UA: ...)"). Sekcja
/// "Logging:LogLevel" z appsettings NIE działa na Serilog, więc szablon produkcyjny z "Microsoft.AspNetCore": "Warning" niczego
/// nie wyciszał. Od v0.24.1 Information z Microsoft.AspNetCore.* jest odcinane w kodzie; ostrzeżenia i błędy frameworka zostają,
/// tak samo Microsoft.Hosting.Lifetime (Now listening / Application started / shutting down) i wszystkie logi mostu.
/// Wołane PRZED ReadFrom.Configuration - sekcja "Serilog" w appsettings może to nadpisać (np. na czas diagnozy).
/// </summary>
public static class BridgeLogging
{
    public static LoggerConfiguration ApplyLevels(LoggerConfiguration cfg)
        => cfg.MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning);
}
