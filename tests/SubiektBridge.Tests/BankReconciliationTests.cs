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
    public BankReconciliationTests() => FakeSferaSession.ResetBankBookingForTests();

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        _ => (-1, null),
    };

    private static BankTransactionsController NewController(FakeSferaSession fake)
        => new(fake, NullLogger<BankTransactionsController>.Instance);

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
        var controller = NewController(new FakeSferaSession());
        var r = await controller.Query(direction: "in", unbookedOnly: true, from: null, to: null, limit: 0, CancellationToken.None);
        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<BankTransactionDto>>(value));
    }

    // ---------- POST /bank-transactions/{hb_id}/book ----------

    [Fact]
    public async Task Book_FirstCall_Creates_SecondCall_AlreadyBooked()
    {
        var fake = new FakeSferaSession();
        var first = await fake.BookBankTransactionAsync(13128, null, keepUnlinked: false, CancellationToken.None);
        var second = await fake.BookBankTransactionAsync(13128, null, keepUnlinked: false, CancellationToken.None);

        Assert.True(first.Linked);
        Assert.False(first.AlreadyBooked);
        Assert.NotNull(first.BankOperationSubiektId);

        Assert.True(second.AlreadyBooked);
        Assert.Equal(first.BankOperationSubiektId, second.BankOperationSubiektId); // ten sam BP, brak duplikatu
    }

    [Fact]
    public async Task Book_TransactionNotFound_Throws()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<BankBookingException>(() => fake.BookBankTransactionAsync(-1, null, keepUnlinked: false, CancellationToken.None));
        Assert.Equal(BookError.TransactionNotFound, ex.Reason);
    }

    [Fact]
    public void Controller_Book_Returns501_NotSupported()
    {
        // Ksiegowanie HB przez Sfere niemozliwe (potwierdzone) - endpoint to 501-stub.
        var controller = NewController(new FakeSferaSession());
        var r = controller.Book(13128, new BookRequestDto());
        var (status, value) = Unwrap(r);
        Assert.Equal(501, status);
        Assert.Equal("HB_BOOKING_NOT_SUPPORTED", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Book_BranchB_NotLinked_NoOrphan()
    {
        var fake = new FakeSferaSession();
        var r = await fake.BookBankTransactionAsync(88_888, null, keepUnlinked: false, CancellationToken.None);
        Assert.False(r.Linked);
        Assert.False(r.AlreadyBooked);
        Assert.Null(r.BankOperationSubiektId); // BP cofniety - brak orphana
    }

    [Fact]
    public async Task Book_NoAccount_Throws()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<BankBookingException>(() => fake.BookBankTransactionAsync(77_777, null, keepUnlinked: false, CancellationToken.None));
        Assert.Equal(BookError.NoAccount, ex.Reason);
    }

}
