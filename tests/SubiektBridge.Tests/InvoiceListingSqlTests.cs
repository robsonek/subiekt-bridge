using System.Data;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Listing GET /invoices i GET /receipts z read-only SQL poza wątkiem STA (po incydencie 2026-11-09: enumeracja kolekcji przez COM
/// kosztuje 0,5–1,5 s na dokument, 100 faktur nie mieści się w timeoucie klienta). Zapytanie jest budowane z białej listy pól
/// i parametryzowane (klient nie podaje raw SQL); wiersz mapowany jest na ten sam InvoiceQueryItemDto, co ścieżka COM, plus
/// `warehouse_subiekt_id` (dok_MagId) - SQL widzi WSZYSTKIE magazyny (COM OtworzKolekcje tylko magazyn sesji).
/// </summary>
public class InvoiceListingSqlTests
{
    private static InvoiceQueryRequestDto Req(string? type = null, string? from = null, string? to = null, string? notes = null,
        string? nip = null, int limit = 200, int? warehouse = null)
        => new(From: from, To: to, Type: type, NotesContains: notes, Nip: nip, Limit: limit, WarehouseSubiektId: warehouse);

    private static object? Param(InvoiceListingQuery q, string name) => q.Parameters.Single(p => p.Name == name).Value;

    // ----------------------------- Build -----------------------------

    [Fact]
    public void Build_NoFilters_TopLimit_ExcludesCancelled_OrdersByIdDesc_AllWarehouses()
    {
        var q = InvoiceListingSql.Build(Req(limit: 300), limit: 300, nipContractorIds: null);

        Assert.Contains("SELECT TOP (@limit)", q.Sql);
        Assert.Equal(300, Param(q, "@limit"));
        Assert.Contains("d.dok_Status <> 2", q.Sql);          // duch (anulowany) = jedyny stan "nie istnieje"
        Assert.EndsWith("ORDER BY d.dok_Id DESC", q.Sql.TrimEnd());
        Assert.DoesNotContain("dok_MagId =", q.Sql);          // bez filtra = wszystkie magazyny
        Assert.DoesNotContain("dok_Typ", q.Sql.Split("FROM")[1].Split("ORDER")[0].Replace("d.dok_Typ,", "")); // brak filtra typu w WHERE
        Assert.Single(q.Parameters);
    }

    [Theory]
    [InlineData("FS", 2)]
    [InlineData("kfs", 6)]
    [InlineData("PZ", 10)]
    public void Build_KnownType_FiltersByDokTypParameter(string type, int dokTyp)
    {
        var q = InvoiceListingSql.Build(Req(type: type), 200, null);

        Assert.Contains("d.dok_Typ = @typ", q.Sql);
        Assert.Equal(dokTyp, Param(q, "@typ"));
        Assert.DoesNotContain("dok_NrPelny LIKE", q.Sql);
    }

    [Fact]
    public void Build_UnknownType_FiltersByNumberPrefix_WithEscapedLike()
    {
        // dok_NrPelny to REALNA kolumna dok__Dokument (w OtworzKolekcje nie było jej jako dok_NumerPelny) - symbol spoza listy dok_Typ
        // idzie po prefiksie numeru w SQL, bez nadpróbkowania i bez wczytywania dokumentów.
        var q = InvoiceListingSql.Build(Req(type: "f_h"), 200, null);

        Assert.Contains("d.dok_NrPelny LIKE @nr", q.Sql);
        Assert.Equal("F[_]H %", Param(q, "@nr"));
        Assert.DoesNotContain("@typ", q.Sql);
    }

    [Fact]
    public void Build_ValidDates_AreDateTimeParameters()
    {
        var q = InvoiceListingSql.Build(Req(from: "2026-07-01", to: "2026-07-31"), 200, null);

        Assert.Contains("d.dok_DataWyst >= @from", q.Sql);
        Assert.Contains("d.dok_DataWyst <= @to", q.Sql);
        Assert.Equal(new DateTime(2026, 7, 1), Param(q, "@from"));
        Assert.Equal(new DateTime(2026, 7, 31), Param(q, "@to"));
        Assert.Equal(SqlDbType.DateTime, q.Parameters.Single(p => p.Name == "@from").Type);
    }

    [Theory]
    [InlineData("2026-02-31")]
    [InlineData("31.07.2026")]
    [InlineData("")]
    public void Build_InvalidDate_IsIgnored(string from)
    {
        var q = InvoiceListingSql.Build(Req(from: from), 200, null);

        Assert.DoesNotContain("@from", q.Sql);
    }

    [Fact]
    public void Build_NotesContains_EscapedLikeParameter()
    {
        var q = InvoiceListingSql.Build(Req(notes: "order_12%[x]"), 200, null);

        Assert.Contains("d.dok_Uwagi LIKE @notes", q.Sql);
        Assert.Equal("%order[_]12[%][[]x]%", Param(q, "@notes"));
    }

    [Fact]
    public void Build_NipContractorIds_InClauseOnPlatnikId()
    {
        var q = InvoiceListingSql.Build(Req(nip: "1111111111"), 200, new long[] { 5, 7 });

        Assert.Contains("dok_PlatnikId IN (5,7)", q.Sql);
        Assert.DoesNotContain("OdbiorcaId", q.Sql);
    }

    [Fact]
    public void Build_Warehouse_FiltersByDokMagId()
    {
        var q = InvoiceListingSql.Build(Req(warehouse: 4), 200, null);

        Assert.Contains("d.dok_MagId = @mag", q.Sql);
        Assert.Equal(4, Param(q, "@mag"));
    }

    [Fact]
    public void Build_SelectsContractorFromAdrEwidMainAddress_LeftJoin()
    {
        var q = InvoiceListingSql.Build(Req(), 200, null);

        Assert.Contains("LEFT JOIN adr__Ewid a ON a.adr_IdObiektu = d.dok_PlatnikId AND a.adr_TypAdresu = 1", q.Sql);
        foreach (var col in new[] { "d.dok_Id", "d.dok_NrPelny", "d.dok_DataWyst", "d.dok_PlatnikId", "d.dok_Uwagi",
                     "d.dok_WartNetto", "d.dok_WartVat", "d.dok_WartBrutto", "d.dok_MagId", "a.adr_NIP", "a.adr_Nazwa" })
        {
            Assert.Contains(col, q.Sql);
        }
    }

    [Fact]
    public void Build_AllFilters_Combined_WithAnd()
    {
        var q = InvoiceListingSql.Build(Req(type: "FS", from: "2026-07-01", to: "2026-07-31", notes: "abc", nip: "1", warehouse: 2),
            100, new long[] { 9 });

        var where = q.Sql.Split("WHERE")[1].Split("ORDER")[0];
        Assert.Equal(7, where.Split(" AND ").Length); // status + typ + from + to + notes + platnik + mag = 7 warunków
        Assert.Contains("d.dok_MagId = @mag", where);
    }

    // ----------------------------- Map -----------------------------

    /// <summary>Pełny wiersz jak z SqlClient (dok_Id/dok_PlatnikId/dok_MagId = int, money = decimal); testy nadpisują pojedyncze kolumny.</summary>
    private static Dictionary<string, object?> Row(params (string Column, object? Value)[] overrides)
    {
        var row = new Dictionary<string, object?>
        {
            ["dok_Id"] = 142877, ["dok_NrPelny"] = "FS 12/01/2026", ["dok_DataWyst"] = new DateTime(2026, 1, 12), ["dok_PlatnikId"] = 77,
            ["dok_Uwagi"] = "x | ref: o:1", ["dok_WartNetto"] = 100.37m, ["dok_WartVat"] = 23.08m, ["dok_WartBrutto"] = 123.45m, ["dok_MagId"] = 4,
            ["adr_NIP"] = "1111111111", ["adr_Nazwa"] = "Mock Test Sp. z o.o.",
        };
        foreach (var (column, value) in overrides) row[column] = value;
        return row;
    }

    [Fact]
    public void Map_FullRow_ProducesSameShapeAsComPath_PlusWarehouse()
    {
        var dto = InvoiceListingSql.Map(Row());

        Assert.Equal(142877, dto.SubiektId);
        Assert.Equal("FS 12/01/2026", dto.Number);
        Assert.Equal("FS", dto.Type);                 // typ z prefiksu numeru - jak ścieżka COM (kontrakt §3.8)
        Assert.Equal("2026-01-12", dto.IssueDate);
        Assert.Equal(77, dto.ContractorId);
        Assert.Equal("1111111111", dto.ContractorNip);
        Assert.Equal("Mock Test Sp. z o.o.", dto.ContractorName);
        Assert.Equal(100.37m, dto.NetAmount);
        Assert.Equal(23.08m, dto.VatAmount);
        Assert.Equal(123.45m, dto.GrossAmount);
        Assert.Equal("x | ref: o:1", dto.Notes);
        Assert.Equal(4, dto.WarehouseSubiektId);
        Assert.Null(dto.Revision);                    // dokleja kontroler (jak dotąd)
    }

    [Fact]
    public void Map_SqlTypes_IntAndDbNull_AreHandled()
    {
        // SqlClient zwraca int (dok_Id, dok_PlatnikId, dok_MagId są int), money jako decimal, brak wartości jako DBNull.
        var dto = InvoiceListingSql.Map(Row(("dok_WartNetto", DBNull.Value), ("dok_WartVat", null), ("dok_WartBrutto", 10m), ("dok_MagId", 1),
            ("adr_NIP", DBNull.Value), ("adr_Nazwa", DBNull.Value)));

        Assert.Equal(77, dto.ContractorId);
        Assert.Null(dto.NetAmount);
        Assert.Null(dto.VatAmount);
        Assert.Equal(10m, dto.GrossAmount);
        Assert.Equal(1, dto.WarehouseSubiektId);
        Assert.Null(dto.ContractorNip);
        Assert.Null(dto.ContractorName);
    }

    [Fact]
    public void Map_TrimsPaddedStrings_EmptyNipBecomesNull()
    {
        var dto = InvoiceListingSql.Map(Row(("dok_NrPelny", "FS 1/2026   "), ("adr_NIP", "   "), ("adr_Nazwa", " Firma  ")));

        Assert.Equal("FS 1/2026", dto.Number);
        Assert.Equal("FS", dto.Type);
        Assert.Null(dto.ContractorNip);
        Assert.Equal("Firma", dto.ContractorName);
    }

    [Fact]
    public void Map_MissingRequiredColumn_Throws_FailClosed()
    {
        var row = Row();
        row.Remove("dok_MagId");

        Assert.Throws<KeyNotFoundException>(() => InvoiceListingSql.Map(row));
    }

    [Fact]
    public void RequestDto_WarehouseIsOptional_DefaultsToNull()
    {
        var r = new InvoiceQueryRequestDto(From: null, To: null, Type: null, NotesContains: null, Nip: null);

        Assert.Null(r.WarehouseSubiektId);
    }
}
