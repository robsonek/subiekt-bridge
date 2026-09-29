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
/// Poprawki z listy otwartych findingów audytu 2026-06-10 (v0.17.0): filtr nip w GET /invoices,
/// /health ze stanem SqlClient, walidacja Symbolu kontrahenta (422 zamiast 500) i ilości decimal.
/// RealSferaSession (COM/SQL) jest windows-only - testujemy wydzieloną czystą logikę + Fake/kontrolery.
/// </summary>
public class AuditFixesTests
{
    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_audit_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

    private static ContractorDto Contractor(string symbol, string? nip = null) => new(
        IsPerson: nip is null,
        Symbol: symbol,
        Nip: nip,
        Name: "Test Klient",
        FullName: null,
        FirstName: null,
        LastName: null,
        Email: null,
        Address: new AddressDto("ul. Testowa 1", "00-001", "Warszawa", "PL"));

    private static InvoiceRequestDto Invoice(ContractorDto contractor, decimal quantity = 1m, decimal unitPrice = 100m) => new(
        Type: "FS",
        IssueDate: "",
        SaleDate: "",
        Payment: new PaymentDto("PlatnoscPrzelew", null, quantity * unitPrice, true),
        Currency: "PLN",
        Contractor: contractor,
        Lines: new[] { new LineDto("5901234123457", "Towar testowy", quantity, "szt.", unitPrice, 23m) },
        Shipping: new ShippingDto(false, "Wysyłka", 0m, 23m),
        Totals: new InvoiceTotalsDto(Gross: quantity * unitPrice),
        ExternalReference: $"test:{Guid.NewGuid():N}",
        Notes: "");

    // ----------------------------- GET /invoices?nip= -----------------------------

    [Theory]
    [InlineData("111-111-11-11", "1111111111")]
    [InlineData("111 111 11 11", "1111111111")]
    [InlineData("1111111111", "1111111111")]
    public void NormalizeNip_StripsDashesAndSpaces(string raw, string expected)
        => Assert.Equal(expected, InvoiceQueryFields.NormalizeNip(raw));

    [Fact]
    public void ContractorClause_FiltersByBuyer_NotNonexistentColumn()
    {
        var clause = InvoiceQueryFields.ContractorClause(new long[] { 5, 7 });

        Assert.Equal("dok_PlatnikId IN (5,7)", clause);
        Assert.DoesNotContain("NabKodSlownik", clause);
        // dok_OdbiorcaId na MM = id magazynu - kh_Id nie może trafić w magazyn.
        Assert.DoesNotContain("OdbiorcaId", clause);
    }

    [Fact]
    public void ContractorClause_EmptyIds_ReturnsNull()
        => Assert.Null(InvoiceQueryFields.ContractorClause(Array.Empty<long>()));

    // ----------------------------- /health + SqlClient -----------------------------

    [Fact]
    public void Health_SferaAndSqlOk_Returns200Ok()
    {
        var (status, body) = HealthController.Map(new SferaHealthDto("1.89 HF2", true, null, null, SqlConnectionOk: true));

        Assert.Equal(200, status);
        Assert.Equal("ok", body.Status);
        Assert.Equal("active", body.SferaSession);
        Assert.Equal("ok", body.SqlConnection);
        Assert.Null(body.SqlError);
    }

    [Fact]
    public void Health_SqlDown_SferaActive_Returns200Degraded()
    {
        var (status, body) = HealthController.Map(
            new SferaHealthDto("1.89 HF2", true, null, null, SqlConnectionOk: false, SqlError: "SqlException: Login failed"));

        // 200, nie 503: wystawianie przez COM działa, klient nie powinien wstrzymywać kolejki dokumentów.
        Assert.Equal(200, status);
        Assert.Equal("degraded", body.Status);
        Assert.Equal("active", body.SferaSession);
        Assert.Equal("down", body.SqlConnection);
        Assert.Equal("SqlException: Login failed", body.SqlError);
    }

    [Fact]
    public void Health_SferaDown_Returns503()
    {
        var (status, body) = HealthController.Map(new SferaHealthDto("unknown", false, null, "COMException: x", SqlConnectionOk: true));

        Assert.Equal(503, status);
        Assert.Equal("degraded", body.Status);
        Assert.Equal("down", body.SferaSession);
    }

    [Fact]
    public async Task Health_Fake_ReportsSqlOk()
    {
        var controller = new HealthController(new FakeSferaSession());

        var (status, value) = Unwrap((await controller.Get(CancellationToken.None)).Result);

        Assert.Equal(200, status);
        var body = Assert.IsType<HealthResponseDto>(value);
        Assert.Equal("ok", body.SqlConnection);
    }

    // ----------------------------- Symbol kontrahenta -----------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")] // 21 znaków
    public void ValidateSymbol_InvalidSymbols_ReturnError(string? symbol)
        => Assert.NotNull(ContractorFields.ValidateSymbol(symbol));

    [Theory]
    [InlineData("K1")]
    [InlineData("ABCDEFGHIJKLMNOPQRST")] // dokładnie 20 = varchar(20) kh_Symbol
    public void ValidateSymbol_ValidSymbols_ReturnNull(string symbol)
        => Assert.Null(ContractorFields.ValidateSymbol(symbol));

    [Fact]
    public async Task CreateInvoice_TooLongSymbolWithoutNip_Returns422()
    {
        var controller = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);

        var result = await controller.Create(Invoice(Contractor("allegro-user-3f2a9c1e")), Guid.NewGuid().ToString(), CancellationToken.None);

        var (status, value) = Unwrap(result.Result);
        Assert.Equal(422, status);
        Assert.Equal("INVALID_CONTRACTOR_SYMBOL", Assert.IsType<ErrorResponseDto>(value).Code);
    }

    [Fact]
    public async Task CreateInvoice_TooLongSymbolWithNip_Passes()
    {
        // Z NIP-em Real szuka najpierw po NIP i Symbol idzie do Subiekta dopiero po chybionym lookupie -
        // dlatego walidacja nie może blokować requestu z góry.
        var controller = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);

        var result = await controller.Create(Invoice(Contractor("allegro-user-3f2a9c1e", nip: "1111111111")), Guid.NewGuid().ToString(), CancellationToken.None);

        Assert.Equal(201, Unwrap(result.Result).status);
    }

    [Fact]
    public async Task CreateReceipt_TooLongSupplierSymbol_Returns422()
    {
        var controller = new ReceiptsController(new FakeSferaSession(), NewStore(), NullLogger<ReceiptsController>.Instance);
        var request = new ReceiptIssueRequestDto(
            IssueDate: "",
            WarehouseSubiektId: null,
            Supplier: Contractor("dostawca-bardzo-dlugi-symbol"),
            Lines: new[] { new LineDto("5901234123457", "Towar testowy", 1m, "szt.", 100m, 23m) },
            ExternalReference: $"test:{Guid.NewGuid():N}",
            Notes: "");

        var result = await controller.Create(request, Guid.NewGuid().ToString(), CancellationToken.None);

        var (status, value) = Unwrap(result.Result);
        Assert.Equal(422, status);
        Assert.Equal("INVALID_CONTRACTOR_SYMBOL", Assert.IsType<ErrorResponseDto>(value).Code);
    }

    // ----------------------------- Ilości decimal -----------------------------

    [Theory]
    [InlineData("2", 2)]
    [InlineData("1.5", 1.5)]
    [InlineData("0.125", 0.125)]
    public void LineDto_Quantity_AcceptsIntegerAndFractionalJson(string jsonNumber, double expected)
    {
        var json = $$"""{"ean":null,"name_fallback":"Kabel","quantity":{{jsonNumber}},"unit":"m","unit_price_gross":10,"vat_rate":23}""";

        var line = JsonSerializer.Deserialize<LineDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal((decimal)expected, line!.Quantity);
    }

    [Fact]
    public async Task CreateInvoice_FractionalQuantity_TotalsValidated()
    {
        var controller = new InvoicesController(new FakeSferaSession(), NewStore(), NullLogger<InvoicesController>.Instance);

        // 2.5 kg x 12.40 = 31.00 - suma kontrolna liczona na decimal, bez obcinania ilości do int.
        var result = await controller.Create(Invoice(Contractor("K1"), quantity: 2.5m, unitPrice: 12.40m), Guid.NewGuid().ToString(), CancellationToken.None);

        Assert.Equal(201, Unwrap(result.Result).status);
    }
}
