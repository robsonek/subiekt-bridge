using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Incydent 2026-11-09: GET /invoices?from=2026-07-01&amp;to=2026-07-31&amp;type=FS&amp;limit=300 dostał z OtworzKolekcje 2012 dokumentów
/// WSZYSTKICH typów, a most ładował przez COM do 1000 z nich (pełny dokument + Kontrahenci.Wczytaj), sprawdzając typ dopiero
/// po mapowaniu. Klient rozłączył się po 30 s, ale job na STA nie patrzył na anulowanie i mielił dalej - każde kolejne żądanie
/// do Sfery (także /health) czekało za nim w kolejce przez wiele minut. Poprawka: typ FS/KFS filtruje SQL (dok_Typ w filtrze
/// OtworzKolekcje), a pętla enumeracji respektuje anulowanie na granicy dokumentu. RealSferaSession jest windows-only, więc
/// testujemy wydzieloną logikę: plan filtra typu i rdzeń pętli na delegatach.
/// </summary>
public class InvoiceListingScanTests
{
    // ----------------------------- Plan filtra typu -----------------------------

    [Fact]
    public void PlanType_Fs_FiltersInSqlByDokTyp_NoClientPrefix_FetchCapIsLimit()
    {
        var plan = InvoiceQueryFields.PlanType("FS", limit: 300);

        Assert.Equal("dok_Typ = 2", plan.SqlClause);
        Assert.Null(plan.ClientPrefix);
        Assert.Equal(300, plan.FetchCap);
    }

    [Fact]
    public void PlanType_Kfs_IsCaseInsensitiveAndTrimmed()
    {
        var plan = InvoiceQueryFields.PlanType(" kfs ", limit: 50);

        Assert.Equal("dok_Typ = 6", plan.SqlClause);
        Assert.Null(plan.ClientPrefix);
        Assert.Equal(50, plan.FetchCap);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PlanType_NoType_NoSqlClauseNoPrefix_FetchCapIsLimit(string? type)
    {
        var plan = InvoiceQueryFields.PlanType(type, limit: 200);

        Assert.Null(plan.SqlClause);
        Assert.Null(plan.ClientPrefix);
        Assert.Equal(200, plan.FetchCap);
    }

    [Fact]
    public void PlanType_Pz_FiltersInSqlByDokTyp()
    {
        // GET /receipts = ten sam QueryInvoicesAsync z Type "PZ" (ReceiptsController.Query). Bez mapowania PZ wpadało
        // w filtr po prefiksie numeru po wczytaniu KAŻDEGO dokumentu z okna dat (do 1000) - ten sam mechanizm, co incydent FS.
        var plan = InvoiceQueryFields.PlanType("PZ", limit: 300);

        Assert.Equal("dok_Typ = 10", plan.SqlClause);
        Assert.Null(plan.ClientPrefix);
        Assert.Equal(300, plan.FetchCap);
    }

    // Pełna lista z MS_Description kolumny dok_Typ (zrzut schematu 1.88 HF3, dbo.dok__Dokument.sql):
    // {1-FZ,2-FS,3-RZ,4-RS,5-KFZ,6-KFS,9-MM,10-PZ,11-WZ,12-PW,13-RW,14-ZW,15-ZD,16-ZK,21-PA,29-IW,35-ZPZ,36-ZWZ,51-TS,62-FM,67-KFM,72-ZM}
    [Theory]
    [InlineData("FZ", 1)] [InlineData("FS", 2)] [InlineData("RZ", 3)] [InlineData("RS", 4)] [InlineData("KFZ", 5)]
    [InlineData("KFS", 6)] [InlineData("MM", 9)] [InlineData("PZ", 10)] [InlineData("WZ", 11)] [InlineData("PW", 12)]
    [InlineData("RW", 13)] [InlineData("ZW", 14)] [InlineData("ZD", 15)] [InlineData("ZK", 16)] [InlineData("PA", 21)]
    [InlineData("IW", 29)] [InlineData("ZPZ", 35)] [InlineData("ZWZ", 36)] [InlineData("TS", 51)] [InlineData("FM", 62)]
    [InlineData("KFM", 67)] [InlineData("ZM", 72)]
    public void PlanType_EveryDokTypFromSchema_FiltersInSql(string type, int dokTyp)
    {
        var plan = InvoiceQueryFields.PlanType(type.ToLowerInvariant(), limit: 100);

        Assert.Equal($"dok_Typ = {dokTyp}", plan.SqlClause);
        Assert.Null(plan.ClientPrefix);
        Assert.Equal(100, plan.FetchCap);
    }

    [Theory]
    [InlineData(4, "dok_MagId = 4")]
    [InlineData(null, null)]
    public void WarehouseClause_FiltersDokMagId_NullMeansAllVisible(int? warehouse, string? expected)
    {
        // Greptile PR #28: ścieżka awaryjna COM (InvoiceListingViaSql=false) ignorowała warehouse_subiekt_id - żądanie magazynu 4
        // zwracało dokumenty magazynu sesji. dok_MagId to kolumna dok__Dokument, więc idzie do filtra OtworzKolekcje jak dok_Typ.
        Assert.Equal(expected, InvoiceQueryFields.WarehouseClause(warehouse));
    }

    [Fact]
    public void DokTyp_SymbolMap_MatchesConstants()
    {
        Assert.Equal(DokTyp.FS, DokTyp.FromSymbol("FS"));
        Assert.Equal(DokTyp.PZ, DokTyp.FromSymbol("pz"));
        Assert.Null(DokTyp.FromSymbol("XYZ"));
        Assert.Null(DokTyp.FromSymbol(""));
        Assert.Equal(22, DokTyp.BySymbol.Count);
    }

    [Fact]
    public void PlanType_UnknownType_FallsBackToNumberPrefix_WithOversampling()
    {
        // Symbol spoza listy dok_Typ (np. niestandardowy "FH") - zostaje dotychczasowy filtr po prefiksie numeru
        // z nadpróbkowaniem limit*5 (cap 1000), żeby po filtrze nie zabrakło wierszy.
        var plan = InvoiceQueryFields.PlanType("fh", limit: 300);

        Assert.Null(plan.SqlClause);
        Assert.Equal("FH", plan.ClientPrefix);
        Assert.Equal(1000, plan.FetchCap);

        Assert.Equal(250, InvoiceQueryFields.PlanType("FH", limit: 50).FetchCap);
    }

    // ----------------------------- Rdzeń pętli listingu -----------------------------

    private static InvoiceQueryItemDto Item(int id, string number) => new(
        SubiektId: id, Number: number, Type: number.Split(' ', 2)[0], IssueDate: null, ContractorId: null,
        ContractorNip: null, ContractorName: null, NetAmount: null, VatAmount: null, GrossAmount: null, Notes: null);

    /// <summary>Enumerator liczący, ile dokumentów faktycznie pobrano (Real: każdy MoveNext = pełne wczytanie dokumentu przez COM).</summary>
    private sealed class CountingDocs
    {
        public int Pulled;
        public Action<int>? OnPull;

        public IEnumerable<int> Of(int count)
        {
            for (int i = 1; i <= count; i++)
            {
                Pulled++;
                OnPull?.Invoke(i);
                yield return i;
            }
        }
    }

    [Fact]
    public void Collect_CancelledDuringMapping_StopsBeforeLoadingNextDocument_AndClosesCurrent()
    {
        using var cts = new CancellationTokenSource();
        var docs = new CountingDocs();
        var mapped = new List<int>();
        var closed = new List<int>();
        var items = new List<InvoiceQueryItemDto>();

        Assert.Throws<OperationCanceledException>(() => InvoiceListingScan.Collect(
            docs.Of(10), items,
            map: d => { mapped.Add(d); if (d == 3) cts.Cancel(); return Item(d, $"FS {d}/2026"); },
            clientTypePrefix: null, fetchCap: 1000, limit: 300, ct: cts.Token,
            onMapError: _ => { }, close: closed.Add));

        Assert.Equal(new[] { 1, 2, 3 }, mapped);
        Assert.Equal(new[] { 1, 2, 3 }, closed);
        Assert.Equal(3, docs.Pulled); // czwarty dokument nie jest już ładowany przez COM
    }

    [Fact]
    public void Collect_CancelledWhileLoadingDocument_ClosesThatDocumentAndStops()
    {
        using var cts = new CancellationTokenSource();
        var docs = new CountingDocs { OnPull = i => { if (i == 4) cts.Cancel(); } };
        var mapped = new List<int>();
        var closed = new List<int>();
        var items = new List<InvoiceQueryItemDto>();

        Assert.Throws<OperationCanceledException>(() => InvoiceListingScan.Collect(
            docs.Of(10), items,
            map: d => { mapped.Add(d); return Item(d, $"FS {d}/2026"); },
            clientTypePrefix: null, fetchCap: 1000, limit: 300, ct: cts.Token,
            onMapError: _ => { }, close: closed.Add));

        Assert.Equal(new[] { 1, 2, 3 }, mapped);     // załadowany czwarty nie jest mapowany (Kontrahenci.Wczytaj to też COM)
        Assert.Equal(new[] { 1, 2, 3, 4 }, closed);  // ale jest zamykany - bez wycieku RCW
    }

    [Fact]
    public void Collect_AlreadyCancelled_LoadsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var docs = new CountingDocs();
        var items = new List<InvoiceQueryItemDto>();

        Assert.Throws<OperationCanceledException>(() => InvoiceListingScan.Collect(
            docs.Of(10), items, map: d => Item(d, $"FS {d}/2026"),
            clientTypePrefix: null, fetchCap: 1000, limit: 300, ct: cts.Token, onMapError: _ => { }, close: _ => { }));

        Assert.Equal(0, docs.Pulled);
        Assert.Empty(items);
    }

    [Fact]
    public void Collect_LimitReached_DoesNotPullAnotherDocument()
    {
        var docs = new CountingDocs();
        var items = new List<InvoiceQueryItemDto>();

        InvoiceListingScan.Collect(docs.Of(10), items, map: d => Item(d, $"FS {d}/2026"),
            clientTypePrefix: null, fetchCap: 1000, limit: 2, ct: CancellationToken.None, onMapError: _ => { }, close: _ => { });

        Assert.Equal(new long[] { 1, 2 }, items.Select(i => i.SubiektId));
        Assert.Equal(2, docs.Pulled);
    }

    [Fact]
    public void Collect_FetchCapBoundsDocumentsSeen_EvenWhenNothingMatches()
    {
        var docs = new CountingDocs();
        var items = new List<InvoiceQueryItemDto>();

        InvoiceListingScan.Collect(docs.Of(10), items, map: d => Item(d, $"PZ {d}/2026"),
            clientTypePrefix: "FS", fetchCap: 3, limit: 300, ct: CancellationToken.None, onMapError: _ => { }, close: _ => { });

        Assert.Empty(items);
        Assert.Equal(3, docs.Pulled);
    }

    [Fact]
    public void Collect_ClientPrefix_FiltersByNumberPrefix_NullKeepsAll()
    {
        var numbers = new Dictionary<int, string> { [1] = "FS 1/2026", [2] = "PZ 2/2026", [3] = "KFS 3/2026" };
        var filtered = new List<InvoiceQueryItemDto>();
        var all = new List<InvoiceQueryItemDto>();

        InvoiceListingScan.Collect(numbers.Keys, filtered, map: d => Item(d, numbers[d]),
            clientTypePrefix: "FS", fetchCap: 1000, limit: 300, ct: CancellationToken.None, onMapError: _ => { }, close: _ => { });
        InvoiceListingScan.Collect(numbers.Keys, all, map: d => Item(d, numbers[d]),
            clientTypePrefix: null, fetchCap: 1000, limit: 300, ct: CancellationToken.None, onMapError: _ => { }, close: _ => { });

        Assert.Equal(new long[] { 1 }, filtered.Select(i => i.SubiektId));
        Assert.Equal(new long[] { 1, 2, 3 }, all.Select(i => i.SubiektId));
    }

    [Fact]
    public void Collect_MapError_SkipsDocument_ReportsIt_AndStillCloses()
    {
        var errors = new List<Exception>();
        var closed = new List<int>();
        var items = new List<InvoiceQueryItemDto>();

        InvoiceListingScan.Collect(new[] { 1, 2, 3 }, items,
            map: d => d == 2 ? throw new InvalidOperationException("mapping") : Item(d, $"FS {d}/2026"),
            clientTypePrefix: null, fetchCap: 1000, limit: 300, ct: CancellationToken.None,
            onMapError: errors.Add, close: closed.Add);

        Assert.Equal(new long[] { 1, 3 }, items.Select(i => i.SubiektId));
        Assert.Single(errors);
        Assert.Equal("mapping", errors[0].Message);
        Assert.Equal(new[] { 1, 2, 3 }, closed);
    }

    [Fact]
    public void Collect_MapErrorOnLastAllowedDocument_DoesNotPullAnother()
    {
        var docs = new CountingDocs();
        var items = new List<InvoiceQueryItemDto>();

        InvoiceListingScan.Collect(docs.Of(10), items,
            map: d => d == 2 ? throw new InvalidOperationException("mapping") : Item(d, $"FS {d}/2026"),
            clientTypePrefix: null, fetchCap: 2, limit: 300, ct: CancellationToken.None, onMapError: _ => { }, close: _ => { });

        Assert.Equal(new long[] { 1 }, items.Select(i => i.SubiektId));
        Assert.Equal(2, docs.Pulled);
    }

    [Fact]
    public void Collect_MapErrorAfterCancellation_StopsWithoutPullingAnother()
    {
        using var cts = new CancellationTokenSource();
        var docs = new CountingDocs();
        var items = new List<InvoiceQueryItemDto>();

        Assert.Throws<OperationCanceledException>(() => InvoiceListingScan.Collect(docs.Of(10), items,
            map: d => { cts.Cancel(); throw new InvalidOperationException("mapping po rozlaczeniu klienta"); },
            clientTypePrefix: null, fetchCap: 1000, limit: 300, ct: cts.Token, onMapError: _ => { }, close: _ => { }));

        Assert.Equal(1, docs.Pulled);
    }

    // ----------------------------- Fallback Element(i) -----------------------------

    [Fact]
    public void ElementsByIndex_FailingReads_StillHonorCancellation()
    {
        // Greptile PR #26: nieudany Element(i) to też wywołanie COM; seria błędów nie może omijać sprawdzenia tokena,
        // bo iterator nie dochodzi wtedy do pętli Collect i po rozłączeniu klienta job STA dalej odpytuje kolekcję.
        using var cts = new CancellationTokenSource();
        int reads = 0;

        var seq = InvoiceListingScan.ElementsByIndex(take: 1000,
            element: i => { reads++; if (i == 2) cts.Cancel(); throw new InvalidOperationException("read"); },
            ct: cts.Token, onReadError: (_, _) => { });

        Assert.Throws<OperationCanceledException>(() => seq.ToList());
        Assert.Equal(2, reads); // trzeci Element(i) nie jest już wołany
    }

    [Fact]
    public void ElementsByIndex_SkipsFailingAndNullElements_YieldsRest_ReportsErrors()
    {
        var errors = new List<int>();

        var seq = InvoiceListingScan.ElementsByIndex(take: 5,
            element: i => i switch { 2 => throw new InvalidOperationException("read"), 4 => null, _ => (object)i },
            ct: CancellationToken.None, onReadError: (i, _) => errors.Add(i));

        Assert.Equal(new object[] { 1, 3, 5 }, seq.ToList());
        Assert.Equal(new[] { 2 }, errors);
    }

    [Fact]
    public void ElementsByIndex_IsLazy_StopsReadingWhenConsumerStops()
    {
        int reads = 0;

        var first = InvoiceListingScan.ElementsByIndex(take: 1000, element: i => { reads++; return i; },
            ct: CancellationToken.None, onReadError: (_, _) => { }).Take(2).ToList();

        Assert.Equal(2, first.Count);
        Assert.Equal(2, reads); // Collect przerywa po limit/fetchCap - iterator nie może czytać na zapas
    }

    [Fact]
    public void Collect_CancellationIsNotSwallowedAsMappingError()
    {
        // OperationCanceledException z mapowania (np. token sprawdzony w lookupie) ma przerwać pętlę, nie trafić do onMapError.
        using var cts = new CancellationTokenSource();
        var errors = new List<Exception>();
        var items = new List<InvoiceQueryItemDto>();

        Assert.Throws<OperationCanceledException>(() => InvoiceListingScan.Collect(new[] { 1, 2, 3 }, items,
            map: d => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return Item(d, $"FS {d}/2026"); },
            clientTypePrefix: null, fetchCap: 1000, limit: 300, ct: cts.Token, onMapError: errors.Add, close: _ => { }));

        Assert.Empty(errors);
        Assert.Empty(items);
    }
}
