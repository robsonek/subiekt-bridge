using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Most wystawia DANE (surowe przelewy + otwarte rozrachunki), matching robi Laravel.
/// Testy przeciw FakeSferaSession + kontrolerom (HTTP). Real (read-only SQL) testowalny tylko na prod DB.
/// </summary>
public class BankReconciliationTests
{
    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, null),
    };

    // ---------- bank-transactions (surowe przelewy) ----------

    [Fact]
    public async Task BankTransactions_UnbookedOnly_ExcludesBooked()
    {
        var fake = new FakeSferaSession();
        var all = await fake.QueryBankTransactionsAsync(new BankTransactionQueryRequestDto(null, UnbookedOnly: false), CancellationToken.None);
        var unbooked = await fake.QueryBankTransactionsAsync(new BankTransactionQueryRequestDto(null, UnbookedOnly: true), CancellationToken.None);

        Assert.Contains(all, t => t.Booked);
        Assert.All(unbooked, t => Assert.False(t.Booked));
        Assert.True(unbooked.Count < all.Count);
    }

    [Fact]
    public async Task BankTransactions_DirectionIn_OnlyIncoming()
    {
        var fake = new FakeSferaSession();
        var inOnly = await fake.QueryBankTransactionsAsync(new BankTransactionQueryRequestDto("in", UnbookedOnly: false), CancellationToken.None);
        Assert.NotEmpty(inOnly);
        Assert.All(inOnly, t => Assert.Equal("in", t.Direction));
    }

    [Fact]
    public async Task BankTransactions_ResolvesContractorIdAsData()
    {
        var fake = new FakeSferaSession();
        var items = await fake.QueryBankTransactionsAsync(new BankTransactionQueryRequestDto("in"), CancellationToken.None);
        // 13109 (Google) ma rozpoznany rachunek -> contractor_id; 13127 (Szyszka) nie -> null. To DANA, nie decyzja.
        Assert.Equal(623, items.Single(t => t.HbId == 13109).ContractorId);
        Assert.Null(items.Single(t => t.HbId == 13127).ContractorId);
    }

    [Fact]
    public async Task Controller_BankTransactions_Returns200()
    {
        var controller = new BankTransactionsController(new FakeSferaSession(), NullLogger<BankTransactionsController>.Instance);
        var r = await controller.Query(direction: "in", unbookedOnly: true, from: null, to: null, limit: 0, CancellationToken.None);
        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<BankTransactionDto>>(value));
    }

    // ---------- open-receivables (otwarte rozrachunki) ----------

    [Fact]
    public async Task OpenReceivables_DirectionIn_OnlyReceivables()
    {
        var fake = new FakeSferaSession();
        var recv = await fake.QueryOpenReceivablesAsync(new OpenReceivableQueryRequestDto("in", null, null), CancellationToken.None);
        Assert.NotEmpty(recv);
        Assert.All(recv, o => Assert.Equal("receivable", o.Kind));
    }

    [Fact]
    public async Task OpenReceivables_DirectionOut_OnlyLiabilities()
    {
        var fake = new FakeSferaSession();
        var liab = await fake.QueryOpenReceivablesAsync(new OpenReceivableQueryRequestDto("out", null, null), CancellationToken.None);
        Assert.NotEmpty(liab);
        Assert.All(liab, o => Assert.Equal("liability", o.Kind));
    }

    [Fact]
    public async Task OpenReceivables_AmountFilter_KeepsAmbiguityAsData()
    {
        var fake = new FakeSferaSession();
        // Szyszka: dwie faktury po 3372,50 -> most ZWRACA OBIE (dane); dwuznaczność rozstrzyga Laravel.
        var matches = await fake.QueryOpenReceivablesAsync(new OpenReceivableQueryRequestDto("in", null, 3372.50m), CancellationToken.None);
        Assert.Equal(2, matches.Count);
        Assert.All(matches, o => Assert.Equal(3372.50m, o.Remaining));
    }

    [Fact]
    public async Task OpenReceivables_ContractorFilter_Narrows()
    {
        var fake = new FakeSferaSession();
        var forKh = await fake.QueryOpenReceivablesAsync(new OpenReceivableQueryRequestDto(null, ContractorId: 623, null), CancellationToken.None);
        Assert.NotEmpty(forKh);
        Assert.All(forKh, o => Assert.Equal(623, o.ContractorId));
    }

    [Fact]
    public async Task Controller_OpenReceivables_Returns200()
    {
        var controller = new OpenReceivablesController(new FakeSferaSession(), NullLogger<OpenReceivablesController>.Instance);
        var r = await controller.Query(direction: null, contractorId: null, amount: null, limit: 0, CancellationToken.None);
        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<OpenReceivableDto>>(value));
    }
}
