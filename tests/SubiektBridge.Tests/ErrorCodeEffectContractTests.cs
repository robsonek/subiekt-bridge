using System.Text.Json;
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
/// Spec 2026-09-29 „kod błędu = dowód o skutku w Subiekcie" (W2–W6): klient klasyfikuje odpowiedź po kodzie
/// i od tego zależy, czy ponowi. Kod „bezpieczny retry" wolno zwrócić tylko, gdy nic nie zapisano; ORPHAN niesie
/// `details.bank_operation_subiekt_id` (long|null); 503 SUBIEKT_UNAVAILABLE = Subiekt offline PRZED pierwszym
/// zapisem (klucz idempotencji zostaje). Ścieżki COM są windows-only - tu Fake + kontrolery.
/// </summary>
[Collection("FakeSferaSharedState")]
public class ErrorCodeEffectContractTests
{
    public ErrorCodeEffectContractTests()
    {
        FakeSferaSession.ResetBankBookingForTests();
        FakeSferaSession.ResetSettlementsForTests();
    }

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_effect_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static BankTransactionsController BookController(FakeSferaSession fake, IdempotencyStore? store = null)
        => new(fake, store ?? NewStore(),
            Options.Create(new BridgeOptions { EnableHbBooking = true }),
            NullLogger<BankTransactionsController>.Instance);

    private static JsonElement DetailsOf(object? value)
    {
        var err = Assert.IsType<ErrorResponseDto>(value);
        Assert.NotNull(err.Details);
        return JsonSerializer.SerializeToElement(err.Details);
    }

    private static ContractorDto Contractor() => new(
        IsPerson: true, Symbol: "K1", Nip: null, Name: "Test Klient", FullName: null, FirstName: null,
        LastName: null, Email: null, Address: new AddressDto("ul. Testowa 1", "00-001", "Warszawa", "PL"));

    private static InvoiceRequestDto Invoice() => new(
        Type: "FS", IssueDate: "", SaleDate: "",
        Payment: new PaymentDto("PlatnoscPrzelew", null, 100m, true),
        Currency: "PLN", Contractor: Contractor(),
        Lines: new[] { new LineDto("5901234123457", "Towar testowy", 1m, "szt.", 100m, 23m) },
        Shipping: new ShippingDto(false, "Wysyłka", 0m, 23m),
        Totals: new InvoiceTotalsDto(Gross: 100m),
        ExternalReference: $"test:{Guid.NewGuid():N}", Notes: "");

    private static InvoiceCorrectionRequestDto Correction() => new("", "zwrot", false, null, null,
        new[] { new CorrectionLineDto("5901234123457", "Towar", -1m, "szt.", 100m, 23m) }, $"test:kfs:{Guid.NewGuid():N}");

    private static ReceiptIssueRequestDto Receipt() => new("", null, Contractor(),
        new[] { new LineDto("5901234123457", "Towar", 1m, "szt.", 100m, 23m) }, $"test:pz:{Guid.NewGuid():N}", "");

    private static TransferRequestDto Transfer() => new(1, 4, new[] { new TransferLineDto("5901234123457", 1m) }, $"test:mm:{Guid.NewGuid():N}", "");

    private static SettlementCreateRequestDto Settlement() => new(BankOperationSubiektId: 5_001, Amount: 50m);

    // ----------------------------- W3: 503 SUBIEKT_UNAVAILABLE przed pierwszym zapisem -----------------------------

    /// <summary>
    /// Sesja Sfery niedostępna na starcie mutacji -> 503 SUBIEKT_UNAVAILABLE (klient: retry tym samym kluczem),
    /// NIC w cache idempotencji, a po powrocie Subiekta ten sam klucz -> 201 (nie replay, bo 503 nie było zapisane).
    /// </summary>
    private static async Task AssertUnavailableKeepsKeyThenRetrySucceeds(
        FakeSferaSession fake, IdempotencyStore store, string key, Func<string, Task<IActionResult?>> call)
    {
        fake.SferaUnavailableForTests = true;
        var (status, value) = Unwrap(await call(key));
        Assert.Equal(503, status);
        Assert.Equal("SUBIEKT_UNAVAILABLE", Assert.IsType<ErrorResponseDto>(value).Code);
        Assert.Null(await store.TryGetAsync<object>(key, CancellationToken.None));

        fake.SferaUnavailableForTests = false;
        var retry = Unwrap(await call(key));
        Assert.Equal(201, retry.status);
        Assert.NotNull(await store.TryGetAsync<object>(key, CancellationToken.None));
    }

    [Fact]
    public async Task CreateInvoice_SubiektDown_503_KeyKept_RetryCreates()
    {
        var fake = new FakeSferaSession(); var store = NewStore();
        var c = new InvoicesController(fake, store, NullLogger<InvoicesController>.Instance);
        var request = Invoice();
        await AssertUnavailableKeepsKeyThenRetrySucceeds(fake, store, "k-fs-down",
            async key => (await c.Create(request, key, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task CreateCorrection_SubiektDown_503_KeyKept_RetryCreates()
    {
        var fake = new FakeSferaSession(); var store = NewStore();
        var c = new InvoicesController(fake, store, NullLogger<InvoicesController>.Instance);
        var request = Correction();
        await AssertUnavailableKeepsKeyThenRetrySucceeds(fake, store, "k-kfs-down",
            async key => (await c.CreateCorrection("fake_inv_000001", request, key, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task CreateReceipt_SubiektDown_503_KeyKept_RetryCreates()
    {
        var fake = new FakeSferaSession(); var store = NewStore();
        var c = new ReceiptsController(fake, store, NullLogger<ReceiptsController>.Instance);
        var request = Receipt();
        await AssertUnavailableKeepsKeyThenRetrySucceeds(fake, store, "k-pz-down",
            async key => (await c.Create(request, key, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task CreateTransfer_SubiektDown_503_KeyKept_RetryCreates()
    {
        var fake = new FakeSferaSession(); var store = NewStore();
        var c = new TransfersController(fake, store, NullLogger<TransfersController>.Instance);
        var request = Transfer();
        await AssertUnavailableKeepsKeyThenRetrySucceeds(fake, store, "k-mm-down",
            async key => (await c.Create(request, key, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task CreateSettlement_SubiektDown_503_KeyKept_RetryCreates()
    {
        var fake = new FakeSferaSession(); var store = NewStore();
        var c = new SettlementsController(fake, store, NullLogger<SettlementsController>.Instance);
        var request = Settlement();
        await AssertUnavailableKeepsKeyThenRetrySucceeds(fake, store, "k-settle-down",
            async key => (await c.Create("sub_1700001", request, key, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Book_SubiektDown_503_KeyKept_RetryCreates()
    {
        var fake = new FakeSferaSession(); var store = NewStore();
        var c = BookController(fake, store);
        await AssertUnavailableKeepsKeyThenRetrySucceeds(fake, store, "k-book-down",
            async key => (await c.Book(12_345, new BookRequestDto(), key, CancellationToken.None)).Result);
    }

    // ----------------------------- W4: KFS niesie kontrahenta FS źródłowej -----------------------------

    [Fact]
    public async Task CreateCorrection_ReturnsContractorOfSourceInvoice_NotZero()
    {
        // Klient zapisuje contractor_subiekt_id jako kontrahenta dokumentu; 0 = "nieznany". Kontrahent KFS
        // = kontrahent (płatnik) FS źródłowej, deterministyczny dla tego samego źródła.
        var c = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);
        var first = Unwrap((await c.CreateCorrection("fake_inv_000123", Correction(), "k-kfs-kh-1", CancellationToken.None)).Result);
        var second = Unwrap((await c.CreateCorrection("fake_inv_000123", Correction(), "k-kfs-kh-2", CancellationToken.None)).Result);

        Assert.Equal(201, first.status);
        long kh = Assert.IsType<InvoiceResponseDto>(first.value).ContractorSubiektId;
        Assert.NotEqual(0, kh);
        Assert.Equal(kh, Assert.IsType<InvoiceResponseDto>(second.value).ContractorSubiektId);
    }

    // ----------------------------- W5: existing_rozliczenie_id = null, gdy nieznane -----------------------------

    [Fact]
    public async Task CreateSettlement_DuplicateWithUnknownId_409_ExistingRozliczenieIdNull()
    {
        // Duplikat wykryty po SplataId, ale RozliczenieId linii nieodczytany: kontrakt mówi `int | null`,
        // nie `-1` (klient i tak przyjmował tylko > 0; null = czytelny "istnieje, numer nieznany").
        var c = new SettlementsController(new FakeSferaSession(), NewStore(), NullLogger<SettlementsController>.Instance);
        var (status, value) = Unwrap((await c.Create("sub_1900002", Settlement(), "k-dup-null", CancellationToken.None)).Result);

        Assert.Equal(409, status);
        Assert.Equal("DUPLICATE_SETTLEMENT", Assert.IsType<ErrorResponseDto>(value).Code);
        var details = DetailsOf(value);
        Assert.Equal(JsonValueKind.Null, details.GetProperty("existing_rozliczenie_id").ValueKind);
        Assert.Equal(5_001, details.GetProperty("bank_operation_subiekt_id").GetInt64());
    }

    [Fact]
    public async Task Health_SubiektDown_Reports503_SessionDown()
    {
        // Health sonduje sesję (nie tylko "czy _subiekt ustawione") - martwa sesja = status 503 / sfera_session "down".
        var fake = new FakeSferaSession { SferaUnavailableForTests = true };
        var (status, value) = Unwrap((await new HealthController(fake).Get(CancellationToken.None)).Result);
        Assert.Equal(503, status);
        Assert.Equal("down", Assert.IsType<HealthResponseDto>(value).SferaSession);
    }

    // ----------------------------- W2: HB_BOOKING_ORPHAN z details -----------------------------

    [Fact]
    public async Task Book_Orphan_WithKnownId_DetailsCarryBankOperationId()
    {
        // Link padł i rollback BP padł: nzfId znany -> klient zapisuje id do ręcznego usunięcia.
        var controller = BookController(new FakeSferaSession());
        var (status, value) = Unwrap((await controller.Book(44_444, new BookRequestDto(), "k-orphan-id", CancellationToken.None)).Result);

        Assert.Equal(500, status);
        Assert.Equal("HB_BOOKING_ORPHAN", Assert.IsType<ErrorResponseDto>(value).Code);
        var details = DetailsOf(value);
        Assert.Equal(90_000 + 44_444, details.GetProperty("bank_operation_subiekt_id").GetInt64());
        Assert.Equal(44_444, details.GetProperty("hb_id").GetInt64());
    }

    [Fact]
    public async Task Book_Orphan_AfterSaveUnknownId_DetailsNull_NotFailed()
    {
        // W1: wyjątek PO Zapisz (id nieodczytane) -> ORPHAN bez id, NIGDY HB_BOOKING_FAILED (retry = drugi BP).
        var controller = BookController(new FakeSferaSession());
        var (status, value) = Unwrap((await controller.Book(44_445, new BookRequestDto(), "k-orphan-noid", CancellationToken.None)).Result);

        Assert.Equal(500, status);
        Assert.Equal("HB_BOOKING_ORPHAN", Assert.IsType<ErrorResponseDto>(value).Code);
        var details = DetailsOf(value);
        Assert.Equal(JsonValueKind.Null, details.GetProperty("bank_operation_subiekt_id").ValueKind);
        Assert.Equal(44_445, details.GetProperty("hb_id").GetInt64());
    }
}
