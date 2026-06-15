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
        string? from = null, string? to = null, int limit = 50, string? search = null)
        => new(min, max, currency, contractorId, from, to, limit, search);

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

        // 4 PLN naleznosci (53447, 53310, 52001, 53500); rekord EUR (51900) odfiltrowany przez default currency=PLN.
        Assert.Equal(4, r.Count);
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

    // ----------------------------- Wyszukiwarka FV (v0.13.0) -----------------------------

    [Theory]
    [InlineData(null, true)]                                  // brak frazy = pasuje wszystko
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("szyszka", true)]                             // nazwa (case-insensitive)
    [InlineData("SZYSZKA", true)]
    [InlineData("573", true)]                                 // numer
    [InlineData("1234563218", true)]                          // NIP
    [InlineData("zzz-nie-ma", false)]
    public void MatchesSearch_NumberNameNip(string? search, bool expected)
    {
        Assert.Equal(expected, OpenReceivableFields.MatchesSearch(search, "FS 573/05/2026", "Szyszka Krzysztof", "1234563218"));
    }

    [Fact]
    public void MatchesSearch_NullNip_DoesNotMatchNipQuery()
    {
        // B2C bez NIP: fraza wygladajaca na NIP nie moze trafic w null.
        Assert.False(OpenReceivableFields.MatchesSearch("1234563218", "FS 540/05/2026", "Google Commerce Limited", null));
        Assert.True(OpenReceivableFields.MatchesSearch("google", "FS 540/05/2026", "Google Commerce Limited", null));
    }

    [Theory]
    [InlineData("Szyszka", "Szyszka")]      // zwykla fraza - bez zmian
    [InlineData("50%", "50[%]")]            // % jako literal (inaczej = wszyscy kontrahenci)
    [InlineData("A_B", "A[_]B")]            // _ jako literal
    [InlineData("Sp[ar]", "Sp[[]ar]")]      // [ neutralizowane PIERWSZE (kolejnosc!)
    [InlineData("", "")]
    public void EscapeLikeWildcards_NeutralizesLikeMetachars(string raw, string expected)
    {
        // Parametr-safe (NIE dubluje apostrofu jak EscapeLikePattern) - dla LIKE @s w FindOpenReceivableContractorIds.
        Assert.Equal(expected, OpenReceivableFields.EscapeLikeWildcards(raw));
    }

    [Fact]
    public async Task Search_FiltersByContractorName()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenReceivablesAsync(Req(search: "szyszka"), CancellationToken.None);

        Assert.Equal(2, r.Count); // 53447 + 52001 (oba Szyszka, PLN); 51900 EUR odpada
        Assert.All(r, x => Assert.Equal("Szyszka Krzysztof", x.ContractorName));
    }

    [Fact]
    public async Task Search_FiltersByInvoiceNumber()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenReceivablesAsync(Req(search: "573"), CancellationToken.None);

        var only = Assert.Single(r);
        Assert.Equal(53447, only.DocumentSubiektId);
    }

    [Fact]
    public async Task Search_FiltersByNip_CaseInsensitive()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenReceivablesAsync(Req(search: "GOOGLE"), CancellationToken.None);

        var only = Assert.Single(r);
        Assert.Equal(53310, only.DocumentSubiektId);
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsEmpty()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenReceivablesAsync(Req(search: "nie-istnieje-xyz"), CancellationToken.None);
        Assert.Empty(r);
    }

    [Fact]
    public async Task Search_ContractorScope_TakesPrecedenceOverNumber()
    {
        // Semantyka v0.14.0 (perf fix): gdy fraza pasuje do KONTRAHENTA (SQL nazwa/NIP), zwracamy wiersze
        // tego kontrahenta - number-path NIE jest mieszany. "410" pasuje do numeru FS 410/04/2026 (52001,
        // Szyszka), ale rowniez do NIP "1234563218"? nie. Tu sprawdzamy odwrotnie: fraza pasujaca do nazwy
        // kontrahenta zaweza po kontrahencie, a nie zwraca dodatkowo wierszy obcych o numerze z ta fraza.
        var fake = new FakeSferaSession();

        // "Szyszka" -> scope = kontrahent 13292 -> tylko jego 2 wiersze (53447, 52001), nie inne.
        var r = await fake.QueryOpenReceivablesAsync(Req(search: "Szyszka"), CancellationToken.None);
        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Equal(13292, x.ContractorId));

        // Twardy dowod precedencji: wiersz 53500 to OBCY kontrahent (16100 "Nowak Anna", nie pasuje do
        // "Szyszka"), ALE jego numer FV zawiera "szyszka". Gdyby number-path byl OR-owany ze scope (a nie
        // wykluczony), 53500 by wrocil. Asercja, ze NIE wraca, lapie regresje OR-mieszania scope+number.
        Assert.DoesNotContain(r, x => x.DocumentSubiektId == 53500);
    }

    [Fact]
    public async Task Controller_SearchPassedThrough()
    {
        var controller = NewController(new FakeSferaSession());
        var r = await controller.OpenReceivables(
            minAmount: null, maxAmount: null, currency: null, contractorId: null,
            from: null, to: null, limit: 0, search: "szyszka", CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        var list = Assert.IsAssignableFrom<IReadOnlyList<OpenReceivableDto>>(value);
        Assert.Equal(2, list.Count);
        Assert.All(list, x => Assert.Equal("Szyszka Krzysztof", x.ContractorName));
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
        // Tylko dolna granica - bez max (decimal? = null) zwraca wszystko >= 200 PLN (53447, 53310, 53500=250;
        // NIE 52001=120).
        var r = await fake.QueryOpenReceivablesAsync(Req(min: 200m), CancellationToken.None);

        Assert.Equal(3, r.Count);
        Assert.DoesNotContain(r, x => x.DocumentSubiektId == 52001);
    }

    [Fact]
    public async Task DateFrom_NarrowsToRecent()
    {
        var fake = new FakeSferaSession();
        // from=2026-05-01 -> tylko czerwcowe (53447, 53310, 53500); 52001 (kwiecien) i 51900 (marzec) odpadaja.
        // Klient poda okno daty wokol przelewu - swiezy przelew -> swieza FV, dodatkowo tnie skan.
        var r = await fake.QueryOpenReceivablesAsync(Req(from: "2026-05-01"), CancellationToken.None);

        Assert.Equal(3, r.Count);
        Assert.All(r, x => Assert.Contains(x.DocumentSubiektId, new[] { 53447L, 53310L, 53500L }));
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
            minAmount: null, maxAmount: null, currency: null, contractorId: null, from: null, to: null, limit: 0, search: null, CancellationToken.None);

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
            minAmount: null, maxAmount: null, currency: "EUR", contractorId: null, from: null, to: null, limit: 0, search: null, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(422, status);
        Assert.Equal("UNSUPPORTED_CURRENCY", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_WindowAndContractor_PassedThrough()
    {
        var controller = NewController(new FakeSferaSession());
        var r = await controller.OpenReceivables(
            minAmount: 100m, maxAmount: 200m, currency: "PLN", contractorId: 13292, from: null, to: null, limit: 0, search: null, CancellationToken.None);

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
            from: "2026-05-01", to: "2026-12-31", limit: 0, search: null, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        var list = Assert.IsAssignableFrom<IReadOnlyList<OpenReceivableDto>>(value);
        // Tylko czerwcowe (53447, 53310, 53500); kwiecien/marzec poza oknem daty.
        Assert.Equal(3, list.Count);
        Assert.All(list, x => Assert.Contains(x.DocumentSubiektId, new[] { 53447L, 53310L, 53500L }));
    }
}
