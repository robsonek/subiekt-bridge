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
/// Otwarte naleznosci = kandydaci do dopasowania z przelewem. Most NIE matchuje - zwraca OKNO kwotowe,
/// dopasowanie robi klient (jak GET /invoices do zamowien). Testy przeciw FakeSferaSession (logika filtra)
/// + InvoicesController (mapowanie HTTP). RealSferaSession (COM, nzf_Typ=39 + atrybuty FinDokument) testuje
/// sie tylko na Windowsie - [SupportedOSPlatform(windows)].
/// </summary>
public class OpenReceivablesTests
{
    private static OpenReceivablesQueryRequestDto Req(
        decimal? min = null, decimal? max = null, string? currency = "PLN", long? contractorId = null,
        string? from = null, string? to = null, int limit = 50)
        => new(min, max, currency, contractorId, from, to, limit);

    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_recv_{Guid.NewGuid():N}.db"),
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
    public async Task AmountWindow_ReturnsOnlyWithinRange()
    {
        var fake = new FakeSferaSession();
        // Okno wokol 371.12 (przelew po prowizji): [365, 380] - tylko FS 540 (371.12) pasuje.
        var r = await fake.QueryOpenReceivablesAsync(Req(min: 365m, max: 380m), CancellationToken.None);

        var only = Assert.Single(r);
        Assert.Equal(371.12m, only.Remaining);
        Assert.Equal(53310, only.DocumentSubiektId);
    }

    [Fact]
    public async Task NoWindow_ReturnsAllPln_ExcludesForeignCurrency()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenReceivablesAsync(Req(), CancellationToken.None);

        // 3 PLN naleznosci; rekord EUR (51900) odfiltrowany przez default currency=PLN.
        Assert.Equal(3, r.Count);
        Assert.All(r, x => Assert.Equal("PLN", x.Currency));
        Assert.DoesNotContain(r, x => x.DocumentSubiektId == 51900);
    }

    [Fact]
    public async Task PlnQuery_ExcludesForeignCurrencyRow()
    {
        // Parytet z Real: WartoscBiezaca jest zawsze w PLN, wiec rozrachunek walutowy bylby mylacy
        // (liczba PLN pod etykieta obcej waluty) i nierozliczalny -> most pomija nie-PLN nawet gdy
        // ktos przekaze currency="EUR" do sesji (controller i tak odrzuca to 422 wczesniej).
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenReceivablesAsync(Req(currency: "EUR"), CancellationToken.None);
        Assert.DoesNotContain(r, x => x.Currency != "PLN");
        Assert.DoesNotContain(r, x => x.DocumentSubiektId == 51900);
    }

    [Fact]
    public async Task ContractorFilter_NarrowsToOneContractor()
    {
        var fake = new FakeSferaSession();
        // 13292 (Szyszka) ma dwie otwarte PLN naleznosci (53447, 52001).
        var r = await fake.QueryOpenReceivablesAsync(Req(contractorId: 13292), CancellationToken.None);

        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Equal(13292, x.ContractorId));
        Assert.Contains(r, x => x.DocumentSubiektId == 53447);
        Assert.Contains(r, x => x.DocumentSubiektId == 52001);
    }

    [Fact]
    public async Task Limit_CapsResultCount()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenReceivablesAsync(Req(limit: 1), CancellationToken.None);
        Assert.Single(r);
    }

    [Fact]
    public async Task Dto_Shape_BridgeIdAndDocType()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenReceivablesAsync(Req(min: 3000m, max: 3500m), CancellationToken.None);

        var item = Assert.Single(r);
        Assert.Equal("sub_53447", item.DocumentId);            // bridge id -> POST /invoices/{id}/settlements
        Assert.Equal(53447, item.DocumentSubiektId);
        Assert.Equal("FS", item.DocType);
        Assert.Equal("Szyszka Krzysztof", item.ContractorName);
        Assert.Equal("FS 573/05/2026", item.Number);
        Assert.Equal($"sub_{item.DocumentSubiektId}", item.DocumentId); // niezmiennik formatu bridge id
    }

    [Fact]
    public async Task Dto_Shape_IncludesDateOriginalNip()
    {
        // Operator widzi date FV, kwote pierwotna (brutto) i NIP, by dopasowac przelew do kontrahenta.
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenReceivablesAsync(Req(min: 3000m, max: 3500m), CancellationToken.None);

        var item = Assert.Single(r);
        Assert.Equal("2026-06-12", item.Date);
        Assert.Equal(3372.50m, item.Original);
        Assert.Equal("1234563218", item.Nip);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]      // B2C: Real Subiekt zwraca pusty/whitespace NIP -> null
    [InlineData("1234563218", "1234563218")]
    public void NormalizeNip_MapsEmptyOrWhitespaceToNull(string? raw, string? expected)
    {
        // Kontrakt "null dla B2C" wydzielony z RealSferaSession (COM/windows-only) do testowalnej metody.
        Assert.Equal(expected, OpenReceivableFields.NormalizeNip(raw));
    }

    [Fact]
    public async Task Dto_Shape_NullNip_ForB2cContractor()
    {
        // Osoba prywatna (B2C) nie ma NIP -> null; kwota pierwotna nadal obecna.
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenReceivablesAsync(Req(min: 365m, max: 380m), CancellationToken.None);

        var item = Assert.Single(r); // 53310
        Assert.Null(item.Nip);
        Assert.Equal(382.60m, item.Original);
        Assert.Equal("2026-06-11", item.Date);
    }

    [Fact]
    public async Task MinOnly_NoUpperBound()
    {
        var fake = new FakeSferaSession();
        // Tylko dolna granica - bez max (decimal? = null) zwraca wszystko >= 200 PLN (53447 i 53310, NIE 52001=120).
        var r = await fake.QueryOpenReceivablesAsync(Req(min: 200m), CancellationToken.None);

        Assert.Equal(2, r.Count);
        Assert.DoesNotContain(r, x => x.DocumentSubiektId == 52001);
    }

    [Fact]
    public async Task DateFrom_NarrowsToRecent()
    {
        var fake = new FakeSferaSession();
        // from=2026-05-01 -> tylko czerwcowe (53447, 53310); 52001 (kwiecien) i 51900 (marzec) odpadaja.
        // Klient poda okno daty wokol przelewu - swiezy przelew -> swieza FV, dodatkowo tnie skan.
        var r = await fake.QueryOpenReceivablesAsync(Req(from: "2026-05-01"), CancellationToken.None);

        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Contains(x.DocumentSubiektId, new[] { 53447L, 53310L }));
    }

    [Fact]
    public async Task EmptyWindow_ReturnsEmpty_NoHang()
    {
        var fake = new FakeSferaSession();
        // Okno bez trafien (min=999990) - przypadek, ktory na prod v0.11.0 robil pelny skan -> timeout >90s.
        // Po fixie filtr jest server-side: SQL zwraca 0 wierszy -> natychmiast pusta lista (zero enumeracji).
        var r = await fake.QueryOpenReceivablesAsync(Req(min: 999990m), CancellationToken.None);
        Assert.Empty(r);
    }

    // ----------------------------- Controller (HTTP) -----------------------------

    [Fact]
    public async Task Controller_Returns200_WithList()
    {
        var controller = NewController(new FakeSferaSession());
        var r = await controller.OpenReceivables(
            minAmount: null, maxAmount: null, currency: null, contractorId: null, from: null, to: null, limit: 0, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        var list = Assert.IsAssignableFrom<IReadOnlyList<OpenReceivableDto>>(value);
        Assert.NotEmpty(list);
        Assert.All(list, x => Assert.Equal("PLN", x.Currency)); // brak currency w query -> default PLN
    }

    [Fact]
    public async Task Controller_CurrencyNotPln_Returns422()
    {
        // Most rozlicza tylko PLN; waluta obca odrzucona zamiast zwracania mylacych danych (remaining w PLN
        // pod etykieta obcej waluty). Spojnie z UNSUPPORTED_CURRENCY w FS i guardem PLN w /settlements.
        var controller = NewController(new FakeSferaSession());
        var r = await controller.OpenReceivables(
            minAmount: null, maxAmount: null, currency: "EUR", contractorId: null, from: null, to: null, limit: 0, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(422, status);
        Assert.Equal("UNSUPPORTED_CURRENCY", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_WindowAndContractor_PassedThrough()
    {
        var controller = NewController(new FakeSferaSession());
        var r = await controller.OpenReceivables(
            minAmount: 100m, maxAmount: 200m, currency: "PLN", contractorId: 13292, from: null, to: null, limit: 0, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        var item = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<OpenReceivableDto>>(value));
        Assert.Equal(52001, item.DocumentSubiektId); // 120 PLN, kontrahent 13292
    }

    [Fact]
    public async Task Controller_DateWindow_PassedThrough()
    {
        var controller = NewController(new FakeSferaSession());
        var r = await controller.OpenReceivables(
            minAmount: null, maxAmount: null, currency: null, contractorId: null,
            from: "2026-05-01", to: "2026-12-31", limit: 0, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        var list = Assert.IsAssignableFrom<IReadOnlyList<OpenReceivableDto>>(value);
        // Tylko czerwcowe (53447, 53310); kwiecien/marzec poza oknem daty.
        Assert.Equal(2, list.Count);
        Assert.All(list, x => Assert.Contains(x.DocumentSubiektId, new[] { 53447L, 53310L }));
    }
}
