using System.Data;

namespace SubiektBridge.Api.Sfera;

/// <summary>
/// NIP + nazwa kontrahenta (pola contractor_nip / contractor_name w GET /invoices/{id}, GET i PATCH /receipts/{id}, open-receivables/payables)
/// z read-only SQL zamiast COM <c>Kontrahenci.Wczytaj</c> (od v0.21.7). Wczytanie kontrahenta w Sferze uruchamia procedurę InsERT, której
/// zły plan w SQL Serverze klienta (2026-11-09) kosztował ~2,4 s na KAŻDY odczyt pojedynczego dokumentu. Kartoteka: adres główny w adr__Ewid
/// (<c>adr_TypAdresu = 1</c>, UNIQUE z adr_IdObiektu - najwyżej jeden wiersz); COM <c>.Nazwa</c> = nazwa skrócona = <c>adr_Nazwa</c>,
/// <c>.NIP</c> = <c>adr_NIP</c>. Czysta logika (bez SqlClient i COM) - połączenie i ścieżka COM w RealSferaSession.
/// <para><b>Ścisły parytet z COM</b> (sondy prod 2026-11-11, 14 dokumentów: firma/osoba/zagraniczny × FS/KFS/PZ - zgodne co do znaku):
/// COM nie przycina (178 nazw ma spacje brzegowe) i dla osoby bez NIP zwraca <c>""</c>, więc wartości idą bez zmian - świadomie INACZEJ niż
/// <c>InvoiceListingSql</c> (TrimmedOrNull), którego nie ruszamy.</para>
/// </summary>
public static class ContractorNipName
{
    public const string Sql = "SELECT a.adr_NIP, a.adr_Nazwa FROM adr__Ewid a WHERE a.adr_IdObiektu = @kh AND a.adr_TypAdresu = 1";

    /// <summary>adr_IdObiektu to int; id spoza zakresu = OverflowException (→ ścieżka COM w <see cref="Resolve"/>).</summary>
    public static SqlListingParameter Parameter(long contractorId) => new("@kh", checked((int)contractorId), SqlDbType.Int);

    /// <summary>
    /// Wiersz (nazwa kolumny → wartość) → (Nip, Name) bez żadnej normalizacji. <c>null</c> = brak wiersza → (null, null), tak jak dziś COM
    /// po nieudanym Wczytaj (kontrahent usunięty / id nie-kontrahenta). Brak kolumny = wyjątek (nigdy cicho null); DBNull → null.
    /// </summary>
    public static (string? Nip, string? Name) Map(IReadOnlyDictionary<string, object?>? row)
    {
        if (row is null) return (null, null);
        return (Text(row, "adr_NIP"), Text(row, "adr_Nazwa"));
    }

    /// <summary>
    /// Decyzja źródła: <paramref name="viaSql"/> = false (Bridge:ContractorReadViaSql) → wyłącznie COM. Wynik SQL (także brak wiersza)
    /// jest ostateczny - COM nie jest wołany. Wyjątek SQL → <paramref name="onSqlFailure"/> (WARNING) i dotychczasowa ścieżka COM: wolniej,
    /// ale ten sam wynik (inaczej niż listing, gdzie fallback na COM blokowałby most na minuty - tu to jedno Wczytaj, czyli stan sprzed v0.21.7).
    /// </summary>
    public static (string? Nip, string? Name) Resolve(long contractorId, bool viaSql,
        Func<long, (string? Nip, string? Name)> readSql,
        Func<long, (string? Nip, string? Name)> readCom,
        Action<Exception> onSqlFailure)
    {
        if (!viaSql) return readCom(contractorId);
        try
        {
            return readSql(contractorId);
        }
        catch (Exception ex)
        {
            onSqlFailure(ex);
            return readCom(contractorId);
        }
    }

    private static string? Text(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value))
        {
            throw new KeyNotFoundException($"NIP/nazwa kontrahenta: brak kolumny {column} w wierszu adr__Ewid.");
        }
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
