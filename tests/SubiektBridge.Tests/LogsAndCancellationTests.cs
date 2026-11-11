using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Porządki po incydencie 2026-11-09 (v0.24.1):
/// - log produkcyjny miał 5-6 linii frameworka ASP.NET Core na KAŻDE żądanie (Request starting / Executing endpoint / Route matched /
///   Executing ObjectResult / Executed action / Executed endpoint / Request finished) obok jednej własnej linii podsumowania -
///   zdalny podgląd (`/admin/logs?tail=`) pokazywał przez to kilka razy mniej historii;
/// - rozłączenie klienta w listingu kończyło się `[ERR] ... failed` + 502, choć to nie awaria mostu ani Subiekta, więc kontrola
///   „zero błędów w logu" dawała fałszywe alarmy;
/// - lista (SQL od v0.21.6) zwracała NIP/nazwę kontrahenta po normalizacji (pusty NIP = null, nazwa po Trim), a pojedynczy dokument
///   (v0.21.7) wartości z kartoteki bez zmian - te same pola w dwóch endpointach różniły się zapisem.
/// </summary>
public class LogsAndCancellationTests
{
    // ----------------------------- Poziomy logowania -----------------------------

    private sealed class CollectingSink : ILogEventSink
    {
        public readonly List<LogEvent> Events = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Serilog.ILogger Logger, CollectingSink Sink) NewLogger()
    {
        var sink = new CollectingSink();
        return (BridgeLogging.ApplyLevels(new LoggerConfiguration()).WriteTo.Sink(sink).CreateLogger(), sink);
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics")]              // Request starting / finished
    [InlineData("Microsoft.AspNetCore.Routing.EndpointMiddleware")]       // Executing / Executed endpoint
    [InlineData("Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker")] // Route matched / Executed action
    [InlineData("Microsoft.AspNetCore.Mvc.Infrastructure.ObjectResultExecutor")]    // Executing ObjectResult
    public void FrameworkPerRequestInfo_IsDropped_ButWarningsStay(string source)
    {
        var (logger, sink) = NewLogger();
        var ctx = logger.ForContext(Constants.SourceContextPropertyName, source);

        ctx.Information("per-request noise");
        Assert.Empty(sink.Events);

        ctx.Warning("framework warning");
        ctx.Error("framework error");
        Assert.Equal(2, sink.Events.Count);
    }

    [Theory]
    [InlineData("Serilog.AspNetCore.RequestLoggingMiddleware")] // "HTTP GET ... responded 200 in X ms" - jedna linia na żądanie
    [InlineData("Microsoft.Hosting.Lifetime")]                  // Now listening on / Application started / shutting down
    [InlineData("SubiektBridge.Api.Sfera.RealSferaSession")]    // QueryInvoices(SQL), Opening Sfera session ...
    [InlineData("SubiektBridge.Api.Controllers.AdminController")]
    public void OwnAndLifetimeInfo_IsKept(string source)
    {
        var (logger, sink) = NewLogger();

        logger.ForContext(Constants.SourceContextPropertyName, source).Information("kept");

        Assert.Single(sink.Events);
    }

    // ----------------------------- Rozłączenie klienta to nie błąd -----------------------------

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static int StatusOf<T>(ActionResult<T> result) => result.Result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException($"Nieoczekiwany wynik: {result.Result?.GetType().Name}"),
    };

    private static CancellationToken Cancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    [Fact]
    public void ClientDisconnect_Result_Is499_LoggedAsInformation()
    {
        var logger = new CapturingLogger<BankOperationsController>();

        var result = ClientDisconnect.Result(logger, "QueryBankOperations");

        Assert.Equal(499, Assert.IsType<StatusCodeResult>(result).StatusCode);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("QueryBankOperations", entry.Message);
    }

    [Fact]
    public async Task BankOperations_ClientDisconnected_Returns499_WithoutErrorLog()
    {
        var logger = new CapturingLogger<BankOperationsController>();
        var c = new BankOperationsController(new FakeSferaSession(), logger);

        var result = await c.Query(null, null, null, false, 0, Cancelled());

        Assert.Equal(499, StatusOf(result));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task OpenReceivablesAndPayables_ClientDisconnected_Return499_WithoutErrorLog()
    {
        var logger = new CapturingLogger<InvoicesController>();
        var store = new IdempotencyStore(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_cancel_{Guid.NewGuid():N}.db"),
        }, NullLogger<IdempotencyStore>.Instance);
        var c = new InvoicesController(new FakeSferaSession(), store, logger);

        var receivables = await c.OpenReceivables(1m, 1000m, "PLN", null, null, null, 0, null, Cancelled());
        var payables = await c.OpenPayables(1m, 1000m, "PLN", null, null, null, 0, null, Cancelled());

        Assert.Equal(499, StatusOf(receivables));
        Assert.Equal(499, StatusOf(payables));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task BankOperations_RealFailure_StillReturns502_AndLogsError()
    {
        // Wyjątek, który NIE jest anulowaniem wołającego, zostaje błędem (502 SUBIEKT_QUERY_FAILED + ERR).
        var logger = new CapturingLogger<BankOperationsController>();
        var c = new BankOperationsController(new FakeSferaSession { BankOperationsFailureForTests = new InvalidOperationException("COM") }, logger);

        var result = await c.Query(null, null, null, false, 0, CancellationToken.None);

        Assert.Equal(502, StatusOf(result));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    // ----------------------------- Lista = pojedynczy dokument (NIP i nazwa bez normalizacji) -----------------------------

    private static Dictionary<string, object?> Row(object? nip, object? nazwa) => new()
    {
        ["dok_Id"] = 1, ["dok_NrPelny"] = "FS 1/2026", ["dok_DataWyst"] = new DateTime(2026, 1, 12), ["dok_PlatnikId"] = 77,
        ["dok_Uwagi"] = null, ["dok_WartNetto"] = 1m, ["dok_WartVat"] = 0m, ["dok_WartBrutto"] = 1m, ["dok_MagId"] = 1,
        ["adr_NIP"] = nip, ["adr_Nazwa"] = nazwa,
    };

    [Fact]
    public void ListRow_ContractorWithoutNip_KeepsEmptyString_LikeSingleDocument()
    {
        var fromList = InvoiceListingSql.Map(Row(nip: "", nazwa: " Jan Kowalski "));
        var fromDetail = ContractorNipName.Map(new Dictionary<string, object?> { ["adr_NIP"] = "", ["adr_Nazwa"] = " Jan Kowalski " });

        Assert.Equal("", fromList.ContractorNip);
        Assert.Equal(" Jan Kowalski ", fromList.ContractorName);
        Assert.Equal(fromDetail.Nip, fromList.ContractorNip);
        Assert.Equal(fromDetail.Name, fromList.ContractorName);
    }

    [Fact]
    public void ListRow_NoContractorRecord_GivesNulls_LikeSingleDocument()
    {
        // LEFT JOIN bez wiersza kartoteki (dok_PlatnikId bez adr__Ewid) = DBNull w obu kolumnach.
        var fromList = InvoiceListingSql.Map(Row(nip: DBNull.Value, nazwa: DBNull.Value));

        Assert.Null(fromList.ContractorNip);
        Assert.Null(fromList.ContractorName);
    }
}
