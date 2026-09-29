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
/// Poprawki z przeglądu RealSferaSession (2026-09-29), obszar dokumenty/kontrahenci: token anti-duplicate,
/// limit dok_Uwagi varchar(500) i 422 NOTES_TOO_LONG, INVALID_CORRECTION. Logika COM jest windows-only -
/// testujemy wydzielone helpery (UwagiFields) i mapowanie HTTP w kontrolerach (Fake).
/// </summary>
public class ReviewFixesDocumentsTests
{
    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_review_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

    private static ContractorDto Contractor() => new(
        IsPerson: true, Symbol: "K1", Nip: null, Name: "Test Klient", FullName: null, FirstName: null,
        LastName: null, Email: null, Address: new AddressDto("ul. Testowa 1", "00-001", "Warszawa", "PL"));

    private static InvoiceRequestDto Invoice(string notes, string reference = "test:order:1") => new(
        Type: "FS", IssueDate: "", SaleDate: "",
        Payment: new PaymentDto("PlatnoscPrzelew", null, 100m, true),
        Currency: "PLN", Contractor: Contractor(),
        Lines: new[] { new LineDto("5901234123457", "Towar testowy", 1m, "szt.", 100m, 23m) },
        Shipping: new ShippingDto(false, "Wysyłka", 0m, 23m),
        Totals: new InvoiceTotalsDto(Gross: 100m),
        ExternalReference: reference, Notes: notes);

    // ----------------------------- anti-duplicate: ref jako cały token -----------------------------

    [Theory]
    [InlineData("ref: nowysystem:order:123", "nowysystem:order:123", true)]
    [InlineData("Zamówienie z Allegro | ref: nowysystem:order:12", "nowysystem:order:12", true)]
    [InlineData("REF: NOWYSYSTEM:ORDER:12", "nowysystem:order:12", true)]           // jak LIKE pod polskim collation
    [InlineData("ref: nowysystem:order:123", "nowysystem:order:12", false)]         // podciąg - NIE duplikat
    [InlineData("ref: nowysystem:order:12", "order:12", false)]                     // inny namespace - NIE duplikat
    [InlineData("ref: nowysystem:order:12-b", "nowysystem:order:12", false)]
    [InlineData("", "nowysystem:order:12", false)]
    [InlineData(null, "nowysystem:order:12", false)]
    public void ContainsReferenceToken_MatchesWholeTokenOnly(string? uwagi, string reference, bool expected)
        => Assert.Equal(expected, UwagiFields.ContainsReferenceToken(uwagi, reference));

    // ----------------------------- dok_Uwagi varchar(500) -----------------------------

    [Fact]
    public void Build_AppendsReference_AndSkipsWhenAlreadyPresentAsToken()
    {
        Assert.Equal("ref: a:1", UwagiFields.Build(null, "a:1"));
        Assert.Equal("notatka | ref: a:1", UwagiFields.Build("notatka", "a:1"));
        Assert.Equal("ma już ref: a:1 w środku", UwagiFields.Build("ma już ref: a:1 w środku", "a:1"));
        // Podciąg innego refu nie liczy się jako obecny - dokleja.
        Assert.Equal("ref: a:12 | ref: a:1", UwagiFields.Build("ref: a:12", "a:1"));
    }

    [Fact]
    public void Build_TruncatesNotes_NeverReference()
    {
        var longNotes = new string('x', 600);
        var built = UwagiFields.Build(longNotes, "sys:order:42");

        Assert.Equal(UwagiFields.MaxLength, built.Length);
        Assert.EndsWith(" | ref: sys:order:42", built);
        Assert.True(UwagiFields.ContainsReferenceToken(built, "sys:order:42"));
    }

    [Fact]
    public void ValidateNotes_AccountsForReferenceSuffix()
    {
        const string reference = "sys:order:42"; // " | ref: sys:order:42" = 20 znaków
        Assert.Null(UwagiFields.ValidateNotes(null, reference));
        Assert.Null(UwagiFields.ValidateNotes(new string('x', 480), reference));
        Assert.NotNull(UwagiFields.ValidateNotes(new string('x', 481), reference));
    }

    [Fact]
    public async Task CreateInvoice_NotesTooLong_Returns422()
    {
        var controller = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);

        var result = await controller.Create(Invoice(new string('n', 500)), Guid.NewGuid().ToString(), CancellationToken.None);

        var (status, value) = Unwrap(result.Result);
        Assert.Equal(422, status);
        Assert.Equal("NOTES_TOO_LONG", Assert.IsType<ErrorResponseDto>(value).Code);
    }

    [Fact]
    public async Task CreateReceipt_NotesTooLong_Returns422()
    {
        var controller = new ReceiptsController(new FakeSferaSession(), NewStore(), NullLogger<ReceiptsController>.Instance);
        var request = new ReceiptIssueRequestDto("", null, Contractor(),
            new[] { new LineDto("5901234123457", "Towar", 1m, "szt.", 100m, 23m) },
            "test:pz:1", new string('n', 500));

        var (status, value) = Unwrap((await controller.Create(request, Guid.NewGuid().ToString(), CancellationToken.None)).Result);

        Assert.Equal(422, status);
        Assert.Equal("NOTES_TOO_LONG", Assert.IsType<ErrorResponseDto>(value).Code);
    }

    [Fact]
    public async Task CreateTransfer_NotesTooLong_Returns422()
    {
        var controller = new TransfersController(new FakeSferaSession(), NewStore(), NullLogger<TransfersController>.Instance);
        var request = new TransferRequestDto(1, 4, new[] { new TransferLineDto("5901234123457", 1m) }, "test:mm:1", new string('n', 500));

        var (status, value) = Unwrap((await controller.Create(request, Guid.NewGuid().ToString(), CancellationToken.None)).Result);

        Assert.Equal(422, status);
        Assert.Equal("NOTES_TOO_LONG", Assert.IsType<ErrorResponseDto>(value).Code);
    }

    [Fact]
    public async Task CreateCorrection_ReasonTooLong_Returns422()
    {
        var controller = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);
        var request = new InvoiceCorrectionRequestDto("", new string('r', 500), false, null, null,
            new[] { new CorrectionLineDto("5901234123457", "Towar", -1m, "szt.", 100m, 23m) }, "test:kfs:1");

        var (status, value) = Unwrap((await controller.CreateCorrection("fake_inv_000001", request, Guid.NewGuid().ToString(), CancellationToken.None)).Result);

        Assert.Equal(422, status);
        Assert.Equal("NOTES_TOO_LONG", Assert.IsType<ErrorResponseDto>(value).Code);
    }
}
