using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Most = czysty passthrough surowych przelewów (hb_Transakcja). Bez matchingu, bez interpretacji -
/// dopasowanie i decyzje robi Laravel. Testy przeciw FakeSferaSession + kontrolerowi (HTTP).
/// Real (read-only SQL) testowalny tylko na prod DB.
/// </summary>
public class BankReconciliationTests
{
    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, null),
    };

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
    public async Task BankTransactions_RawPassthrough_NoMatching()
    {
        var fake = new FakeSferaSession();
        var all = await fake.QueryBankTransactionsAsync(new BankTransactionQueryRequestDto(null, UnbookedOnly: false), CancellationToken.None);

        // Niezaksięgowana: bank_operation_subiekt_id == null; zaksięgowana niesie nzf_Id (gotowe do /settlements).
        var unbooked = all.Single(t => t.HbId == 13127);
        Assert.False(unbooked.Booked);
        Assert.Null(unbooked.BankOperationSubiektId);

        var booked = all.Single(t => t.HbId == 12001);
        Assert.True(booked.Booked);
        Assert.Equal(88001, booked.BankOperationSubiektId);

        // Surowe pola kontrahenta są przekazywane jak są (most NIE rozpoznaje kontrahenta po rachunku).
        Assert.Equal("Jan Szyszka", unbooked.ContractorName);
        Assert.Equal("PL27114020040000300201355387", unbooked.ContractorAccount);

        // rachunek_id/numer = konto wyciągu (do późniejszego /book na właściwym koncie) - surowa dana.
        Assert.Equal(1, unbooked.RachunekId);
        Assert.NotNull(unbooked.RachunekNumer);
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
}
