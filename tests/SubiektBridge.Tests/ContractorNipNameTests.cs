using System.Data;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// NIP i nazwa kontrahenta z read-only SQL (adr__Ewid, adres główny) zamiast COM Kontrahenci.Wczytaj (v0.21.7). Wczytanie kontrahenta
/// w Sferze uruchamia procedurę InsERT, która przy złym planie w SQL Serverze kosztowała ~2,4 s na każdy GET /invoices/{id} i
/// GET /receipts/{id}. Wynik musi być bajt w bajt taki jak z COM (sondy prod: COM nie przycina, osoba bez NIP = ""), więc mapowanie
/// NIE używa reguły listingu (TrimmedOrNull). Awaria SQL = dotychczasowa ścieżka COM (wolniej, ale bez zmiany wyniku).
/// </summary>
public class ContractorNipNameTests
{
    private static Dictionary<string, object?> Row(object? nip, object? name) => new() { ["adr_NIP"] = nip, ["adr_Nazwa"] = name };

    // ----------------------------- Sql + Parameter -----------------------------

    [Fact]
    public void Sql_ReadsNipAndShortName_FromContractorMainAddress_ByParameter()
    {
        var sql = ContractorNipName.Sql;

        Assert.Contains("SELECT a.adr_NIP, a.adr_Nazwa", sql);   // COM .Nazwa = nazwa skrócona (adr_Nazwa), nie adr_NazwaPelna
        Assert.Contains("FROM adr__Ewid a", sql);
        Assert.Contains("a.adr_IdObiektu = @kh", sql);
        Assert.Contains("a.adr_TypAdresu = 1", sql);              // 1 = adres siedziby kontrahenta (2/11 = korespondencyjny/dostawy)
        Assert.DoesNotContain("adr_NazwaPelna", sql);
    }

    [Fact]
    public void Parameter_IsContractorIdAsInt()
    {
        var p = ContractorNipName.Parameter(4321);

        Assert.Equal("@kh", p.Name);
        Assert.Equal(SqlDbType.Int, p.Type);                      // adr_IdObiektu to int
        Assert.Equal((object)4321, p.Value);                     // boxed int (long dałby inny typ parametru)
    }

    [Fact]
    public void Parameter_IdBeyondIntRange_Throws()
    {
        Assert.Throws<OverflowException>(() => ContractorNipName.Parameter((long)int.MaxValue + 1));
    }

    // ----------------------------- Map (parytet z COM) -----------------------------

    [Fact]
    public void Map_KeepsValuesVerbatim_NoTrim_LikeCom()
    {
        // Prod: 178 nazw ze spacją na brzegu, COM zwraca je bez przycinania - SQL musi dać to samo.
        var (nip, name) = ContractorNipName.Map(Row("DE 111111111 ", " Mock Test GmbH "));

        Assert.Equal("DE 111111111 ", nip);
        Assert.Equal(" Mock Test GmbH ", name);
    }

    [Fact]
    public void Map_EmptyNip_StaysEmptyString_LikeComForPerson()
    {
        // Osoba bez NIP: COM daje "" (klucz obecny, pusty napis), nie null - reguła listingu zamieniłaby to na null.
        var (nip, name) = ContractorNipName.Map(Row("", "Jan Testowy"));

        Assert.Equal("", nip);
        Assert.Equal("Jan Testowy", name);
    }

    [Fact]
    public void Map_NoRow_GivesNulls()
    {
        // Brak adresu głównego (kontrahent usunięty / id nie-kontrahenta) = (null, null), jak dziś COM po nieudanym Wczytaj.
        Assert.Equal(((string?)null, (string?)null), ContractorNipName.Map(null));
    }

    [Fact]
    public void Map_DbNull_GivesNull()
    {
        var (nip, name) = ContractorNipName.Map(Row(DBNull.Value, DBNull.Value));

        Assert.Null(nip);
        Assert.Null(name);
    }

    [Theory]
    [InlineData("adr_NIP")]
    [InlineData("adr_Nazwa")]
    public void Map_MissingColumn_Throws_NeverSilentNull(string missing)
    {
        var row = Row("1111111111", "Mock Test Sp. z o.o.");
        row.Remove(missing);

        Assert.Throws<KeyNotFoundException>(() => ContractorNipName.Map(row));
    }

    // ----------------------------- Resolve (SQL -> COM tylko przy awarii) -----------------------------

    [Fact]
    public void Resolve_SqlResult_IsReturned_WithoutTouchingCom()
    {
        int comCalls = 0;
        Exception? reported = null;
        long? askedSql = null;

        var result = ContractorNipName.Resolve(7, viaSql: true,
            readSql: id => { askedSql = id; return ("1111111111", "Mock Test Sp. z o.o."); },
            readCom: _ => { comCalls++; return ("9999999999", "COM"); },
            onSqlFailure: ex => reported = ex);

        Assert.Equal(("1111111111", "Mock Test Sp. z o.o."), result);
        Assert.Equal(7, askedSql);
        Assert.Equal(0, comCalls);
        Assert.Null(reported);
    }

    [Fact]
    public void Resolve_SqlNoRow_GivesNulls_WithoutFallingBackToCom()
    {
        int comCalls = 0;

        var result = ContractorNipName.Resolve(7, viaSql: true,
            readSql: _ => (null, null),
            readCom: _ => { comCalls++; return ("9999999999", "COM"); },
            onSqlFailure: _ => { });

        Assert.Equal(((string?)null, (string?)null), result);
        Assert.Equal(0, comCalls);
    }

    [Fact]
    public void Resolve_SqlThrows_ReportsFailure_AndFallsBackToCom()
    {
        var boom = new InvalidOperationException("SQL niedostępny");
        Exception? reported = null;
        long? askedCom = null;

        var result = ContractorNipName.Resolve(7, viaSql: true,
            readSql: _ => throw boom,
            readCom: id => { askedCom = id; return ("1111111111", "Mock z COM"); },
            onSqlFailure: ex => reported = ex);

        Assert.Equal(("1111111111", "Mock z COM"), result);
        Assert.Equal(7, askedCom);
        Assert.Same(boom, reported);
    }

    [Fact]
    public void Resolve_SwitchOff_UsesComOnly()
    {
        int sqlCalls = 0;

        var result = ContractorNipName.Resolve(7, viaSql: false,
            readSql: _ => { sqlCalls++; return ("1111111111", "SQL"); },
            readCom: _ => ("", "Mock z COM"),
            onSqlFailure: _ => { });

        Assert.Equal(("", "Mock z COM"), result);
        Assert.Equal(0, sqlCalls);
    }

    [Fact]
    public void Option_ContractorReadViaSql_IsOnByDefault()
    {
        // Bez wpisu w appsettings fix ma działać (false = jawny przełącznik awaryjny na dawną ścieżkę COM).
        Assert.True(new BridgeOptions().ContractorReadViaSql);
    }
}
