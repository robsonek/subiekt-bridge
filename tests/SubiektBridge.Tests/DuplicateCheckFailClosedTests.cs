using System.Text.Json;
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
/// Spec 2026-10-10 (backlog 138): awaria skanu duplikatów po external_reference = 503 DUPLICATE_CHECK_UNAVAILABLE
/// (nic nie zapisano, retry tym samym kluczem, NIC w cache idempotencji) na FS/KFS/PZ/MM; pusty albo za długi
/// external_reference = 422 INVALID_EXTERNAL_REFERENCE PRZED walidacją notatek. Ścieżka COM/SQL jest windows-only -
/// tu Fake + kontrolery.
/// </summary>
[Collection("FakeSferaSharedState")]
public class DuplicateCheckFailClosedTests
{
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
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_dupscan_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static JsonElement DetailsOf(object? value)
    {
        var err = Assert.IsType<ErrorResponseDto>(value);
        Assert.NotNull(err.Details);
        return JsonSerializer.SerializeToElement(err.Details);
    }

    private static ContractorDto Contractor() => new(
        IsPerson: true, Symbol: "K1", Nip: null, Name: "Test Klient", FullName: null, FirstName: null,
        LastName: null, Email: null, Address: new AddressDto("ul. Testowa 1", "00-001", "Warszawa", "PL"));

    private static InvoiceRequestDto Invoice(string reference, string notes = "") => new(
        Type: "FS", IssueDate: "", SaleDate: "",
        Payment: new PaymentDto("PlatnoscPrzelew", null, 100m, true),
        Currency: "PLN", Contractor: Contractor(),
        Lines: new[] { new LineDto("5901234123457", "Towar testowy", 1m, "szt.", 100m, 23m) },
        Shipping: new ShippingDto(false, "Wysyłka", 0m, 23m),
        Totals: new InvoiceTotalsDto(Gross: 100m),
        ExternalReference: reference, Notes: notes);

    private static InvoiceCorrectionRequestDto Correction(string reference, string reason = "zwrot") => new("", reason, false, null, null,
        new[] { new CorrectionLineDto("5901234123457", "Towar", -1m, "szt.", 100m, 23m) }, reference);

    private static ReceiptIssueRequestDto Receipt(string reference, string notes = "") => new("", null, Contractor(),
        new[] { new LineDto("5901234123457", "Towar", 1m, "szt.", 100m, 23m) }, reference, notes);

    private static TransferRequestDto Transfer(string reference, string notes = "") => new(1, 4, new[] { new TransferLineDto("5901234123457", 1m) }, reference, notes);

    private static string NewRef() => $"test:{Guid.NewGuid():N}";

    // ----------------------------- 43-46: 503 DUPLICATE_CHECK_UNAVAILABLE, klucz zostaje, retry -> 201 -----------------------------

    /// <summary>
    /// Skan padł -> 503 DUPLICATE_CHECK_UNAVAILABLE z details {external_reference, document_type}, NIC w cache
    /// idempotencji (503 nie jest zapisywane), a po powrocie skanu ten sam klucz -> 201 i wpis w cache.
    /// Mutant: catch-all -> 500 INTERNAL_ERROR (kod spoza listy retry); Mutant: SaveAsync przed try -> replay 503.
    /// </summary>
    private static async Task AssertDuplicateCheckUnavailableKeepsKeyThenRetrySucceeds(
        FakeSferaSession fake, IdempotencyStore store, string key, string reference, string expectedType,
        Func<string, Task<IActionResult?>> call)
    {
        fake.FailDuplicateCheckForTests = true;
        var (status, value) = Unwrap(await call(key));
        Assert.Equal(503, status);
        Assert.Equal("DUPLICATE_CHECK_UNAVAILABLE", Assert.IsType<ErrorResponseDto>(value).Code);
        var details = DetailsOf(value);
        Assert.Equal(reference, details.GetProperty("external_reference").GetString());
        Assert.Equal(expectedType, details.GetProperty("document_type").GetString());
        Assert.Null(await store.TryGetAsync<object>(key, CancellationToken.None));

        fake.FailDuplicateCheckForTests = false;
        var retry = Unwrap(await call(key));
        Assert.Equal(201, retry.status);
        Assert.NotNull(await store.TryGetAsync<object>(key, CancellationToken.None));
    }

    [Fact]
    public async Task CreateInvoice_DuplicateCheckFails_503_KeyKept_RetryCreates()
    {
        var fake = new FakeSferaSession(); var store = NewStore();
        var c = new InvoicesController(fake, store, NullLogger<InvoicesController>.Instance);
        var reference = NewRef();
        await AssertDuplicateCheckUnavailableKeepsKeyThenRetrySucceeds(fake, store, "k-fs-dup", reference, "FS",
            async key => (await c.Create(Invoice(reference), key, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task CreateCorrection_DuplicateCheckFails_503_KeyKept_RetryCreates()
    {
        var fake = new FakeSferaSession(); var store = NewStore();
        var c = new InvoicesController(fake, store, NullLogger<InvoicesController>.Instance);
        var reference = NewRef();
        await AssertDuplicateCheckUnavailableKeepsKeyThenRetrySucceeds(fake, store, "k-kfs-dup", reference, "KFS",
            async key => (await c.CreateCorrection("fake_inv_000001", Correction(reference), key, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task CreateReceipt_DuplicateCheckFails_503_KeyKept_RetryCreates()
    {
        var fake = new FakeSferaSession(); var store = NewStore();
        var c = new ReceiptsController(fake, store, NullLogger<ReceiptsController>.Instance);
        var reference = NewRef();
        await AssertDuplicateCheckUnavailableKeepsKeyThenRetrySucceeds(fake, store, "k-pz-dup", reference, "PZ",
            async key => (await c.Create(Receipt(reference), key, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task CreateTransfer_DuplicateCheckFails_503_KeyKept_RetryCreates()
    {
        var fake = new FakeSferaSession(); var store = NewStore();
        var c = new TransfersController(fake, store, NullLogger<TransfersController>.Instance);
        var reference = NewRef();
        await AssertDuplicateCheckUnavailableKeepsKeyThenRetrySucceeds(fake, store, "k-mm-dup", reference, "MM",
            async key => (await c.Create(Transfer(reference), key, CancellationToken.None)).Result);
    }

    // ----------------------------- 47-54: 422 INVALID_EXTERNAL_REFERENCE -----------------------------

    private static void AssertInvalidReference((int status, object? value) result)
    {
        Assert.Equal(422, result.status);
        Assert.Equal("INVALID_EXTERNAL_REFERENCE", Assert.IsType<ErrorResponseDto>(result.value).Code);
        Assert.Equal(495, DetailsOf(result.value).GetProperty("max_length").GetInt32());
    }

    private static readonly string TooLong = new string('r', 496);
    private static readonly string MaxLen = new string('r', 495);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateInvoice_EmptyExternalReference_422(string reference)
    {
        // Mutant: brak walidacji -> skan pominiety (fail-open po cichu).
        var c = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);
        AssertInvalidReference(Unwrap((await c.Create(Invoice(reference), "k-fs-empty", CancellationToken.None)).Result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateCorrection_EmptyExternalReference_422(string reference)
    {
        var c = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);
        AssertInvalidReference(Unwrap((await c.CreateCorrection("fake_inv_000001", Correction(reference), "k-kfs-empty", CancellationToken.None)).Result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateReceipt_EmptyExternalReference_422(string reference)
    {
        var c = new ReceiptsController(new FakeSferaSession(), NewStore(), NullLogger<ReceiptsController>.Instance);
        AssertInvalidReference(Unwrap((await c.Create(Receipt(reference), "k-pz-empty", CancellationToken.None)).Result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTransfer_EmptyExternalReference_422(string reference)
    {
        var c = new TransfersController(new FakeSferaSession(), NewStore(), NullLogger<TransfersController>.Instance);
        AssertInvalidReference(Unwrap((await c.Create(Transfer(reference), "k-mm-empty", CancellationToken.None)).Result));
    }

    [Fact]
    public async Task CreateInvoice_ExternalReferenceTooLong_WithNotes_422InvalidReference_NotNotesTooLong()
    {
        // 496 znakow + NIEPUSTE notatki: wygrywa INVALID_EXTERNAL_REFERENCE (walidacja ref PRZED ValidateNotes).
        var c = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);
        AssertInvalidReference(Unwrap((await c.Create(Invoice(TooLong, notes: "x"), "k-fs-long", CancellationToken.None)).Result));
    }

    [Fact]
    public async Task CreateCorrection_ExternalReferenceTooLong_422InvalidReference_NotNotesTooLong()
    {
        var c = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);
        AssertInvalidReference(Unwrap((await c.CreateCorrection("fake_inv_000001", Correction(TooLong), "k-kfs-long", CancellationToken.None)).Result));
    }

    [Fact]
    public async Task CreateReceipt_ExternalReferenceTooLong_WithNotes_422InvalidReference()
    {
        var c = new ReceiptsController(new FakeSferaSession(), NewStore(), NullLogger<ReceiptsController>.Instance);
        AssertInvalidReference(Unwrap((await c.Create(Receipt(TooLong, notes: "x"), "k-pz-long", CancellationToken.None)).Result));
    }

    [Fact]
    public async Task CreateTransfer_ExternalReferenceTooLong_WithNotes_422InvalidReference()
    {
        var c = new TransfersController(new FakeSferaSession(), NewStore(), NullLogger<TransfersController>.Instance);
        AssertInvalidReference(Unwrap((await c.Create(Transfer(TooLong, notes: "x"), "k-mm-long", CancellationToken.None)).Result));
    }

    // ----------------------------- 54b/54c: granica 495 -----------------------------

    [Fact]
    public async Task CreateInvoice_ExternalReference495_EmptyNotes_201()
    {
        // Mutant: granica >= w ValidateExternalReference.
        var c = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);
        Assert.Equal(201, Unwrap((await c.Create(Invoice(MaxLen), "k-fs-495", CancellationToken.None)).Result).status);
    }

    [Fact]
    public async Task CreateReceipt_ExternalReference495_EmptyNotes_201()
    {
        var c = new ReceiptsController(new FakeSferaSession(), NewStore(), NullLogger<ReceiptsController>.Instance);
        Assert.Equal(201, Unwrap((await c.Create(Receipt(MaxLen), "k-pz-495", CancellationToken.None)).Result).status);
    }

    [Fact]
    public async Task CreateTransfer_ExternalReference495_EmptyNotes_201()
    {
        var c = new TransfersController(new FakeSferaSession(), NewStore(), NullLogger<TransfersController>.Instance);
        Assert.Equal(201, Unwrap((await c.Create(Transfer(MaxLen), "k-mm-495", CancellationToken.None)).Result).status);
    }

    [Fact]
    public async Task CreateCorrection_ExternalReference495_422NotesTooLong()
    {
        // KFS zawsze ma "Korekta: {reason}" w Uwagach - przy ref 495 nie miesci sie w 500 -> NOTES_TOO_LONG (istniejaca regula).
        // Mutant: KFS pomija ValidateNotes.
        var c = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);
        var (status, value) = Unwrap((await c.CreateCorrection("fake_inv_000001", Correction(MaxLen), "k-kfs-495", CancellationToken.None)).Result);

        Assert.Equal(422, status);
        Assert.Equal("NOTES_TOO_LONG", Assert.IsType<ErrorResponseDto>(value).Code);
    }
}
