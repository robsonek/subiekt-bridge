using System.Data;
using System.Globalization;
using SubiektBridge.Api.Models;

namespace SubiektBridge.Api.Sfera;

/// <summary>Parametr zapytania listingu: nazwa (z @), wartość i typ SQL (string jako VarChar - kolumny są varchar, NVarChar wymusiłby konwersję kolumny).</summary>
public sealed record SqlListingParameter(string Name, object Value, SqlDbType Type);

/// <summary>Gotowe zapytanie listingu: tekst SQL + parametry (klient nigdy nie podaje raw SQL; stałe fragmenty z białej listy).</summary>
public sealed record InvoiceListingQuery(string Sql, IReadOnlyList<SqlListingParameter> Parameters);

/// <summary>
/// Listing GET /invoices i GET /receipts z read-only SQL POZA wątkiem STA (od v0.21.6). Po incydencie 2026-11-09 enumeracja kolekcji
/// Sfery przez COM kosztowała 0,5–1,5 s na dokument (pełne wczytanie dokumentu + Kontrahenci.Wczytaj), więc nawet 100 faktur nie mieściło
/// się w timeoucie klienta, a każdy taki job blokował pozostałe wywołania Sfery. Nagłówek dokumentu NIE jest szyfrowany (szyfrowany
/// jest tylko XML e-Faktury w ksef_Pliki) - wszystkie pola odpowiedzi są w dok__Dokument + adr__Ewid (kolumny kwot potwierdzone na prod
/// przez InvoiceRevision). Czysta logika (budowa zapytania + mapowanie wiersza) bez SqlClient - testowalna cross-platform; połączenie
/// i wykonanie w RealSferaSession.
/// Różnice wobec ścieżki COM (kontrakt §3.8): widzi WSZYSTKIE magazyny (opcjonalny filtr dok_MagId, magazyn w każdym wierszu),
/// pomija dokumenty anulowane (dok_Status = 2 - jedyny stan „nie istnieje", CHM SubiektDokumentStatusEnum; COM i tak nie wczytywał
/// ducha), typ spoza listy dok_Typ po prefiksie REALNEJ kolumny dok_NrPelny (bez nadpróbkowania). `type` w odpowiedzi nadal z numeru.
/// </summary>
public static class InvoiceListingSql
{
    /// <summary>Kolumny SELECT - jedno źródło dla zapytania i <see cref="Map"/> (brak kolumny w wierszu = wyjątek, nigdy cicho null).</summary>
    public static readonly IReadOnlyList<string> Columns = new[]
    {
        "d.dok_Id", "d.dok_Typ", "d.dok_NrPelny", "d.dok_DataWyst", "d.dok_PlatnikId", "d.dok_Uwagi",
        "d.dok_WartNetto", "d.dok_WartVat", "d.dok_WartBrutto", "d.dok_MagId", "a.adr_NIP", "a.adr_Nazwa",
    };

    /// <param name="limit">Już sklampowany (1..1000) przez wołającego.</param>
    /// <param name="nipContractorIds">kh_Id dla filtra `nip` (SQL po adr__Ewid, jak dotąd); pusta kolekcja = wołający zwraca [] bez zapytania.</param>
    public static InvoiceListingQuery Build(InvoiceQueryRequestDto request, int limit, IReadOnlyCollection<long>? nipContractorIds)
    {
        var parameters = new List<SqlListingParameter> { new("@limit", limit, SqlDbType.Int) };
        // Duch (anulowany) = WYŁĄCZNIE dok_Status = 2; 0 (wycofany skutek magazynowy), 1, 3 (odłożony) to dokumenty istniejące.
        var where = new List<string> { $"d.dok_Status <> {DuplicateScan.StatusAnulowany}" };

        var type = string.IsNullOrWhiteSpace(request.Type) ? null : request.Type.Trim().ToUpperInvariant();
        if (type is not null)
        {
            if (DokTyp.FromSymbol(type) is { } dokTyp)
            {
                where.Add("d.dok_Typ = @typ");
                parameters.Add(new("@typ", dokTyp, SqlDbType.Int));
            }
            else
            {
                // Symbol spoza listy typów Subiekta (np. niestandardowy "FH"): prefiks numeru. dok_NrPelny to realna kolumna
                // (w filtrze OtworzKolekcje był tylko atrybut COM dok_NumerPelny - stąd dawny filtr client-side).
                where.Add("d.dok_NrPelny LIKE @nr");
                parameters.Add(new("@nr", OpenReceivableFields.EscapeLikeWildcards(type) + " %", SqlDbType.VarChar));
            }
        }

        if (TryParseIsoDate(request.From, out var from))
        {
            where.Add("d.dok_DataWyst >= @from");
            parameters.Add(new("@from", from, SqlDbType.DateTime));
        }
        if (TryParseIsoDate(request.To, out var to))
        {
            where.Add("d.dok_DataWyst <= @to");
            parameters.Add(new("@to", to, SqlDbType.DateTime));
        }

        if (!string.IsNullOrWhiteSpace(request.NotesContains))
        {
            where.Add("d.dok_Uwagi LIKE @notes");
            parameters.Add(new("@notes", "%" + OpenReceivableFields.EscapeLikeWildcards(request.NotesContains) + "%", SqlDbType.VarChar));
        }

        // Kontrahent dokumentu sprzedaży = dok_PlatnikId (NIE dok_OdbiorcaId - na MM to id magazynu). Id to long - bez escapowania.
        if (nipContractorIds is not null && InvoiceQueryFields.ContractorClause(nipContractorIds) is { } contractorClause)
        {
            where.Add(contractorClause);
        }

        if (request.WarehouseSubiektId is { } mag)
        {
            where.Add("d.dok_MagId = @mag");
            parameters.Add(new("@mag", mag, SqlDbType.Int));
        }

        var sql =
            $"SELECT TOP (@limit) {string.Join(", ", Columns)}\n" +
            "FROM dok__Dokument d\n" +
            // Dane BIEŻĄCE kartoteki (adres główny, adr_TypAdresu = 1) - tak samo jak Kontrahenci.Wczytaj w ścieżce COM. LEFT JOIN: brak
            // kartoteki (dok_PlatnikId NULL/0) nie wycina dokumentu, tylko daje contractor_nip/name = null.
            "LEFT JOIN adr__Ewid a ON a.adr_IdObiektu = d.dok_PlatnikId AND a.adr_TypAdresu = 1\n" +
            $"WHERE {string.Join(" AND ", where)}\n" +
            // Najnowsze najpierw (Subiekt nadaje rosnące dok_Id) - jak sort OtworzKolekcje.
            "ORDER BY d.dok_Id DESC";

        return new InvoiceListingQuery(sql, parameters);
    }

    /// <summary>Wiersz (nazwa kolumny bez aliasu → wartość; DBNull lub null = brak) → DTO w kształcie ścieżki COM + warehouse_subiekt_id.</summary>
    public static InvoiceQueryItemDto Map(IReadOnlyDictionary<string, object?> row)
    {
        long subiektId = Convert.ToInt64(Required(row, "dok_Id"), CultureInfo.InvariantCulture);
        string number = (Required(row, "dok_NrPelny") as string)?.Trim() ?? string.Empty;
        string type = number.Split(' ', 2)[0]; // "FS 1/2026" -> "FS" (jak MapDokumentToQueryItem; kontrakt §3.8)

        var date = Required(row, "dok_DataWyst") as DateTime?;
        long? contractorId = Required(row, "dok_PlatnikId") is { } c ? Convert.ToInt64(c, CultureInfo.InvariantCulture) : null;
        int? warehouse = Required(row, "dok_MagId") is { } m ? Convert.ToInt32(m, CultureInfo.InvariantCulture) : null;

        return new InvoiceQueryItemDto(
            SubiektId: subiektId,
            Number: number,
            Type: type,
            IssueDate: date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ContractorId: contractorId,
            ContractorNip: TrimmedOrNull(Required(row, "adr_NIP")),
            ContractorName: TrimmedOrNull(Required(row, "adr_Nazwa")),
            NetAmount: Money(Required(row, "dok_WartNetto")),
            VatAmount: Money(Required(row, "dok_WartVat")),
            GrossAmount: Money(Required(row, "dok_WartBrutto")),
            Notes: Required(row, "dok_Uwagi") as string,
            Revision: null,
            WarehouseSubiektId: warehouse);
    }

    /// <summary>Brak kolumny = błąd odczytu (502), nigdy cicho null. DBNull → null.</summary>
    private static object? Required(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value))
        {
            throw new KeyNotFoundException($"Listing SQL: brak kolumny {column} w wierszu dok__Dokument.");
        }
        return value is DBNull ? null : value;
    }

    private static string? TrimmedOrNull(object? value)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static decimal? Money(object? value)
        => value is null ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static bool TryParseIsoDate(string? s, out DateTime date)
        => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
