using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SubiektBridge.Api.Sfera;

// Rewizja FS/KFS (v0.21.2): znacznik treści, która trafia na wydruk PDF. Logika pure, testowalna bez COM i bez SqlClienta.
// Spec: docs/superpowers/specs/2026-10-13-invoice-revision-fs-kfs-design.md §4-§6. Real (SQL) i Fake liczą TĄ SAMĄ funkcją.

/// <summary>
/// Whitelista kolumn (spec §4) - jedno źródło dla SELECT i skrótu. Zmiana listy zmienia rewizje WSZYSTKICH dokumentów:
/// nowy prefiks (<see cref="InvoiceRevision.Prefix"/>) + wpis w §9 kontraktu. Nazwy/typy: zrzut schematu 1.88 HF3, prod 1.89 - spec §10.
/// </summary>
public static class InvoiceRevisionColumns
{
    /// <summary>dok__Dokument (alias d).</summary>
    public static readonly IReadOnlyList<string> Header = new[]
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
    };

    public static readonly IReadOnlyList<string> Address = new[]
    {
        "adrh_Id", "adrh_Nazwa", "adrh_NazwaPelna", "adrh_Telefon", "adrh_Ulica", "adrh_NrDomu", "adrh_NrLokalu", "adrh_Adres",
        "adrh_Kod", "adrh_Miejscowosc", "adrh_Poczta", "adrh_IdPanstwo", "adrh_NIP",
    };

    /// <summary>
    /// Złączenia 1:1 z nagłówkiem (LEFT JOIN, klucz wiersza = alias_kolumna). Adres historyczny per rola: wydruk bierze dane zapamiętane
    /// przy wystawieniu (CHM KontrahentAdresId → adr_Historia). Numer KSeF: widoki producenta (vwDok4FSFZGrid, vwDok4KFSGrid) czytają
    /// go z ksef_NumerKSeF przez dok_NumerKSeFId - tekstowe dok_NumerKSeF/dok_DoNumerKSeF zostają obok (Codex r1 #1).
    /// </summary>
    public static readonly IReadOnlyList<InvoiceRevisionJoin> Joins = new[]
    {
        new InvoiceRevisionJoin("platnik", "adr_Historia", "adrh_Id", "dok_PlatnikAdreshId", Address),
        new InvoiceRevisionJoin("odbiorca", "adr_Historia", "adrh_Id", "dok_OdbiorcaAdreshId", Address),
        new InvoiceRevisionJoin("dostawa", "adr_Historia", "adrh_Id", "dok_AdresDostawyAdreshId", Address),
        new InvoiceRevisionJoin("ksef", "ksef_NumerKSeF", "ksefnr_Id", "dok_NumerKSeFId", new[] { "ksefnr_NumerKSeF" }),
        new InvoiceRevisionJoin("doksef", "ksef_NumerKSeF", "ksefnr_Id", "dok_DoNumerKSeFId", new[] { "ksefnr_NumerKSeF" }),
    };

    /// <summary>dok_Pozycja (alias p) - wiersze z ob_DokHanId = dok_Id.</summary>
    public static readonly IReadOnlyList<string> Position = new[]
    {
        "ob_Id", "ob_DokHanLp", "ob_TowId", "ob_TowRodzaj", "ob_Opis", "ob_Ilosc", "ob_Jm", "ob_CenaNetto", "ob_CenaBrutto",
        "ob_CenaWaluta", "ob_Rabat", "ob_VatId", "ob_VatProc", "ob_WartNetto", "ob_WartVat", "ob_WartBrutto", "ob_TowPkwiu",
        "ob_TowKodCN", "ob_OznaczenieGTU", "ob_PrzyczynaKorektyId", "ob_Znak", "ob_DoId",
    };

    /// <summary>tw__Towar (alias t): dok_Pozycja nie ma kopii nazwy - wydruk bierze bieżącą nazwę z kartoteki (CHM SuPozycja_TowarNazwa).</summary>
    public static readonly IReadOnlyList<string> Product = new[] { "tw_Nazwa", "tw_Symbol" };

    /// <summary>
    /// dok_Vat (alias v): tabelka VAT per stawka. Przy VatLiczonyAutomatycznie=False VAT w stawce ustawia się ręcznie (CHM), więc nie jest
    /// pochodną pozycji (Codex r1 #2). vt_Id poza skrótem: przepisanie tabelki bez zmiany treści nie zmienia wydruku.
    /// </summary>
    public static readonly IReadOnlyList<string> Vat = new[]
    {
        "vt_VatId", "vt_VatProc", "vt_WartNetto", "vt_WartVat", "vt_WartBrutto", "vt_WartNettoWaluta", "vt_WartVatWaluta", "vt_WartBruttoWaluta",
    };

    /// <summary>Klucz grupowania pozycji (= dok_Id dokumentu); w SELECT, poza skrótem.</summary>
    public const string PositionDocumentKey = "ob_DokHanId";

    /// <summary>Klucz grupowania wierszy VAT (= dok_Id dokumentu); w SELECT, poza skrótem.</summary>
    public const string VatDocumentKey = "vt_DokId";

    /// <summary>
    /// dok_Status = też stan skutku magazynowego (0 wycofany / 1 wykonany / 3 odłożony; CHM SubiektDokumentStatusEnum) - realizacja nie
    /// zmienia wydruku. Do skrótu idzie tylko fakt unieważnienia (2) (Codex r1 #6).
    /// </summary>
    public const string StatusColumn = "dok_Status";

    public static string JoinKey(string alias, string column) => alias + "_" + column;

    /// <summary>Klucze wiersza nagłówka w kolejności skrótu: kolumny dokumentu, potem złączenia (alias_kolumna).</summary>
    public static readonly IReadOnlyList<string> HeaderKeys =
        Header.Concat(Joins.SelectMany(j => j.Columns.Select(c => JoinKey(j.Alias, c)))).ToArray();

    /// <summary>Klucze wiersza pozycji w kolejności skrótu.</summary>
    public static readonly IReadOnlyList<string> PositionKeys = Position.Concat(Product).ToArray();
}

public sealed record InvoiceRevisionJoin(string Alias, string Table, string KeyColumn, string ForeignKey, IReadOnlyList<string> Columns);

/// <summary>Wiersze jednej partii (trzy zestawy wyników SQL: nagłówek ze złączeniami, pozycje z kartoteką, tabela VAT).</summary>
public sealed record InvoiceRevisionRows(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Headers,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Positions,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Vat);

public static class InvoiceRevisionSql
{
    /// <summary>Partia id na jedno polecenie: 3 SELECT-y współdzielą ten sam zestaw parametrów; MSSQL przyjmuje maks. 2100 parametrów.</summary>
    public const int BatchSize = 500;

    public static string ParameterName(int index) => "@d" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Trzy SELECT-y w jednym poleceniu (nagłówek + złączenia; pozycje + kartoteka towaru; tabela VAT). Filtr dok_Typ IN (2, 6) to
    /// optymalizacja (lista bez `type` zwraca wszystkie typy) - kontrakt bramkuje <see cref="InvoiceRevision.Compute"/>.
    /// </summary>
    public static string Build(int idCount)
    {
        if (idCount < 1 || idCount > BatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(idCount), idCount, $"Partia rewizji: 1..{BatchSize} id.");
        }
        string ids = string.Join(", ", Enumerable.Range(0, idCount).Select(ParameterName));
        var cols = InvoiceRevisionColumns.Header.Select(c => $"d.[{c}]")
            .Concat(InvoiceRevisionColumns.Joins.SelectMany(j =>
                j.Columns.Select(c => $"{j.Alias}.[{c}] AS [{InvoiceRevisionColumns.JoinKey(j.Alias, c)}]")));
        var joins = InvoiceRevisionColumns.Joins.Select(j => $"LEFT JOIN {j.Table} {j.Alias} ON {j.Alias}.{j.KeyColumn} = d.{j.ForeignKey}");
        var posCols = new[] { $"p.[{InvoiceRevisionColumns.PositionDocumentKey}]" }
            .Concat(InvoiceRevisionColumns.Position.Select(c => $"p.[{c}]"))
            .Concat(InvoiceRevisionColumns.Product.Select(c => $"t.[{c}]"));
        var vatCols = new[] { $"v.[{InvoiceRevisionColumns.VatDocumentKey}]" }.Concat(InvoiceRevisionColumns.Vat.Select(c => $"v.[{c}]"));

        return $"SELECT {string.Join(", ", cols)} FROM dok__Dokument d {string.Join(" ", joins)} "
             + $"WHERE d.dok_Id IN ({ids}) AND d.dok_Typ IN (2, 6);\n"
             + $"SELECT {string.Join(", ", posCols)} FROM dok_Pozycja p "
             + "JOIN dok__Dokument d ON d.dok_Id = p.ob_DokHanId AND d.dok_Typ IN (2, 6) "
             + "LEFT JOIN tw__Towar t ON t.tw_Id = p.ob_TowId "
             + $"WHERE p.ob_DokHanId IN ({ids});\n"
             + $"SELECT {string.Join(", ", vatCols)} FROM dok_Vat v "
             + "JOIN dok__Dokument d ON d.dok_Id = v.vt_DokId AND d.dok_Typ IN (2, 6) "
             + $"WHERE v.vt_DokId IN ({ids});";
    }
}

public static class InvoiceRevision
{
    /// <summary>Wersja algorytmu i zbioru pól (PZ: "r1:"). Zmiana = jednorazowa różnica dla wszystkich dokumentów.</summary>
    public const string Prefix = "f1:";

    /// <summary>
    /// "f1:" + 16 hex SHA-256 kanonicznej reprezentacji; null dla dok_Typ ∉ {FS, KFS}. Brak kolumny z whitelisty →
    /// KeyNotFoundException, nieznany typ wartości → NotSupportedException (fail-closed: wołający daje null, nigdy złą rewizję).
    /// </summary>
    public static string? Compute(IReadOnlyDictionary<string, object?> header, IEnumerable<IReadOnlyDictionary<string, object?>> positions,
        IEnumerable<IReadOnlyDictionary<string, object?>> vat)
    {
        int typ = Convert.ToInt32(Required(header, "dok_Typ"), CultureInfo.InvariantCulture);
        if (typ != DokTyp.FS && typ != DokTyp.KFS) return null;

        var sb = new StringBuilder();
        foreach (var key in InvoiceRevisionColumns.HeaderKeys)
        {
            object? value = Required(header, key);
            if (key == InvoiceRevisionColumns.StatusColumn && value is not null)
            {
                value = Convert.ToInt32(value, CultureInfo.InvariantCulture) == DuplicateScan.StatusAnulowany;
            }
            Append(sb, key, value);
        }
        foreach (var pos in positions.OrderBy(p => Convert.ToInt64(Required(p, "ob_Id"), CultureInfo.InvariantCulture)))
        {
            sb.Append("#p\n");
            foreach (var key in InvoiceRevisionColumns.PositionKeys) Append(sb, key, Required(pos, key));
        }
        // Wiersze VAT bez vt_Id: porządek = stawka, potem pełna treść (deterministyczny także przy dwóch wierszach tej samej stawki).
        var vatRows = vat.Select(v =>
        {
            var row = new StringBuilder("#v\n");
            foreach (var key in InvoiceRevisionColumns.Vat) Append(row, key, Required(v, key));
            return (Rate: Convert.ToInt64(Required(v, "vt_VatId"), CultureInfo.InvariantCulture), Text: row.ToString());
        });
        foreach (var row in vatRows.OrderBy(r => r.Rate).ThenBy(r => r.Text, StringComparer.Ordinal)) sb.Append(row.Text);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Prefix + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>dok_Id → rewizja (null dla nie-FS/KFS); pozycje i VAT grupowane po dokumencie; id bez wiersza nagłówka nieobecne.</summary>
    public static IReadOnlyDictionary<long, string?> ComputeAll(InvoiceRevisionRows rows)
    {
        var positions = GroupBy(rows.Positions, InvoiceRevisionColumns.PositionDocumentKey);
        var vat = GroupBy(rows.Vat, InvoiceRevisionColumns.VatDocumentKey);
        var none = new List<IReadOnlyDictionary<string, object?>>();
        var result = new Dictionary<long, string?>();
        foreach (var h in rows.Headers)
        {
            long id = Convert.ToInt64(Required(h, "dok_Id"), CultureInfo.InvariantCulture);
            result[id] = Compute(h, positions.GetValueOrDefault(id, none), vat.GetValueOrDefault(id, none));
        }
        return result;
    }

    /// <summary>
    /// Partie po <see cref="InvoiceRevisionSql.BatchSize"/> (limit parametrów MSSQL), id bez powtórzeń. Wyjątek dowolnej partii
    /// przerywa CAŁOŚĆ (bez wyniku częściowego) - fail-soft robi kontroler.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, string?>> ReadAllAsync(IReadOnlyCollection<long> ids,
        Func<long[], CancellationToken, Task<InvoiceRevisionRows>> readBatch, CancellationToken ct)
    {
        var result = new Dictionary<long, string?>();
        foreach (var batch in ids.Distinct().Chunk(InvoiceRevisionSql.BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (id, revision) in ComputeAll(await readBatch(batch, ct))) result[id] = revision;
        }
        return result;
    }

    private static Dictionary<long, List<IReadOnlyDictionary<string, object?>>> GroupBy(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, string documentKey)
        => rows.GroupBy(r => Convert.ToInt64(Required(r, documentKey), CultureInfo.InvariantCulture))
               .ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>
    /// Adapter czytnika polecenia <see cref="InvoiceRevisionSql.Build"/>: dokładnie trzy zestawy wyników w kolejności nagłówek, pozycje,
    /// VAT. Inna liczba zestawów = wyjątek (brak VAT dałby cicho złą rewizję) - fail-soft robi kontroler.
    /// </summary>
    public static async Task<InvoiceRevisionRows> ReadResultSetsAsync(DbDataReader reader, CancellationToken ct)
    {
        var sets = new List<List<IReadOnlyDictionary<string, object?>>>();
        do
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (await reader.ReadAsync(ct)) rows.Add(ReadRow(reader));
            sets.Add(rows);
        }
        while (await reader.NextResultAsync(ct));
        if (sets.Count != 3)
        {
            throw new InvalidOperationException($"Rewizja FS/KFS: {sets.Count} zestawow wynikow zamiast 3 (naglowek, pozycje, VAT).");
        }
        return new InvoiceRevisionRows(sets[0], sets[1], sets[2]);
    }

    /// <summary>Wiersz SQL → słownik po nazwie kolumny (aliasy ról adresu), DBNull → null.</summary>
    public static Dictionary<string, object?> ReadRow(IDataRecord record)
    {
        var row = new Dictionary<string, object?>(record.FieldCount, StringComparer.Ordinal);
        for (int i = 0; i < record.FieldCount; i++)
        {
            object value = record.GetValue(i);
            row[record.GetName(i)] = value is DBNull ? null : value;
        }
        return row;
    }

    private static object? Required(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value)
            ? value
            : throw new KeyNotFoundException($"Rewizja FS/KFS: brak kolumny '{key}' w wierszu (whitelista vs SELECT).");

    // nazwa=długość:wartość\n - długość wyklucza niejednoznaczność sklejeń sąsiednich kolumn.
    private static void Append(StringBuilder sb, string key, object? value)
    {
        string v = Canonical(value);
        sb.Append(key).Append('=').Append(v.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(v).Append('\n');
    }

    /// <summary>null/DBNull ≡ ""; decimal 4 dp (money); DateTime do ms; bool 1/0; liczby całkowite invariant; inny typ → wyjątek.</summary>
    public static string Canonical(object? value) => value switch
    {
        null or DBNull => string.Empty,
        string s => s,
        decimal m => Math.Round(m, 4, MidpointRounding.AwayFromZero).ToString("F4", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        byte or short or int or long => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"Rewizja FS/KFS: nieobslugiwany typ wartosci {value.GetType().Name}."),
    };
}
