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
/// v0.21.1: POST /receipts zwraca totals.net/vat odczytane z zapisanego PZ (do v0.21.0 zawsze null), tą samą ścieżką co
/// GET /receipts/{id} (DocumentTotals). Real (COM) czyta atrybuty SuDokument przez delegat - testujemy helper na słowniku atrybutów
/// i Fake (model kwot Sfery) przez kontroler. Mutant przy każdym teście.
/// </summary>
[Collection("FakeSferaSharedState")]
public class ReceiptTotalsTests
{
    public ReceiptTotalsTests()
    {
        FakeSferaSession.ResetReceiptsForTests();
    }

    /// <summary>Atrybuty SuDokument jak widzi je TryReadDecimal: brak w słowniku = nieczytelne (null).</summary>
    private static Func<string, decimal?> Attrs(params (string Name, decimal Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    private static IReadOnlyList<LineDto> Lines(params LineDto[] lines) => lines;

    // ----------------------------- helper DocumentTotals (Real: name => TryReadDecimal(dok, name)) -----------------------------

    [Fact]
    public void H1_Issued_NetVatGross_FromDocument_NotFromLines()
    {
        // Mutant: Net/Vat = null (stan do v0.21.0) albo zamiana WartoscNetto <-> WartoscBrutto -> asercja.
        var read = DocumentTotals.Read(Attrs(("WartoscNetto", 172m), ("WartoscVat", 39.56m), ("WartoscBrutto", 211.56m)));
        var totals = DocumentTotals.ForIssuedReceipt(read, Lines(new LineDto("5901234123450", "Towar", 1m, "szt.", 999m, 23m)));
        Assert.Equal(172m, totals.Net);
        Assert.Equal(39.56m, totals.Vat);
        Assert.Equal(211.56m, totals.Gross);
    }

    [Fact]
    public void H2_Read_GetPath_AttributeNames_BruttoBeforeKwotaDoZaplaty()
    {
        // Mutant: zamiana nazw atrybutów albo KwotaDoZaplaty przed WartoscBrutto -> asercja.
        var read = DocumentTotals.Read(Attrs(("WartoscNetto", 1m), ("WartoscVat", 2m), ("WartoscBrutto", 3m), ("KwotaDoZaplaty", 4m)));
        Assert.Equal(new DocumentAmounts(1m, 2m, 3m), read);
    }

    [Fact]
    public void H3_UnreadableNetVat_StayNull_NeverZero()
    {
        // Fail-soft: nieczytelne pole = null jak dotąd. Mutant: "?? 0m" na netto/VAT albo liczenie netto z linii -> asercja.
        var read = DocumentTotals.Read(Attrs(("WartoscBrutto", 123m)));
        var totals = DocumentTotals.ForIssuedReceipt(read, Lines(new LineDto("5901234123450", "Towar", 1m, "szt.", 123m, 23m)));
        Assert.Null(totals.Net);
        Assert.Null(totals.Vat);
        Assert.Equal(123m, totals.Gross);
    }

    [Fact]
    public void H4_GrossUnreadable_FallsBackToLines_NetVatStillFromDocument()
    {
        // Brutto z linii tylko gdy dokument go nie podał (jak do v0.21.0): linia z unit_price_net -> netto * (1 + VAT), inaczej unit_price_gross.
        // Mutant: brak fallbacku (0/null) albo unit_price_gross zamiast netto*(1+VAT) dla linii netto -> asercja.
        var read = DocumentTotals.Read(Attrs(("WartoscNetto", 110m), ("WartoscVat", 23m)));
        var totals = DocumentTotals.ForIssuedReceipt(read, Lines(
            new LineDto("5901234123450", "Towar netto", 2m, "szt.", 0m, 23m, UnitPriceNet: 50m),
            new LineDto("5901234123451", "Towar brutto", 1m, "szt.", 10m, 23m)));
        Assert.Equal(110m, totals.Net);
        Assert.Equal(23m, totals.Vat);
        Assert.Equal(133m, totals.Gross);
    }

    [Fact]
    public void H5_BruttoMissing_KwotaDoZaplaty_SameAsGet()
    {
        // POST = ta sama ścieżka co GET: gdy WartoscBrutto nieczytelne, a KwotaDoZaplaty jest - obie odpowiedzi mają tę samą kwotę.
        // Mutant: osobna ścieżka POST bez KwotaDoZaplaty (brutto z linii = 999) -> asercja.
        var attrs = Attrs(("WartoscNetto", 172m), ("WartoscVat", 39.56m), ("KwotaDoZaplaty", 211.56m));
        var get = DocumentTotals.Read(attrs);
        var post = DocumentTotals.ForIssuedReceipt(DocumentTotals.Read(attrs), Lines(new LineDto("5901234123450", "Towar", 1m, "szt.", 999m, 23m)));
        Assert.Equal(get, new DocumentAmounts(post.Net, post.Vat, post.Gross));
        Assert.Equal(211.56m, post.Gross);
    }

    // ----------------------------- Fake + kontroler: POST /receipts -> totals, round-trip GET -----------------------------

    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_receipt_totals_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static ReceiptsController Controller(FakeSferaSession fake)
        => new(fake, NewStore(), NullLogger<ReceiptsController>.Instance);

    private static ContractorDto Supplier() => new(
        IsPerson: false, Symbol: "DOST1", Nip: "1111111111", Name: "Dostawca Testowy Sp. z o.o.", FullName: null, FirstName: null,
        LastName: null, Email: null, Address: new AddressDto("ul. Testowa 1", "00-001", "Warszawa", "PL"));

    /// <summary>Przypadki: linie + oczekiwane kwoty Sfery (netto pozycji do groszy, VAT z tabelki stawek do groszy, brutto = netto + VAT).</summary>
    private static (IReadOnlyList<LineDto> Lines, decimal Net, decimal Vat, decimal Gross) Case(string name) => name switch
    {
        // linia z unit_price_net (zalecane dla PZ): 2 x 100 netto, 23%
        "net_price" => (Lines(new LineDto("5901234123450", "Towar", 2m, "szt.", 0m, 23m, UnitPriceNet: 100m)), 200m, 46m, 246m),
        // linia z unit_price_gross: netto = 100 / 1.23 = 81.30 (do groszy), VAT 18.70
        "gross_price" => (Lines(new LineDto("5901234123450", "Towar", 1m, "szt.", 100m, 23m)), 81.30m, 18.70m, 100m),
        // usługa jednorazowa (bez EAN), 8%
        "one_off_service" => (Lines(new LineDto(null, "Transport", 1m, "szt.", 0m, 8m, UnitPriceNet: 50m)), 50m, 4m, 54m),
        // 2 linie, różne stawki: 23% (200 netto) + 8% (30 netto)
        "two_vat_rates" => (Lines(
            new LineDto("5901234123450", "Towar A", 2m, "szt.", 0m, 23m, UnitPriceNet: 100m),
            new LineDto("5901234123451", "Towar B", 3m, "szt.", 0m, 8m, UnitPriceNet: 10m)), 230m, 48.40m, 278.40m),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static ReceiptIssueRequestDto Receipt(IReadOnlyList<LineDto> lines)
        => new("", null, Supplier(), lines, $"test:pz-totals:{Guid.NewGuid():N}", "PZ testowe", "Client:test_buyer");

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        null => (0, null),
        _ => (-1, null),
    };

    [Theory]
    [InlineData("net_price")]
    [InlineData("gross_price")]
    [InlineData("one_off_service")]
    [InlineData("two_vat_rates")]
    public async Task P1_Post_ReturnsNetVatGross_ConsistentWithLines(string name)
    {
        // Mutant: Fake netto = brutto i VAT = 0 (stan do v0.21.0) albo netto/VAT null -> asercja.
        var (lines, net, vat, gross) = Case(name);
        var (s, v) = Unwrap((await Controller(new FakeSferaSession()).Create(Receipt(lines), $"k-p1-{name}", CancellationToken.None)).Result);
        Assert.Equal(201, s);
        var totals = Assert.IsType<InvoiceResponseDto>(v).Totals;
        Assert.Equal(net, totals.Net);
        Assert.Equal(vat, totals.Vat);
        Assert.Equal(gross, totals.Gross);
    }

    [Theory]
    [InlineData("net_price")]
    [InlineData("gross_price")]
    [InlineData("one_off_service")]
    [InlineData("two_vat_rates")]
    public async Task P2_Post_ThenGet_SameAmounts(string name)
    {
        // Round-trip: totals z POST = net/vat/gross_amount z GET tego PZ. Mutant: osobne liczenie kwot w POST i GET -> asercja.
        var fake = new FakeSferaSession();
        var c = Controller(fake);
        var (sc, vc) = Unwrap((await c.Create(Receipt(Case(name).Lines), $"k-p2-{name}", CancellationToken.None)).Result);
        Assert.Equal(201, sc);
        var created = Assert.IsType<InvoiceResponseDto>(vc);

        var (sg, vg) = Unwrap((await c.Get($"sub_{created.SubiektId}", CancellationToken.None)).Result);
        Assert.Equal(200, sg);
        var detail = Assert.IsType<ReceiptDetailDto>(vg);
        Assert.Equal(detail.NetAmount, created.Totals.Net);
        Assert.Equal(detail.VatAmount, created.Totals.Vat);
        Assert.Equal(detail.GrossAmount, created.Totals.Gross);
    }
}
