using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Most = czysty passthrough surowych przelewów (hb_Transakcja). Bez matchingu, bez interpretacji -
/// dopasowanie i decyzje robi Laravel. Testy przeciw FakeSferaSession + kontrolerowi (HTTP).
/// Real (raw UPDATE hb_Transakcja + COM) testowalny tylko na prod DB (test odwracalny §7 planu).
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

    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_book_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static BankTransactionsController NewController(FakeSferaSession fake, bool enableBooking = false)
        => new(fake, NewStore(),
            Options.Create(new BridgeOptions { EnableHbBooking = enableBooking }),
            NullLogger<BankTransactionsController>.Instance);

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

    // ---------- POST /bank-transactions/{hb_id}/book - logika FakeSfera (wariant B) ----------

    [Fact]
    public async Task Book_FirstCall_Creates_SecondCall_AlreadyBooked()
    {
        var fake = new FakeSferaSession();
        var first = await fake.BookBankTransactionAsync(13128, null, CancellationToken.None);
        var second = await fake.BookBankTransactionAsync(13128, null, CancellationToken.None);

        // Wariant B: most sam ustawia link (raw UPDATE) -> linked zawsze true na sukcesie.
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
        var ex = await Assert.ThrowsAsync<BankBookingException>(() => fake.BookBankTransactionAsync(-1, null, CancellationToken.None));
        Assert.Equal(BookError.TransactionNotFound, ex.Reason);
    }

    [Fact]
    public async Task Book_NoAccount_Throws()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<BankBookingException>(() => fake.BookBankTransactionAsync(77_777, null, CancellationToken.None));
        Assert.Equal(BookError.NoAccount, ex.Reason);
    }

    [Fact]
    public async Task Book_ForeignAccount_Throws()
    {
        // Rachunek walutowy (rb_IdWaluty != 'PLN') -> odrzucamy zanim utworzymy BP (R7).
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<BankBookingException>(() => fake.BookBankTransactionAsync(66_666, null, CancellationToken.None));
        Assert.Equal(BookError.ForeignAccount, ex.Reason);
    }

    // ---------- POST /bank-transactions/{hb_id}/book - kontroler (HTTP) ----------

    [Fact]
    public async Task Controller_Book_DefaultOptions_BookingActive()
    {
        // Domyslny config (brak klucza EnableHbBooking) -> ksiegowanie AKTYWNE (nie 501). Flaga zostaje jako
        // wylacznik (default ON) na wypadek dostepu do serwera; brak dostepu = i tak dziala po deployu.
        var controller = new BankTransactionsController(
            new FakeSferaSession(), NewStore(),
            Options.Create(new BridgeOptions()),
            NullLogger<BankTransactionsController>.Instance);
        var r = await controller.Book(13128, new BookRequestDto(), "key-default", CancellationToken.None);
        var (status, _) = Unwrap(r.Result);
        Assert.Equal(201, status);
    }

    [Fact]
    public async Task Controller_Book_FlagExplicitlyOff_Returns501()
    {
        // Jawne EnableHbBooking=false -> 501 (wylacznik dziala dla kogos z dostepem do appsettings).
        var controller = NewController(new FakeSferaSession(), enableBooking: false);
        var r = await controller.Book(13128, new BookRequestDto(), "key-off", CancellationToken.None);
        var (status, value) = Unwrap(r.Result);
        Assert.Equal(501, status);
        Assert.Equal("HB_BOOKING_NOT_SUPPORTED", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Book_FlagOn_MissingIdempotencyKey_Returns400()
    {
        var controller = NewController(new FakeSferaSession(), enableBooking: true);
        var r = await controller.Book(13128, new BookRequestDto(), idempotencyKey: null, CancellationToken.None);
        var (status, value) = Unwrap(r.Result);
        Assert.Equal(400, status);
        Assert.Equal("MISSING_IDEMPOTENCY_KEY", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Book_FlagOn_Success_Returns201()
    {
        var controller = NewController(new FakeSferaSession(), enableBooking: true);
        var r = await controller.Book(13128, new BookRequestDto(), "key-ok", CancellationToken.None);
        var (status, value) = Unwrap(r.Result);
        Assert.Equal(201, status);
        var dto = Assert.IsType<BookResultDto>(value);
        Assert.True(dto.Linked);
        Assert.False(dto.AlreadyBooked);
        Assert.NotNull(dto.BankOperationSubiektId);
    }

    [Fact]
    public async Task Controller_Book_FlagOn_SameKey_ReplaysCached()
    {
        // Ten sam Idempotency-Key -> drugie wywołanie zwraca CACHE (AlreadyBooked=false, 201),
        // a NIE ponowny call sesji (który dałby AlreadyBooked=true). To dowodzi short-circuit cache.
        var controller = NewController(new FakeSferaSession(), enableBooking: true);
        var first = await controller.Book(13128, new BookRequestDto(), "key-replay", CancellationToken.None);
        var second = await controller.Book(13128, new BookRequestDto(), "key-replay", CancellationToken.None);

        var (s1, v1) = Unwrap(first.Result);
        var (s2, v2) = Unwrap(second.Result);
        Assert.Equal(201, s1);
        Assert.Equal(201, s2);
        var d1 = Assert.IsType<BookResultDto>(v1);
        var d2 = Assert.IsType<BookResultDto>(v2);
        Assert.False(d2.AlreadyBooked);                                   // cache, nie re-call sesji
        Assert.Equal(d1.BankOperationSubiektId, d2.BankOperationSubiektId);
    }

    [Fact]
    public async Task Controller_Book_FlagOn_ForeignAccount_Returns422()
    {
        var controller = NewController(new FakeSferaSession(), enableBooking: true);
        var r = await controller.Book(66_666, new BookRequestDto(), "key-fx", CancellationToken.None);
        var (status, value) = Unwrap(r.Result);
        Assert.Equal(422, status);
        Assert.Equal("UNSUPPORTED_FOREIGN_ACCOUNT", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Book_FlagOn_NotFound_Returns404()
    {
        var controller = NewController(new FakeSferaSession(), enableBooking: true);
        var r = await controller.Book(-1, new BookRequestDto(), "key-404", CancellationToken.None);
        var (status, value) = Unwrap(r.Result);
        Assert.Equal(404, status);
        Assert.Equal("BANK_TRANSACTION_NOT_FOUND", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Book_FlagOn_Internal_Returns500_NoStackLeak()
    {
        // COM/Sfera padło przy tworzeniu BP (BP cofnięty/nie powstał) -> 500 HB_BOOKING_FAILED (retryowalne).
        var controller = NewController(new FakeSferaSession(), enableBooking: true);
        var r = await controller.Book(55_555, new BookRequestDto(), "key-internal", CancellationToken.None);
        var (status, value) = Unwrap(r.Result);
        Assert.Equal(500, status);
        Assert.Equal("HB_BOOKING_FAILED", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Book_FlagOn_Orphan_Returns500()
    {
        // Rollback BP padł po nieudanym linku -> ORPHAN (operacja bez linku). NIE wolno udawać 2xx sukcesu:
        // 500 HB_BOOKING_ORPHAN sygnalizuje interwencję ręczną (klient NIE retry'uje na ślepo -> kolejny BP).
        var controller = NewController(new FakeSferaSession(), enableBooking: true);
        var r = await controller.Book(44_444, new BookRequestDto(), "key-orphan", CancellationToken.None);
        var (status, value) = Unwrap(r.Result);
        Assert.Equal(500, status);
        Assert.Equal("HB_BOOKING_ORPHAN", ((ErrorResponseDto)value!).Code);
    }
}
