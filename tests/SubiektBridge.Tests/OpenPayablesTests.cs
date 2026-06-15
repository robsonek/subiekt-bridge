using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Otwarte zobowiazania (FZ) = kandydaci do dopasowania z WYPLATA. Lustro open-receivables (nzf_Typ=40
/// zamiast 39). Testy przeciw FakeSferaSession (logika filtra dzielona przez FilterOpenSettlements) +
/// InvoicesController (mapowanie HTTP). Real (COM, windows-only) nietestowany na CI.
/// </summary>
public class OpenPayablesTests
{
    private static OpenReceivablesQueryRequestDto Req(
        decimal? min = null, decimal? max = null, long? contractor = null,
        string? from = null, string? to = null, int limit = 50, string? search = null)
        => new(min, max, "PLN", contractor, from, to, limit, search);

    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_pay_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static InvoicesController NewController(FakeSferaSession fake)
        => new(fake, NewStore(), NullLogger<InvoicesController>.Instance);

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

    // ----------------------------- FakeSfera logic -----------------------------

    [Fact]
    public async Task Payables_ReturnsOnlyFzRows_InAmountWindow()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenPayablesAsync(Req(min: 100m, max: 5000m), CancellationToken.None);
        Assert.NotEmpty(r);
        Assert.All(r, x => Assert.Equal("FZ", x.DocType));
        Assert.All(r, x => Assert.InRange(x.Remaining, 100m, 5000m));
    }

    [Fact]
    public async Task Payables_FiltersByContractor()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenPayablesAsync(Req(contractor: 5001), CancellationToken.None);
        Assert.NotEmpty(r);
        Assert.All(r, x => Assert.Equal(5001, x.ContractorId));
    }

    [Fact]
    public async Task Payables_ExcludesNonPln()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenPayablesAsync(Req(), CancellationToken.None);
        Assert.All(r, x => Assert.Equal("PLN", x.Currency));
    }

    // ----------------------------- Controller (HTTP) -----------------------------

    [Fact]
    public async Task Controller_Returns200_WithFzList()
    {
        var controller = NewController(new FakeSferaSession());
        var r = await controller.OpenPayables(
            minAmount: null, maxAmount: null, currency: null, contractorId: null,
            from: null, to: null, limit: 0, search: null, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        var list = Assert.IsAssignableFrom<IReadOnlyList<OpenReceivableDto>>(value);
        Assert.NotEmpty(list);
        Assert.All(list, x => Assert.Equal("FZ", x.DocType));   // open-payables -> wylacznie zobowiazania FZ
        Assert.All(list, x => Assert.Equal("PLN", x.Currency)); // brak currency w query -> default PLN
    }

    [Fact]
    public async Task Controller_CurrencyNotPln_Returns422()
    {
        // Most rozlicza tylko PLN; waluta obca odrzucona (spojnie z open-receivables i guardem PLN w /settlements).
        var controller = NewController(new FakeSferaSession());
        var r = await controller.OpenPayables(
            minAmount: null, maxAmount: null, currency: "EUR", contractorId: null,
            from: null, to: null, limit: 0, search: null, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(422, status);
        Assert.Equal("UNSUPPORTED_CURRENCY", ((ErrorResponseDto)value!).Code);
    }
}
