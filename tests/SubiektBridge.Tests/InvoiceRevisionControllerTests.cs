using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// v0.21.2: `revision` w GET /invoices/{id} i GET /invoices (spec §2, §6-§7). Fake modeluje wiersze SQL dokumentu i liczy przez
/// InvoiceRevision.ComputeAll (jak Real); kontroler dokleja rewizję fail-soft. Haki Fake są instancyjne (bez stanu współdzielonego).
/// Jeden plik SQLite idempotencji na przypadek testowy, kasowany w Dispose (Codex review kodu #2). Mutant przy każdym teście.
/// </summary>
public sealed class InvoiceRevisionControllerTests : IDisposable
{
    private const long Id = 50_001;
    private const string Format = "^f1:[0-9a-f]{16}$";

    private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"idem_invoice_revision_{Guid.NewGuid():N}.db");
    private IdempotencyStore? _store;

    private IdempotencyStore Store => _store ??= new(new BridgeOptions
    {
        IdempotencyStorePath = _storePath,
        IdempotencyTtlDays = 30,
    }, NullLogger<IdempotencyStore>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _storePath, _storePath + "-wal", _storePath + "-shm", _storePath + "-journal" })
        {
            try { File.Delete(file); } catch (IOException) { /* best-effort: plik tymczasowy */ }
        }
    }

    private InvoicesController Controller(FakeSferaSession fake)
        => new(fake, Store, NullLogger<InvoicesController>.Instance);

    private static T Ok<T>(ActionResult<T> result)
        => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value, exactMatch: false);

    private async Task<InvoiceQueryItemDto> Get(FakeSferaSession fake, long id = Id)
        => Ok(await Controller(fake).Get($"sub_{id}", CancellationToken.None));

    /// <summary>Rewizja GET wymagana jako f1: - porównania równości/nierówności nie mogą przejść na null (Codex review kodu #1).</summary>
    private async Task<string> Revision(FakeSferaSession fake, long id = Id)
    {
        string? revision = (await Get(fake, id)).Revision;
        Assert.Matches(Format, revision);
        return revision!;
    }

    private async Task<IReadOnlyList<InvoiceQueryItemDto>> List(FakeSferaSession fake)
        => Ok(await Controller(fake).Query(null, null, null, null, null, 0, CancellationToken.None));

    // ----------------------------- GET /invoices/{id} -----------------------------

    /// <summary>Mutant: kontroler nie dokleja rewizji albo zmienia pozostałe pola DTO (zgodność wstecz R5).</summary>
    [Fact]
    public async Task Get_returns_revision_and_keeps_other_fields()
    {
        var fake = new FakeSferaSession();
        var dto = await Get(fake);

        Assert.Matches(Format, dto.Revision);
        var plain = await fake.FindInvoiceByIdAsync(Id, CancellationToken.None);
        Assert.Equal(plain, dto with { Revision = null });
    }

    /// <summary>Mutant: rewizja zależna od czasu/licznika odczytów (R2: sam odczyt nie zmienia rewizji).</summary>
    [Fact]
    public async Task Get_revision_is_stable_across_reads()
    {
        var fake = new FakeSferaSession();
        Assert.Equal(await Revision(fake), await Revision(fake));
    }

    public static TheoryData<string> DocumentChanges() => new()
    {
        "nazwa_towaru", "forma_platnosci", "ilosc", "nabywca_historyczny", "numer_ksef", "numer_ksef_z_tabeli", "uwagi",
        "tabela_vat", "ceny_od_brutto", "uniewaznienie",
    };

    /// <summary>
    /// Mutant: rewizja z metadanych zamiast z treści (zmiana nazwy pozycji i formy płatności nie ruszają kwot - przypadki panelu z 13.10).
    /// </summary>
    [Theory]
    [MemberData(nameof(DocumentChanges))]
    public async Task Get_revision_changes_when_printed_content_changes(string change)
    {
        var fake = new FakeSferaSession();
        string before = await Revision(fake);

        fake.MutateInvoiceForTests(Id, (h, positions, vat) =>
        {
            switch (change)
            {
                case "numer_ksef_z_tabeli": h["dok_NumerKSeFId"] = 7; h["ksef_ksefnr_NumerKSeF"] = "1111111111-20260115-0000000000-00"; break;
                case "tabela_vat": vat[0]["vt_WartVat"] = 18.69m; break;
                case "ceny_od_brutto": h["dok_CenyTyp"] = 0; break;
                case "uniewaznienie": h["dok_Status"] = DuplicateScan.StatusAnulowany; break;
                case "nazwa_towaru": positions[0]["tw_Nazwa"] = "Towar testowy (nowa nazwa)"; break;
                case "forma_platnosci": h["dok_KwGotowka"] = 0m; h["dok_KwKredyt"] = 100m; h["dok_PlatTermin"] = new DateTime(2026, 2, 1); break;
                case "ilosc": positions[0]["ob_Ilosc"] = 2m; break;
                case "nabywca_historyczny": h["platnik_adrh_Nazwa"] = "Mock Klient (nowe dane)"; break;
                case "numer_ksef": h["dok_NumerKSeF"] = "1111111111-20260115-0000000000-00"; break;
                case "uwagi": h["dok_Uwagi"] = "fake (poprawione)"; break;
            }
        });

        Assert.NotEqual(before, await Revision(fake));
    }

    /// <summary>Mutant: surowy dok_Status w skrócie - realizacja skutku magazynowego (odłożony → wykonany) odświeżałaby PDF masowo.</summary>
    [Fact]
    public async Task Get_stock_realization_does_not_change_revision()
    {
        var fake = new FakeSferaSession();
        fake.MutateInvoiceForTests(Id, (h, _) => h["dok_Status"] = 3);
        string before = await Revision(fake);
        fake.MutateInvoiceForTests(Id, (h, _) => h["dok_Status"] = 1);
        Assert.Equal(before, await Revision(fake));
    }

    /// <summary>Mutant: brak wiersza w SQL (dokument usunięty między odczytem COM a SQL) daje rewizję zamiast null.</summary>
    [Fact]
    public async Task Get_document_missing_in_sql_has_null_revision()
    {
        var fake = new FakeSferaSession();
        var dto = await Get(fake, FakeSferaSession.NotFoundIdForTests);
        Assert.Null(dto.Revision);
        Assert.Equal(1, fake.InvoiceRevisionReadCountForTests);
    }

    /// <summary>Mutant: rewizja dla dokumentu spoza FS/KFS (PZ ma r1: w /receipts - dwie różne rewizje tego samego PZ).</summary>
    [Fact]
    public async Task Get_non_sales_document_has_null_revision()
    {
        var fake = new FakeSferaSession();
        fake.MutateInvoiceForTests(Id, (h, _) => h["dok_Typ"] = DokTyp.PZ);
        Assert.Null((await Get(fake)).Revision);
    }

    /// <summary>Mutant: KFS bez rewizji (bramka tylko FS).</summary>
    [Fact]
    public async Task Get_correction_has_revision()
    {
        var fake = new FakeSferaSession();
        fake.MutateInvoiceForTests(Id, (h, _) => h["dok_Typ"] = DokTyp.KFS);
        await Revision(fake);
    }

    /// <summary>Mutant: awaria SQL rewizji psuje GET (500/502) zamiast revision=null (fail-soft, spec §7).</summary>
    [Fact]
    public async Task Get_revision_read_failure_is_soft()
    {
        var fake = new FakeSferaSession { InvoiceRevisionReadFailsForTests = true };
        var dto = await Get(fake);

        Assert.Null(dto.Revision);
        Assert.Equal(await fake.FindInvoiceByIdAsync(Id, CancellationToken.None), dto);
    }

    /// <summary>Mutant: rewizja czytana przed odczytem COM albo przy 503 (zbędna runda SQL, 503 maskowane).</summary>
    [Fact]
    public async Task Get_unavailable_session_returns_503_without_reading_revision()
    {
        var fake = new FakeSferaSession { SferaUnavailableForTests = true };
        var result = await Controller(fake).Get($"sub_{Id}", CancellationToken.None);

        Assert.Equal(503, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal(0, fake.InvoiceRevisionReadCountForTests);
    }

    /// <summary>Mutant: catch-all połyka anulowanie żądania (OCE zamienione na revision=null).</summary>
    [Fact]
    public async Task Get_cancellation_propagates()
    {
        var fake = new FakeSferaSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller(fake).Get($"sub_{Id}", cts.Token));
    }

    // ----------------------------- GET /invoices -----------------------------

    /// <summary>Mutant: lista bez rewizji albo z innego źródła niż GET (panel porównuje rewizje z obu endpointów).</summary>
    [Fact]
    public async Task List_items_have_the_same_revision_as_get()
    {
        var fake = new FakeSferaSession();
        var items = await List(fake);

        Assert.NotEmpty(items);
        foreach (var item in items)
        {
            Assert.Matches(Format, item.Revision);
            Assert.Equal(await Revision(fake, item.SubiektId), item.Revision);
        }
    }

    /// <summary>Mutant: N+1 - osobny odczyt SQL na każdy element listy.</summary>
    [Fact]
    public async Task List_reads_revisions_once_per_call()
    {
        var fake = new FakeSferaSession();
        var items = await List(fake);

        Assert.True(items.Count > 1);
        Assert.Equal(1, fake.InvoiceRevisionReadCountForTests);
    }

    /// <summary>Mutant: anulowanie podczas odczytu rewizji listy połknięte (revision=null) albo zamienione na 502.</summary>
    [Fact]
    public async Task List_cancellation_during_revision_read_propagates()
    {
        var fake = new FakeSferaSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller(fake).Query(null, null, null, null, null, 0, cts.Token));
    }

    /// <summary>Mutant: rewizja czytana w ścieżce replayu idempotencji (FindInvoiceByIdAsync) - zbędna zależność POST od SqlClienta.</summary>
    [Fact]
    public async Task Idempotent_replay_does_not_read_revisions()
    {
        var fake = new FakeSferaSession();
        var controller = Controller(fake);
        var request = new InvoiceRequestDto(
            Type: "FS", IssueDate: "", SaleDate: "", Payment: new PaymentDto("PlatnoscPrzelew", null, 100m, true), Currency: "PLN",
            Contractor: new ContractorDto(true, "K1", null, "Test Klient", null, null, null, null, new AddressDto("ul. Testowa 1", "00-001", "Warszawa", "PL")),
            Lines: new[] { new LineDto("5901234123457", "Towar testowy", 1m, "szt.", 100m, 23m) },
            Shipping: new ShippingDto(false, "Wysyłka", 0m, 23m), Totals: new InvoiceTotalsDto(Gross: 100m),
            ExternalReference: $"test:{Guid.NewGuid():N}", Notes: "");
        string key = Guid.NewGuid().ToString();

        var first = await controller.Create(request, key, CancellationToken.None);
        var replay = await controller.Create(request, key, CancellationToken.None);

        Assert.Equal(201, Assert.IsAssignableFrom<ObjectResult>(first.Result).StatusCode);
        Assert.Equal(200, Assert.IsAssignableFrom<ObjectResult>(replay.Result).StatusCode ?? 200);
        Assert.Equal(0, fake.InvoiceRevisionReadCountForTests);
    }

    /// <summary>Mutant: awaria rewizji psuje listę (502) zamiast null w każdym elemencie.</summary>
    [Fact]
    public async Task List_revision_read_failure_is_soft()
    {
        var fake = new FakeSferaSession { InvoiceRevisionReadFailsForTests = true };
        var items = await List(fake);
        var plain = await fake.QueryInvoicesAsync(new InvoiceQueryRequestDto(null, null, null, null, null, 200), CancellationToken.None);

        Assert.Equal(plain, items);
        Assert.All(items, i => Assert.Null(i.Revision));
    }

    // ----------------------------- serializacja -----------------------------

    /// <summary>Mutant: JsonIgnore(WhenWritingNull) - klient nie odróżniłby „brak rewizji” od starego mostu (kontrakt: pole zawsze obecne).</summary>
    [Fact]
    public void Revision_key_is_serialized_even_when_null()
    {
        var dto = new InvoiceQueryItemDto(1, "FS 1/2026", "FS", "2026-01-15", 1, null, "Mock", 1m, 0m, 1m, null);
        Assert.Contains("\"revision\":null", JsonSerializer.Serialize(dto));
        Assert.Contains("\"revision\":\"f1:0123456789abcdef\"", JsonSerializer.Serialize(dto with { Revision = "f1:0123456789abcdef" }));
    }
}
