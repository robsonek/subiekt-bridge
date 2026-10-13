using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// v0.21.2: rewizja FS/KFS (`revision` w GET /invoices) - pure część: whitelista kolumn, kanonizacja, skrót, SQL, odczyt wiersza.
/// Spec docs/superpowers/specs/2026-10-13-invoice-revision-fs-kfs-design.md §4-§6 (r2: KSeF przez ksef_NumerKSeF, tabela VAT,
/// dok_CenyTyp, status = tylko unieważnienie, orkiestrator partii). Mutant przy każdym teście.
/// </summary>
public class InvoiceRevisionTests
{
    // ----------------------------- pomocniki -----------------------------

    /// <summary>Wiersz nagłówka z KAŻDYM kluczem whitelisty: napisy "v_&lt;kol&gt;", liczby dla id/typu.</summary>
    internal static Dictionary<string, object?> Header(long id = 101, int typ = DokTyp.FS)
    {
        var h = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in InvoiceRevisionColumns.HeaderKeys) h[key] = "v_" + key;
        h["dok_Id"] = (int)id;
        h["dok_Typ"] = typ;
        h["dok_Status"] = 1;
        return h;
    }

    internal static Dictionary<string, object?> Position(long docId = 101, int obId = 1)
    {
        var p = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in InvoiceRevisionColumns.PositionKeys) p[key] = $"v_{key}_{obId}";
        p[InvoiceRevisionColumns.PositionDocumentKey] = (int)docId;
        p["ob_Id"] = obId;
        return p;
    }

    internal static Dictionary<string, object?> VatRow(long docId = 101, int vatId = 1)
    {
        var v = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in InvoiceRevisionColumns.Vat) v[key] = $"v_{key}_{vatId}";
        v[InvoiceRevisionColumns.VatDocumentKey] = (int)docId;
        v["vt_VatId"] = vatId;
        return v;
    }

    private static readonly Dictionary<string, object?>[] NoVat = Array.Empty<Dictionary<string, object?>>();

    private static string Rev(Dictionary<string, object?> header, params Dictionary<string, object?>[] positions)
        => RevV(header, positions, NoVat);

    private static string RevV(Dictionary<string, object?> header, Dictionary<string, object?>[] positions, Dictionary<string, object?>[] vat)
        => InvoiceRevision.Compute(header, positions, vat) ?? throw new Xunit.Sdk.XunitException("Compute zwrocilo null dla FS/KFS");

    private static object? Changed(object? value) => value switch
    {
        null => "x",
        string s => s + "x",
        int i => i + 1,
        _ => throw new InvalidOperationException($"nieobsluzony typ {value.GetType()}"),
    };

    public static TheoryData<string> HeaderKeysExceptType()
    {
        var d = new TheoryData<string>();
        foreach (var k in InvoiceRevisionColumns.HeaderKeys.Where(k => k != "dok_Typ")) d.Add(k);
        return d;
    }

    public static TheoryData<string> PositionKeys()
    {
        var d = new TheoryData<string>();
        foreach (var k in InvoiceRevisionColumns.PositionKeys) d.Add(k);
        return d;
    }

    public static TheoryData<string> VatKeys()
    {
        var d = new TheoryData<string>();
        foreach (var k in InvoiceRevisionColumns.Vat) d.Add(k);
        return d;
    }

    // ----------------------------- whitelista (spec §4) -----------------------------

    /// <summary>
    /// Mutant: kolumna dopisana/usunięta z listy bez decyzji (także jednocześnie z SELECT-a i skrótu - lista niżej jest NIEZALEŻNA od
    /// produkcyjnej). Lista = spec §4 r2: 64 nagłówka + złączenia (3 adresy historyczne × 13, 2 numery KSeF) + 22 pozycji + 2 kartoteki
    /// + 8 tabeli VAT. Zmiana listy zmienia rewizje WSZYSTKICH dokumentów → nowy prefiks (f2:) i wpis w §9 kontraktu.
    /// </summary>
    [Fact]
    public void Whitelist_is_pinned_to_spec()
    {
        Assert.Equal(new[]
        {
            "dok_Id", "dok_Typ", "dok_Podtyp", "dok_Tytul", "dok_Podtytul", "dok_NrPelny", "dok_NrPelnyOryg", "dok_Status",
            "dok_DataWyst", "dok_DataZakonczenia", "dok_DataMag", "dok_MscWyst", "dok_Wystawil", "dok_Odebral",
            "dok_Uwagi", "dok_UwagiExt", "dok_InformacjeDodatkowe",
            "dok_WartNetto", "dok_WartVat", "dok_WartBrutto", "dok_Waluta", "dok_WalutaKurs", "dok_RabatProc", "dok_CenyTyp",
            "dok_MechanizmPodzielonejPlatnosci", "dok_MetodaKasowa", "dok_ProceduraMarzy", "dok_VatMarza", "dok_FakturaUproszczona",
            "dok_IdPrzyczynyZwolnieniaZVAT", "dok_RodzajOperacjiVat", "dok_FormaDokumentowania",
            "dok_TypNrIdentNabywcy", "dok_NrIdentNabywcy", "dok_KorektaDanychNabywcy", "dok_NrRachunkuBankowegoPdm",
            "dok_NumerKSeF", "dok_NumerKSeFId",
            "dok_PlatId", "dok_PlatTermin", "dok_KartaId", "dok_KredytId", "dok_KwGotowka", "dok_KwKarta", "dok_KwPrzelew",
            "dok_KwKredyt", "dok_KwGotowkaPrzedplata", "dok_KwPrzelewPrzedplata", "dok_KwKartaPrzedplata", "dok_KwReszta",
            "dok_KwDoZaplaty", "dok_KwWartosc",
            "dok_DoDokId", "dok_DoDokNrPelny", "dok_DoDokDataWyst", "dok_DoNumerKSeF", "dok_DoNumerKSeFId",
            "dok_TypDatyUjeciaKorekty", "dok_DataUjeciaKorekty",
            "dok_PlatnikId", "dok_PlatnikAdreshId", "dok_OdbiorcaId", "dok_OdbiorcaAdreshId", "dok_AdresDostawyAdreshId",
        }, InvoiceRevisionColumns.Header);
        string[] address =
        {
            "adrh_Id", "adrh_Nazwa", "adrh_NazwaPelna", "adrh_Telefon", "adrh_Ulica", "adrh_NrDomu", "adrh_NrLokalu", "adrh_Adres",
            "adrh_Kod", "adrh_Miejscowosc", "adrh_Poczta", "adrh_IdPanstwo", "adrh_NIP",
        };
        Assert.Equal(new[]
        {
            ("platnik", "adr_Historia", "adrh_Id", "dok_PlatnikAdreshId", string.Join(",", address)),
            ("odbiorca", "adr_Historia", "adrh_Id", "dok_OdbiorcaAdreshId", string.Join(",", address)),
            ("dostawa", "adr_Historia", "adrh_Id", "dok_AdresDostawyAdreshId", string.Join(",", address)),
            ("ksef", "ksef_NumerKSeF", "ksefnr_Id", "dok_NumerKSeFId", "ksefnr_NumerKSeF"),
            ("doksef", "ksef_NumerKSeF", "ksefnr_Id", "dok_DoNumerKSeFId", "ksefnr_NumerKSeF"),
        }, InvoiceRevisionColumns.Joins.Select(j => (j.Alias, j.Table, j.KeyColumn, j.ForeignKey, string.Join(",", j.Columns))));
        Assert.Equal(new[]
        {
            "ob_Id", "ob_DokHanLp", "ob_TowId", "ob_TowRodzaj", "ob_Opis", "ob_Ilosc", "ob_Jm", "ob_CenaNetto", "ob_CenaBrutto",
            "ob_CenaWaluta", "ob_Rabat", "ob_VatId", "ob_VatProc", "ob_WartNetto", "ob_WartVat", "ob_WartBrutto", "ob_TowPkwiu",
            "ob_TowKodCN", "ob_OznaczenieGTU", "ob_PrzyczynaKorektyId", "ob_Znak", "ob_DoId",
        }, InvoiceRevisionColumns.Position);
        Assert.Equal(new[] { "tw_Nazwa", "tw_Symbol" }, InvoiceRevisionColumns.Product);
        Assert.Equal(new[]
        {
            "vt_VatId", "vt_VatProc", "vt_WartNetto", "vt_WartVat", "vt_WartBrutto", "vt_WartNettoWaluta", "vt_WartVatWaluta", "vt_WartBruttoWaluta",
        }, InvoiceRevisionColumns.Vat);
        Assert.Equal("ob_DokHanId", InvoiceRevisionColumns.PositionDocumentKey);
        Assert.Equal("vt_DokId", InvoiceRevisionColumns.VatDocumentKey);
        Assert.Equal(64 + 3 * 13 + 2, InvoiceRevisionColumns.HeaderKeys.Count);
        Assert.Equal(22 + 2, InvoiceRevisionColumns.PositionKeys.Count);
        Assert.Contains("platnik_adrh_NIP", InvoiceRevisionColumns.HeaderKeys);
        Assert.Contains("ksef_ksefnr_NumerKSeF", InvoiceRevisionColumns.HeaderKeys);
        Assert.Contains("doksef_ksefnr_NumerKSeF", InvoiceRevisionColumns.HeaderKeys);
    }

    // ----------------------------- format i bramka typu -----------------------------

    /// <summary>Mutant: inny prefiks, wielkie litery, inna długość (panel: varchar(32)).</summary>
    [Fact]
    public void Format_is_f1_plus_16_lowercase_hex()
    {
        string rev = Rev(Header(), Position());
        Assert.Matches(new Regex("^f1:[0-9a-f]{16}$"), rev);
        Assert.Equal(19, rev.Length);
    }

    /// <summary>
    /// Mutant: każda zmiana kanonizacji/kolejności/kodowania. Wartość złota: zmiana = zmiana algorytmu → nowy prefiks (f2:) + §9 kontraktu.
    /// </summary>
    [Fact]
    public void Golden_value_pins_the_algorithm()
    {
        var h = Header(id: 7);
        h["dok_WartBrutto"] = 123.45m;
        h["dok_DataWyst"] = new DateTime(2026, 1, 15, 10, 30, 0);
        h["dok_MetodaKasowa"] = false;
        h["dok_Uwagi"] = null;
        h["dok_Status"] = 3;
        var p = Position(docId: 7, obId: 3);
        p["ob_Ilosc"] = 2m;
        p["ob_Znak"] = (short)1;
        var v = VatRow(docId: 7, vatId: 1);
        v["vt_WartVat"] = 23.08m;
        Assert.Equal("f1:00885ccaf66e0207", RevV(h, new[] { p }, new[] { v })); // niezależnie: Python, kodowanie ze specu §5
    }

    /// <summary>
    /// Mutant (Codex r2 #1): sort pozycji/VAT tekstowy zamiast liczbowego (2 &lt; 10), remis stawki VAT bez rozstrzygania treścią,
    /// długość jako bajty UTF-8 albo punkty kodowe zamiast jednostek UTF-16, zaokrąglenie ToEven zamiast AwayFromZero.
    /// Wartość policzona niezależnie (Python: len UTF-16-LE/2, Decimal ROUND_HALF_UP).
    /// </summary>
    [Fact]
    public void Golden_value_pins_ordering_unicode_and_rounding()
    {
        var h = Header(id: 8, typ: DokTyp.KFS);
        h["dok_Uwagi"] = "Zażółć gęślą jaźń \U0001D11E";
        h["dok_WartBrutto"] = 12.34565m;
        h["dok_KwKredyt"] = -0.00005m;
        var p10 = Position(docId: 8, obId: 10); p10["tw_Nazwa"] = "Łódź – nóż";
        var p2 = Position(docId: 8, obId: 2); p2["ob_Ilosc"] = -1.5m;
        var v10 = VatRow(docId: 8, vatId: 10); v10["vt_WartVat"] = 1m;
        var v2a = VatRow(docId: 8, vatId: 2); v2a["vt_WartVat"] = 9m;
        var v2b = VatRow(docId: 8, vatId: 2); v2b["vt_WartVat"] = 1m;
        Assert.Equal("f1:c21f51b0be5d74aa", RevV(h, new[] { p10, p2 }, new[] { v10, v2a, v2b }));
    }

    /// <summary>Mutant (Codex r2 #1): MidpointRounding.ToEven (12.34565 → 12.3456) zamiast AwayFromZero, także dla ujemnych.</summary>
    [Fact]
    public void Decimal_midpoints_round_away_from_zero()
    {
        string Of(decimal v) { var h = Header(); h["dok_WartBrutto"] = v; return Rev(h); }
        Assert.Equal(Of(12.3457m), Of(12.34565m));
        Assert.Equal(Of(-12.3457m), Of(-12.34565m));
        Assert.Equal(Of(0.0001m), Of(0.00005m));
    }

    /// <summary>Mutant: brak bramki typu (rewizja dla PZ/FZ/MM - PZ ma własne r1: w /receipts).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(DokTyp.FZ)]
    [InlineData(5)]
    [InlineData(DokTyp.MM)]
    [InlineData(DokTyp.PZ)]
    public void Non_sales_document_types_have_no_revision(int typ)
    {
        Assert.Null(InvoiceRevision.Compute(Header(typ: typ), new[] { Position() }, NoVat));
    }

    /// <summary>Mutant: bramka tylko na FS (KFS bez rewizji) albo dok_Typ poza skrótem.</summary>
    [Fact]
    public void Fs_and_kfs_both_have_revisions_and_differ_by_type()
    {
        string fs = Rev(Header(typ: DokTyp.FS), Position());
        string kfs = Rev(Header(typ: DokTyp.KFS), Position());
        Assert.NotEqual(fs, kfs);
    }

    // ----------------------------- pokrycie kolumn -----------------------------

    /// <summary>Mutant: kolumna nagłówka/adresu z listy pominięta w skrócie (np. nazwa płatnika, forma płatności, uwagi).</summary>
    [Theory]
    [MemberData(nameof(HeaderKeysExceptType))]
    public void Every_header_column_changes_revision(string key)
    {
        var h = Header();
        string before = Rev(h, Position());
        h[key] = Changed(h[key]);
        Assert.NotEqual(before, Rev(h, Position()));
    }

    /// <summary>Mutant: kolumna pozycji pominięta (np. nazwa towaru z kartoteki, ilość, cena).</summary>
    [Theory]
    [MemberData(nameof(PositionKeys))]
    public void Every_position_column_changes_revision(string key)
    {
        var p = Position();
        string before = Rev(Header(), p);
        p[key] = Changed(p[key]);
        Assert.NotEqual(before, Rev(Header(), p));
    }

    /// <summary>Mutant: skrót po WSZYSTKICH kluczach słownika (status KSeF, status księgowy, licznik wydruku dawałyby fałszywe zmiany - R2).</summary>
    [Fact]
    public void Columns_outside_whitelist_do_not_change_revision()
    {
        var h = Header();
        var p = Position();
        string before = Rev(h, p);
        h["dok_StatusKSeF"] = 4;
        h["dok_StatusKsieg"] = 1;
        h["dok_Rozliczony"] = true;
        p["ob_Status"] = 9;
        p["ob_DokMagId"] = 555;
        Assert.Equal(before, Rev(h, p));
    }

    /// <summary>Mutant: iteracja po słowniku zamiast po liście (kolejność wstawiania zmieniałaby skrót).</summary>
    [Fact]
    public void Dictionary_insertion_order_does_not_matter()
    {
        var h = Header();
        var reversed = new Dictionary<string, object?>(h.Reverse(), StringComparer.Ordinal);
        Assert.Equal(Rev(h, Position()), Rev(reversed, Position()));
    }

    // ----------------------------- pozycje -----------------------------

    /// <summary>Mutant: brak sortowania po ob_Id (kolejność wyniku SQL zmieniałaby rewizję) albo sort tekstowy.</summary>
    [Fact]
    public void Position_order_in_input_does_not_matter()
    {
        var p2 = Position(obId: 2);
        var p10 = Position(obId: 10);
        var p9 = Position(obId: 9);
        Assert.Equal(Rev(Header(), p2, p10, p9), Rev(Header(), p9, p2, p10));
    }

    /// <summary>Mutant: pozycje poza skrótem albo tylko pierwsza pozycja.</summary>
    [Fact]
    public void Adding_or_removing_a_position_changes_revision()
    {
        string one = Rev(Header(), Position(obId: 1));
        string two = Rev(Header(), Position(obId: 1), Position(obId: 2));
        string none = Rev(Header());
        Assert.NotEqual(one, two);
        Assert.NotEqual(one, none);
    }

    /// <summary>Mutant: skrót pozycji jako zbiór wartości bez przypisania do pozycji (zamiana ilości między pozycjami niewidoczna).</summary>
    [Fact]
    public void Swapping_values_between_positions_changes_revision()
    {
        var a = Position(obId: 1); a["ob_Ilosc"] = 1m;
        var b = Position(obId: 2); b["ob_Ilosc"] = 5m;
        string before = Rev(Header(), a, b);
        a["ob_Ilosc"] = 5m; b["ob_Ilosc"] = 1m;
        Assert.NotEqual(before, Rev(Header(), a, b));
    }

    // ----------------------------- kanonizacja (spec §5) -----------------------------

    /// <summary>Mutant: null i "" rozróżniane (przejście NULL↔'' nie zmienia wydruku) albo DBNull jako inny napis.</summary>
    [Fact]
    public void Null_dbnull_and_empty_string_are_equivalent()
    {
        var a = Header(); a["dok_Uwagi"] = null;
        var b = Header(); b["dok_Uwagi"] = "";
        var c = Header(); c["dok_Uwagi"] = DBNull.Value;
        Assert.Equal(Rev(a), Rev(b));
        Assert.Equal(Rev(a), Rev(c));
    }

    /// <summary>Mutant: decimal bez normalizacji skali (money 12.3000 vs 12.3) albo bez zaokrąglenia do 4 dp.</summary>
    [Fact]
    public void Decimals_are_normalized_to_four_places()
    {
        string Of(decimal v) { var h = Header(); h["dok_WartBrutto"] = v; return Rev(h); }
        Assert.Equal(Of(12.3m), Of(12.3000m));
        Assert.Equal(Of(12.3m), Of(12.30001m));
        Assert.NotEqual(Of(12.3m), Of(12.3001m));
    }

    /// <summary>Mutant: data bez czasu albo z precyzją ticków (datetime SQL ma ~3 ms).</summary>
    [Fact]
    public void Dates_keep_time_to_milliseconds()
    {
        string Of(DateTime v) { var h = Header(); h["dok_PlatTermin"] = v; return Rev(h); }
        var t = new DateTime(2026, 2, 1, 0, 0, 0);
        Assert.NotEqual(Of(t), Of(t.AddSeconds(1)));
        Assert.NotEqual(Of(t), Of(t.AddMilliseconds(3)));
        Assert.Equal(Of(t), Of(t.AddTicks(1)));
    }

    /// <summary>Mutant: bool kanonizowany stałą wartością (MPP/metoda kasowa niewidoczne).</summary>
    [Fact]
    public void Booleans_are_distinguished()
    {
        string Of(bool v) { var h = Header(); h["dok_MechanizmPodzielonejPlatnosci"] = v; return Rev(h); }
        Assert.NotEqual(Of(true), Of(false));
    }

    /// <summary>Mutant: liczby całkowite różnych typów CLR dają różne napisy (Real: int/short z SQL, Fake: long).</summary>
    [Fact]
    public void Integer_clr_types_are_equivalent()
    {
        string Of(object v) { var h = Header(); h["dok_PlatId"] = v; return Rev(h); }
        Assert.Equal(Of(5), Of(5L));
        Assert.Equal(Of(5), Of((short)5));
        Assert.Equal(Of(5), Of((byte)5));
    }

    /// <summary>Mutant: kodowanie bez długości (sklejenie "ab"+"c" ≡ "a"+"bc" w sąsiednich kolumnach).</summary>
    [Fact]
    public void Adjacent_values_cannot_be_reshuffled()
    {
        var a = Header(); a["dok_Uwagi"] = "ab"; a["dok_UwagiExt"] = "c";
        var b = Header(); b["dok_Uwagi"] = "a"; b["dok_UwagiExt"] = "bc";
        Assert.NotEqual(Rev(a), Rev(b));
    }

    /// <summary>Mutant: brak kolumny z listy cicho pominięty (fail-open - zła rewizja zamiast null).</summary>
    [Fact]
    public void Missing_whitelisted_column_throws()
    {
        var h = Header(); h.Remove("odbiorca_adrh_NIP");
        Assert.Throws<KeyNotFoundException>(() => InvoiceRevision.Compute(h, new[] { Position() }, NoVat));
        var p = Position(); p.Remove("tw_Nazwa");
        Assert.Throws<KeyNotFoundException>(() => InvoiceRevision.Compute(Header(), new[] { p }, NoVat));
    }

    /// <summary>Mutant: nieznany typ wartości kanonizowany przez ToString() (np. kultura, niestabilny format).</summary>
    [Fact]
    public void Unknown_value_type_throws()
    {
        var h = Header(); h["dok_Uwagi"] = Guid.Empty;
        Assert.Throws<NotSupportedException>(() => InvoiceRevision.Compute(h, NoVat, NoVat));
    }

    // ----------------------------- tabela VAT (r2, Codex r1 #2) -----------------------------

    /// <summary>Mutant: tabela VAT poza skrótem (ręczny VAT per stawka przy niezmienionych sumach nagłówka).</summary>
    [Theory]
    [MemberData(nameof(VatKeys))]
    public void Every_vat_column_changes_revision(string key)
    {
        var v = VatRow();
        string before = RevV(Header(), new[] { Position() }, new[] { v });
        v[key] = Changed(v[key]);
        Assert.NotEqual(before, RevV(Header(), new[] { Position() }, new[] { v }));
    }

    /// <summary>Mutant: przesunięcie grosza VAT między stawkami niewidoczne (sumy nagłówka bez zmian).</summary>
    [Fact]
    public void Moving_vat_between_rates_changes_revision()
    {
        var a = VatRow(vatId: 1); a["vt_WartVat"] = 23.00m;
        var b = VatRow(vatId: 2); b["vt_WartVat"] = 8.00m;
        string before = RevV(Header(), NoVat, new[] { a, b });
        a["vt_WartVat"] = 22.99m; b["vt_WartVat"] = 8.01m;
        Assert.NotEqual(before, RevV(Header(), NoVat, new[] { a, b }));
    }

    /// <summary>Mutant: kolejność wierszy VAT z SQL zmienia skrót albo vt_Id (przepisanie tabelki bez zmiany treści) w skrócie.</summary>
    [Fact]
    public void Vat_rows_are_order_independent_and_ignore_row_id()
    {
        var a = VatRow(vatId: 1); var b = VatRow(vatId: 2);
        string before = RevV(Header(), NoVat, new[] { a, b });
        a["vt_Id"] = 900; b["vt_Id"] = 901;
        Assert.Equal(before, RevV(Header(), NoVat, new[] { b, a }));
    }

    // ----------------------------- status (r2, Codex r1 #6) -----------------------------

    /// <summary>
    /// Mutant: surowy dok_Status w skrócie - realizacja skutku magazynowego (0 wycofany / 1 wykonany / 3 odłożony, CHM
    /// SubiektDokumentStatusEnum) dawałaby masowe odświeżenia bez zmiany wydruku. Liczy się tylko unieważnienie (2).
    /// </summary>
    [Fact]
    public void Status_counts_only_cancellation()
    {
        string Of(int status) { var h = Header(); h["dok_Status"] = status; return Rev(h); }
        Assert.Equal(Of(1), Of(0));
        Assert.Equal(Of(1), Of(3));
        Assert.NotEqual(Of(1), Of(DuplicateScan.StatusAnulowany));
    }

    // ----------------------------- kanonizacja: przypadki brzegowe (Codex r1 #7) -----------------------------

    /// <summary>Mutant: null kanonizowany jak 0 / false (pole puste vs zero na wydruku to różne stany).</summary>
    [Fact]
    public void Null_differs_from_zero_and_false()
    {
        string Of(object? v) { var h = Header(); h["dok_KwKarta"] = v; return Rev(h); }
        Assert.NotEqual(Of(null), Of(0m));
        Assert.NotEqual(Of(null), Of(0));
        Assert.NotEqual(Of(null), Of(false));
    }

    /// <summary>Mutant: Trim/ToLower w kanonizacji (spacja i wielkość liter są na wydruku).</summary>
    [Fact]
    public void Strings_keep_whitespace_and_case()
    {
        string Of(string v) { var p = Position(); p["tw_Nazwa"] = v; return Rev(Header(), p); }
        Assert.NotEqual(Of("Towar"), Of("Towar "));
        Assert.NotEqual(Of("Towar"), Of("towar"));
    }

    /// <summary>Mutant: formatowanie zależne od kultury wątku (separator dziesiętny, kalendarz) - rewizja zależna od ustawień serwera.</summary>
    [Fact]
    public void Revision_does_not_depend_on_current_culture()
    {
        var h = Header(); h["dok_WartBrutto"] = 1234.5m; h["dok_DataWyst"] = new DateTime(2026, 1, 15, 10, 30, 0);
        string invariant = Rev(h);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "pl-PL", "th-TH", "ar-SA", "fa-IR" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Assert.Equal(invariant, Rev(h));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    /// <summary>Mutant: nazwa usługi jednorazowej poza skrótem (TowarId NULL, nazwa w ob_Opis - CHM SuPozycja_TowarId, Codex r1 #8).</summary>
    [Fact]
    public void One_off_service_name_change_is_visible()
    {
        var p = Position(); p["ob_TowId"] = null; p["tw_Nazwa"] = null; p["tw_Symbol"] = null; p["ob_Opis"] = "Usługa montażu";
        string before = Rev(Header(), p);
        p["ob_Opis"] = "Usługa montażu i transportu";
        Assert.NotEqual(before, Rev(Header(), p));
    }

    // ----------------------------- ComputeAll -----------------------------

    /// <summary>Mutant: pozycje/VAT przypisane do złego dokumentu (brak grupowania) albo rewizja dla id bez nagłówka.</summary>
    [Fact]
    public void ComputeAll_groups_positions_and_vat_by_document()
    {
        var h1 = Header(id: 1); var h2 = Header(id: 2); var hPz = Header(id: 3, typ: DokTyp.PZ);
        var p1 = Position(docId: 1, obId: 10); var p2a = Position(docId: 2, obId: 20); var p2b = Position(docId: 2, obId: 21);
        var orphan = Position(docId: 99, obId: 990);
        var v1 = VatRow(docId: 1, vatId: 1); var v2 = VatRow(docId: 2, vatId: 1); var v2b = VatRow(docId: 2, vatId: 2);

        var all = InvoiceRevision.ComputeAll(new InvoiceRevisionRows(new[] { h1, h2, hPz }, new[] { p2b, orphan, p1, p2a }, new[] { v2b, v1, v2 }));

        Assert.Equal(InvoiceRevision.Compute(h1, new[] { p1 }, new[] { v1 }), all[1]);
        Assert.Equal(InvoiceRevision.Compute(h2, new[] { p2a, p2b }, new[] { v2, v2b }), all[2]);
        Assert.Null(all[3]);
        Assert.False(all.ContainsKey(99));
        Assert.Equal(3, all.Count);
    }

    // ----------------------------- orkiestrator partii (Codex r1 #7) -----------------------------

    private static InvoiceRevisionRows RowsFor(IEnumerable<long> ids)
        => new(ids.Select(id => (IReadOnlyDictionary<string, object?>)Header(id: id)).ToList(),
               ids.Select(id => (IReadOnlyDictionary<string, object?>)Position(docId: id)).ToList(),
               Array.Empty<IReadOnlyDictionary<string, object?>>());

    /// <summary>Mutant: partia większa niż BatchSize (limit 2100 parametrów MSSQL) albo zgubiona końcówka listy (501. id).</summary>
    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(500, new[] { 500 })]
    [InlineData(501, new[] { 500, 1 })]
    [InlineData(1000, new[] { 500, 500 })]
    public async Task ReadAll_splits_ids_into_batches(int count, int[] expectedBatches)
    {
        var ids = Enumerable.Range(1, count).Select(i => (long)i).ToList();
        var batches = new List<int>();

        var all = await InvoiceRevision.ReadAllAsync(ids, (batch, _) => { batches.Add(batch.Length); return Task.FromResult(RowsFor(batch)); }, CancellationToken.None);

        Assert.Equal(expectedBatches, batches);
        Assert.Equal(count, all.Count);
        Assert.All(ids, id => Assert.StartsWith(InvoiceRevision.Prefix, all[id]));
    }

    /// <summary>Mutant: awaria drugiej partii połknięta - częściowy wynik (część dokumentów z rewizją, reszta bez) zamiast wyjątku.</summary>
    [Fact]
    public async Task ReadAll_failure_in_any_batch_discards_everything()
    {
        var ids = Enumerable.Range(1, 501).Select(i => (long)i).ToList();
        int calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvoiceRevision.ReadAllAsync(ids, (batch, _) =>
            ++calls == 2 ? throw new InvalidOperationException("druga partia") : Task.FromResult(RowsFor(batch)), CancellationToken.None));
        Assert.Equal(2, calls);
    }

    /// <summary>Mutant: zduplikowane id w kilku partiach / puste wywołanie SQL dla pustej listy.</summary>
    [Fact]
    public async Task ReadAll_deduplicates_and_skips_empty()
    {
        var batches = new List<long[]>();
        Task<InvoiceRevisionRows> Read(long[] b, CancellationToken _) { batches.Add(b); return Task.FromResult(RowsFor(b)); }

        Assert.Empty(await InvoiceRevision.ReadAllAsync(Array.Empty<long>(), Read, CancellationToken.None));
        Assert.Empty(batches);
        await InvoiceRevision.ReadAllAsync(new long[] { 5, 5, 6 }, Read, CancellationToken.None);
        Assert.Equal(new long[] { 5, 6 }, Assert.Single(batches));
    }

    /// <summary>Mutant: anulowanie ignorowane przed pierwszą partią.</summary>
    [Fact]
    public async Task ReadAll_honours_cancellation_before_first_batch()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvoiceRevision.ReadAllAsync(new long[] { 1 }, (b, _) => Task.FromResult(RowsFor(b)), cts.Token));
    }

    /// <summary>Mutant (Codex r2 #1): anulowanie w trakcie pierwszej partii ignorowane - druga partia (kolejne polecenie SQL) i tak rusza.</summary>
    [Fact]
    public async Task ReadAll_stops_between_batches_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var ids = Enumerable.Range(1, 501).Select(i => (long)i).ToList();
        int calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvoiceRevision.ReadAllAsync(ids, (b, _) =>
        {
            calls++;
            cts.Cancel();
            return Task.FromResult(RowsFor(b));
        }, cts.Token));
        Assert.Equal(1, calls);
    }

    // ----------------------------- adapter czytnika (Codex r2 #1: logika adaptera Real bez SQL) -----------------------------

    private static DataTable Table(string column, params object[] values)
    {
        var t = new DataTable();
        t.Columns.Add(column, values.Length > 0 ? values[0].GetType() : typeof(int));
        foreach (var v in values) t.Rows.Add(v);
        return t;
    }

    /// <summary>Mutant: zestawy wyników przypisane w złej kolejności (nagłówek/pozycje/VAT) albo zgubiony wiersz zestawu.</summary>
    [Fact]
    public async Task ReadSets_maps_three_result_sets_in_order()
    {
        var ds = new DataSet();
        ds.Tables.Add(Table("dok_Id", 1, 2));
        ds.Tables.Add(Table("ob_Id", 10));
        ds.Tables.Add(Table("vt_VatId", 7, 8, 9));
        using var reader = ds.CreateDataReader();

        var rows = await InvoiceRevision.ReadResultSetsAsync(reader, CancellationToken.None);

        Assert.Equal(new object?[] { 1, 2 }, rows.Headers.Select(r => r["dok_Id"]));
        Assert.Equal(10, Assert.Single(rows.Positions)["ob_Id"]);
        Assert.Equal(new object?[] { 7, 8, 9 }, rows.Vat.Select(r => r["vt_VatId"]));
    }

    /// <summary>Mutant: brak kontroli liczby zestawów (2 = VAT cicho pusty → zła rewizja; 4 = nieznany kształt polecenia).</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ReadSets_requires_exactly_three_result_sets(int sets)
    {
        var ds = new DataSet();
        for (int i = 0; i < sets; i++) ds.Tables.Add(Table("c" + i, i));
        using var reader = ds.CreateDataReader();

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvoiceRevision.ReadResultSetsAsync(reader, CancellationToken.None));
    }

    // ----------------------------- SQL (spec §6.2) -----------------------------

    /// <summary>Mutant: kolumna z listy nieobecna w SELECT (KeyNotFound na prod), brak złączenia KSeF/adresu albo SELECT-a tabeli VAT.</summary>
    [Fact]
    public void Sql_selects_every_whitelisted_column()
    {
        string sql = InvoiceRevisionSql.Build(3);
        foreach (var c in InvoiceRevisionColumns.Header) Assert.Contains($"d.[{c}]", sql);
        foreach (var j in InvoiceRevisionColumns.Joins)
        {
            Assert.Contains($"LEFT JOIN {j.Table} {j.Alias} ON {j.Alias}.{j.KeyColumn} = d.{j.ForeignKey}", sql);
            foreach (var c in j.Columns) Assert.Contains($"{j.Alias}.[{c}] AS [{j.Alias}_{c}]", sql);
        }
        Assert.Contains($"p.[{InvoiceRevisionColumns.PositionDocumentKey}]", sql);
        foreach (var c in InvoiceRevisionColumns.Position) Assert.Contains($"p.[{c}]", sql);
        foreach (var c in InvoiceRevisionColumns.Product) Assert.Contains($"t.[{c}]", sql);
        Assert.Contains("LEFT JOIN tw__Towar t ON t.tw_Id = p.ob_TowId", sql);
        Assert.Contains($"v.[{InvoiceRevisionColumns.VatDocumentKey}]", sql);
        foreach (var c in InvoiceRevisionColumns.Vat) Assert.Contains($"v.[{c}]", sql);
        Assert.Contains("FROM dok_Vat v", sql);
    }

    /// <summary>Mutant: parametry sklejone z wartościami / zła liczba parametrów / brak filtra typu w którymś SELECT.</summary>
    [Fact]
    public void Sql_uses_parameters_and_type_filter_in_every_select()
    {
        string sql = InvoiceRevisionSql.Build(3);
        Assert.Equal(3, Regex.Matches(sql, @"IN \(@d0, @d1, @d2\)").Count);
        Assert.DoesNotContain("@d3", sql);
        Assert.Equal(3, Regex.Matches(sql, @"\bSELECT\b").Count);
        Assert.Equal(3, Regex.Matches(sql, @"dok_Typ IN \(2, 6\)").Count);
        Assert.Contains("WHERE p.ob_DokHanId IN (", sql);
        Assert.Contains("WHERE v.vt_DokId IN (", sql);
        Assert.Equal("@d4", InvoiceRevisionSql.ParameterName(4));
    }

    /// <summary>
    /// Mutant: trzecia instrukcja zakomentowana/sklejona (Real dostałby 2 zestawy wyników) albo zmieniona kolejność zestawów
    /// (nagłówek / pozycje / VAT - adapter Real czyta je pozycyjnie).
    /// </summary>
    [Fact]
    public void Sql_has_three_statements_in_reader_order()
    {
        string sql = InvoiceRevisionSql.Build(2);
        Assert.DoesNotContain("--", sql);
        Assert.DoesNotContain("/*", sql);
        var statements = sql.Split(";\n");
        Assert.Equal(3, statements.Length);
        Assert.StartsWith("SELECT d.[dok_Id]", statements[0]);
        Assert.Contains("FROM dok__Dokument d ", statements[0]);
        Assert.StartsWith($"SELECT p.[{InvoiceRevisionColumns.PositionDocumentKey}]", statements[1]);
        Assert.Contains("FROM dok_Pozycja p ", statements[1]);
        Assert.StartsWith($"SELECT v.[{InvoiceRevisionColumns.VatDocumentKey}]", statements[2]);
        Assert.EndsWith("WHERE v.vt_DokId IN (@d0, @d1);", statements[2]);
    }

    /// <summary>Mutant: brak limitu partii (limit 2100 parametrów MSSQL) albo zapytanie z pustym IN ().</summary>
    [Fact]
    public void Sql_rejects_empty_and_oversized_batches()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceRevisionSql.Build(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceRevisionSql.Build(InvoiceRevisionSql.BatchSize + 1));
        Assert.True(InvoiceRevisionSql.BatchSize < 2100);
        _ = InvoiceRevisionSql.Build(InvoiceRevisionSql.BatchSize);
    }

    // ----------------------------- odczyt wiersza (adapter Real) -----------------------------

    /// <summary>Mutant: DBNull przepuszczony jako obiekt (null ≢ DBNull w kanonizacji) albo nazwy kolumn bez aliasu.</summary>
    [Fact]
    public void ReadRow_maps_names_and_dbnull()
    {
        var table = new DataTable();
        table.Columns.Add("dok_Id", typeof(int));
        table.Columns.Add("platnik_adrh_NIP", typeof(string));
        table.Columns.Add("dok_WartBrutto", typeof(decimal));
        table.Rows.Add(5, DBNull.Value, 1.5m);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());

        var row = InvoiceRevision.ReadRow(reader);

        Assert.Equal(5, row["dok_Id"]);
        Assert.True(row.ContainsKey("platnik_adrh_NIP"));
        Assert.Null(row["platnik_adrh_NIP"]);
        Assert.Equal(1.5m, row["dok_WartBrutto"]);
    }
}
