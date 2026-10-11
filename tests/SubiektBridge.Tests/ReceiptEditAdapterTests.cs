using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Adapter COM edycji PZ na delegatach (Codex r4 #1/#2/#3/#6): odczyt chronionego snapshotu (<see cref="ReceiptSnapshotReader"/>),
/// klasyfikacja awarii WczytajDokument (<see cref="ReceiptLoadFailure"/>), Zamknij z raportem i resetem sesji (<see cref="ReceiptCloseGuard"/>),
/// zakres id mostu (<see cref="BridgeIds"/>). RealSferaSession woła TE funkcje z delegatami InvokeMember — testy dowodzą zachowania
/// adaptera, nie Fake. Każdy test nazywa mutanta, który go czerwieni.
/// </summary>
public class ReceiptEditAdapterTests
{
    // ----------------------------- pomocniki -----------------------------

    private static Dictionary<string, object?> Header(object? linkedRaw = null) => new()
    {
        ["Typ"] = 10, ["StatusDokumentu"] = 1, ["NumerPelny"] = "PZ 1/2026", ["NumerOryginalny"] = "Client:test_buyer",
        ["Uwagi"] = "PZ testowe | ref: test:pz:1", ["DoDokumentuId"] = linkedRaw ?? DBNull.Value,
    };

    private static Dictionary<string, object?> Position(long id = 910001, int lp = 1) => new()
    {
        ["Id"] = (int)id, ["Lp"] = (short)lp, ["IloscJm"] = 2.0, ["Ilosc"] = 2.0, ["Jm"] = "szt.", ["CenaNettoPrzedRabatem"] = 100.0,
        ["RabatProcent"] = 0.0, ["VatProcent"] = 23.0, ["TowarId"] = 6931, ["UslugaJednorazowa"] = false, ["TowarRodzaj"] = 1,
    };

    private static Func<string, object?> Attr(Dictionary<string, object?> d) => name =>
        d.TryGetValue(name, out var v) ? v : throw new InvalidOperationException($"nieznany atrybut {name}");

    private static ReceiptProtectedRead Read(Dictionary<string, object?> header, params Dictionary<string, object?>[] positions)
        => ReceiptSnapshotReader.Read(1, Attr(header), () => positions.Select(Attr));

    private static readonly string[] HeaderAttributes = { "Typ", "StatusDokumentu", "NumerPelny", "NumerOryginalny", "Uwagi", "DoDokumentuId" };
    private static readonly string[] PositionAttributes =
        { "Id", "Lp", "IloscJm", "Ilosc", "Jm", "CenaNettoPrzedRabatem", "RabatProcent", "VatProcent", "TowarId", "UslugaJednorazowa", "TowarRodzaj" };

    public static IEnumerable<object[]> HeaderNames() => HeaderAttributes.Select(n => new object[] { n });
    public static IEnumerable<object[]> PositionNames() => PositionAttributes.Select(n => new object[] { n });
    public static IEnumerable<object[]> RequiredHeaderNulls() => new[] { "Typ", "StatusDokumentu", "NumerOryginalny" }
        .SelectMany(n => new object?[] { null, DBNull.Value }.Select(v => new object?[] { n, v }))!;
    public static IEnumerable<object[]> RequiredPositionNulls() => new[] { "Id", "Lp", "IloscJm", "Ilosc", "CenaNettoPrzedRabatem", "RabatProcent", "VatProcent", "UslugaJednorazowa", "TowarRodzaj" }
        .SelectMany(n => new object?[] { null, DBNull.Value }.Select(v => new object?[] { n, v }))!;

    // ----------------------------- ReceiptSnapshotReader (r4 #1 / #3: klasa N4/N7) -----------------------------

    [Fact]
    public void Reader_HappyPath_MapsEveryProtectedField_PreservesOrder()
    {
        // Mutant: pominięcie dowolnego pola (np. VatProcent -> 0) albo sortowanie pozycji -> pada.
        var r = Read(Header(linkedRaw: 60001), Position(910002, 2), Position(910001, 1));
        Assert.Equal(10, r.DokTyp); Assert.Equal("PZ 1/2026", r.Number); Assert.Equal("PZ testowe | ref: test:pz:1", r.Uwagi); Assert.Equal(60001, r.LinkedId);
        Assert.Equal("Client:test_buyer", r.Snapshot.OriginalNumber); Assert.Equal(1, r.Snapshot.Status); Assert.Null(r.Snapshot.BookingStatus);
        Assert.Equal(2, r.Snapshot.Items.Count);
        Assert.Equal(910002, r.Snapshot.Items[0].PositionId); // kolejność enumeracji (Element(1..n)) zachowana — prezentacja mapowana po indeksie
        var i = r.Snapshot.Items[1];
        Assert.Equal(910001, i.PositionId); Assert.Equal(1, i.Lp); Assert.Equal(6931, i.ProductId); Assert.Equal(ReceiptItemKind.Product, i.Kind);
        Assert.Equal(2m, i.Quantity); Assert.Equal(2m, i.QuantityInStockUnits); Assert.Equal("szt.", i.Unit); Assert.Equal(100m, i.UnitPriceNet);
        Assert.Equal(0m, i.DiscountPercent); Assert.Equal(23m, i.VatRate);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(60001, 60001L)]
    public void Reader_LinkedId_NullZeroNegativeMeanNoLink(int? raw, long? expected)
    {
        // Mutant: 0/-1 traktowane jako link -> pada; null -> wyjątek -> pada (CHM: DoDokumentuId bez linku = NULL, dok_DoDokId NULL).
        object? value = raw is null ? DBNull.Value : raw;
        Assert.Equal(expected, Read(Header(linkedRaw: value), Position()).LinkedId);
        if (raw is null) Assert.Null(ReceiptSnapshotReader.Read(1, n => n == "DoDokumentuId" ? null : Attr(Header())(n), () => new[] { Attr(Position()) }).LinkedId);
    }

    [Theory]
    [MemberData(nameof(HeaderNames))]
    public void Reader_HeaderAttributeThrows_ReadFailed_InnerPreserved(string name)
    {
        // Mutant: TryRead* (catch -> null/0) dla pola nagłówka -> snapshot z podstawioną wartością -> pada (oczekiwany ReadFailed z inner).
        var h = Header();
        var thrown = new COMException("RPC server unavailable", unchecked((int)0x800706BA));
        var ex = Assert.Throws<ReceiptEditException>(() =>
            ReceiptSnapshotReader.Read(7, n => n == name ? throw thrown : Attr(h)(n), () => new[] { Attr(Position()) }));
        Assert.Equal(ReceiptEditError.ReadFailed, ex.Reason);
        Assert.Same(thrown, ex.InnerException);
        Assert.Contains(name, ex.Message);
        Assert.Contains("7", ex.Message);
    }

    [Theory]
    [MemberData(nameof(RequiredHeaderNulls))]
    public void Reader_RequiredHeaderNull_ReadFailed(string name, object? value)
    {
        // Mutant: `?? 0` / `?? string.Empty` dla pola wymaganego (dok_Typ/dok_Status/dok_NrPelnyOryg są NOT NULL) -> pada.
        var h = Header(); h[name] = value;
        var ex = Assert.Throws<ReceiptEditException>(() => Read(h, Position()));
        Assert.Equal(ReceiptEditError.ReadFailed, ex.Reason);
        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Reader_OptionalHeaderNulls_AreValues()
    {
        // Mutant: Uwagi/NumerPelny null -> wyjątek -> pada (prezentacyjne; Uwagi może być NULL, numer tylko do logów/DTO).
        var h = Header(); h["Uwagi"] = null; h["NumerPelny"] = DBNull.Value;
        var r = Read(h, Position());
        Assert.Null(r.Uwagi); Assert.Equal(string.Empty, r.Number);
    }

    [Fact]
    public void Reader_PositionsNull_ReadFailed()
    {
        // Mutant: Pozycje == null -> pusta kolekcja (snapshot "bez pozycji" uznany za kompletny) -> pada.
        var ex = Assert.Throws<ReceiptEditException>(() => ReceiptSnapshotReader.Read(1, Attr(Header()), () => null));
        Assert.Equal(ReceiptEditError.ReadFailed, ex.Reason);
        Assert.Contains("Pozycje", ex.Message);
    }

    [Fact]
    public void Reader_PositionsEnumerationThrows_ReadFailed_InnerPreserved()
    {
        // Mutant: wyjątek z Element(i)/Liczba połknięty (częściowa lista) -> pada.
        var thrown = new COMException("Element(2) padl", unchecked((int)0x80004005));
        IEnumerable<Func<string, object?>> Seq() { yield return Attr(Position()); throw thrown; }
        var ex = Assert.Throws<ReceiptEditException>(() => ReceiptSnapshotReader.Read(1, Attr(Header()), Seq));
        Assert.Equal(ReceiptEditError.ReadFailed, ex.Reason);
        Assert.Same(thrown, ex.InnerException);
    }

    [Theory]
    [MemberData(nameof(PositionNames))]
    public void Reader_PositionAttributeThrows_ReadFailed_InnerPreserved(string name)
    {
        // Mutant (r4 #1): TryReadInt64(poz,"TowarId") / TryReadBool("UslugaJednorazowa") / TryReadInt32("TowarRodzaj") połykają wyjątek
        // -> Kind=OneOffService podstawione przed i po zapisie -> Intended mimo nieznanej tożsamości pozycji -> ten test pada.
        var p = Position();
        var thrown = new COMException("RPC", unchecked((int)0x800706BA));
        var ex = Assert.Throws<ReceiptEditException>(() =>
            ReceiptSnapshotReader.Read(1, Attr(Header()), () => new[] { (Func<string, object?>)(n => n == name ? throw thrown : Attr(p)(n)) }));
        Assert.Equal(ReceiptEditError.ReadFailed, ex.Reason);
        Assert.Same(thrown, ex.InnerException);
        Assert.Contains(name, ex.Message);
    }

    [Theory]
    [MemberData(nameof(RequiredPositionNulls))]
    public void Reader_RequiredPositionNull_ReadFailed(string name, object? value)
    {
        // Mutant: `Convert.ToDecimal(x ?? 0m)` (null -> 0 w polu komparatora) -> pada. Kolumny ob_* są NOT NULL (zrzut schematu).
        var p = Position(); p[name] = value;
        var ex = Assert.Throws<ReceiptEditException>(() => Read(Header(), p));
        Assert.Equal(ReceiptEditError.ReadFailed, ex.Reason);
        Assert.Contains(name, ex.Message);
    }

    [Theory]
    [InlineData(1, false, 6931, ReceiptItemKind.Product)]
    [InlineData(2, false, 6931, ReceiptItemKind.Service)]
    [InlineData(4, false, 6931, ReceiptItemKind.Package)]
    [InlineData(8, false, 6931, ReceiptItemKind.Bundle)]
    [InlineData(32, false, 6931, ReceiptItemKind.Suggestion)]
    [InlineData(99, false, 6931, ReceiptItemKind.Unknown)]
    [InlineData(1, true, 6931, ReceiptItemKind.OneOffService)]
    [InlineData(2, false, null, ReceiptItemKind.OneOffService)]
    public void Reader_KindMapping_TowarIdNullIsOneOffService(int rodzaj, bool oneOff, int? towarId, ReceiptItemKind expected)
    {
        // Mutant: TowarId null -> wyjątek -> pada (CHM SuPozycja_TowarId: NULL = usługa jednorazowa); Unknown zamiast Suggestion -> pada.
        var p = Position(); p["TowarRodzaj"] = rodzaj; p["UslugaJednorazowa"] = oneOff; p["TowarId"] = towarId is null ? DBNull.Value : towarId;
        var item = Read(Header(), p).Snapshot.Items.Single();
        Assert.Equal(expected, item.Kind);
        Assert.Equal(towarId is null ? null : (long?)towarId, item.ProductId);
    }

    [Fact]
    public void Reader_JmNull_IsEmptyUnit()
    {
        // Mutant: Jm null -> wyjątek -> pada (ob_Jm jest NULL-owalne w schemacie).
        var p = Position(); p["Jm"] = null;
        Assert.Equal(string.Empty, Read(Header(), p).Snapshot.Items.Single().Unit);
    }

    [Fact]
    public void Reader_IncompleteSnapshot_NeverEqualsItselfThroughComparator()
    {
        // Dowód klasy N4/N7: awaria tego samego pola przed i po zapisie nie może dać Intended/Unchanged — reader rzuca, komparator nie dostaje snapshotu.
        var thrown = new COMException("x", unchecked((int)0x80004005));
        Func<ReceiptStateSnapshot> read = () => ReceiptSnapshotReader.Read(1, Attr(Header()),
            () => new[] { (Func<string, object?>)(n => n == "TowarId" ? throw thrown : Attr(Position())(n)) }).Snapshot;
        Assert.Throws<ReceiptEditException>(() => read());
        Assert.Throws<ReceiptEditException>(() => read());
    }

    // ----------------------------- ReceiptLoadFailure (r4 #3: mutant "Istnieje pominięte") -----------------------------

    [Fact]
    public void Load_SessionDead_SferaUnavailable_IstniejeNotAsked()
    {
        // Mutant: Istnieje przed sondą sesji -> pada; mapowanie na ReadFailed -> pada (503 = nic nie zapisano, retry).
        int asked = 0;
        var inner = new COMException("RPC", unchecked((int)0x800706BA));
        var ex = ReceiptLoadFailure.Classify(5, inner, () => false, () => { asked++; return false; });
        var su = Assert.IsType<SferaUnavailableException>(ex);
        Assert.Same(inner, su.InnerException);
        Assert.Equal(0, asked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_Alive_IstniejeFalse_NotFound(bool withLoadException)
    {
        // Mutant: 404 bez pozytywnego Istnieje==false -> pada w teście niżej; tu: pozytywny brak -> NotFound (także gdy WczytajDokument zwrócił null).
        var inner = withLoadException ? new InvalidOperationException("WczytajDokument") : null;
        var ex = Assert.IsType<ReceiptEditException>(ReceiptLoadFailure.Classify(5, inner, () => true, () => false));
        Assert.Equal(ReceiptEditError.NotFound, ex.Reason);
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public void Load_Alive_IstniejeTrue_ReadFailed_NotNotFound()
    {
        // Mutant (deklarowany w T12): `Istnieje` pominięte -> NotFound przy awarii odczytu -> pada (404 tylko przy pozytywnym braku).
        var inner = new COMException("WczytajDokument padl", unchecked((int)0x80004005));
        var ex = Assert.IsType<ReceiptEditException>(ReceiptLoadFailure.Classify(5, inner, () => true, () => true));
        Assert.Equal(ReceiptEditError.ReadFailed, ex.Reason);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void Load_Alive_IstniejeThrows_ReadFailed_InnerIsIstniejeException()
    {
        // Mutant: wyjątek Istnieje -> NotFound (zgadywanie ducha) -> pada.
        var istniejeEx = new COMException("Istnieje padlo", unchecked((int)0x80004005));
        var ex = Assert.IsType<ReceiptEditException>(ReceiptLoadFailure.Classify(5, new InvalidOperationException("load"), () => true, () => throw istniejeEx));
        Assert.Equal(ReceiptEditError.ReadFailed, ex.Reason);
        Assert.Same(istniejeEx, ex.InnerException);
    }

    // ----------------------------- ReceiptCloseGuard (r4 #2: regresja licznika/resetu) -----------------------------

    [Fact]
    public void Close_Ok_ConfirmedTrue_ReleaseOnce_NoReset()
    {
        // Mutant: reset po udanym Zamknij -> pada; brak release -> pada.
        var calls = new List<string>();
        bool ok = ReceiptCloseGuard.CloseVerified(() => calls.Add("zamknij"), () => calls.Add("release"), () => calls.Add("reset"), NullLogger.Instance);
        Assert.True(ok);
        Assert.Equal(new[] { "zamknij", "release" }, calls);
    }

    [Fact]
    public void Close_Throws_ConfirmedFalse_ReleaseThenReset()
    {
        // Mutant (r4 #2): brak ResetSessionOnSta po nieudanym Zamknij (lock po mutacji wisi w sesji) -> pada; reset przed release -> pada.
        var calls = new List<string>();
        bool ok = ReceiptCloseGuard.CloseVerified(() => { calls.Add("zamknij"); throw new COMException("Zamknij", unchecked((int)0x80004005)); },
            () => calls.Add("release"), () => calls.Add("reset"), NullLogger.Instance);
        Assert.False(ok);
        Assert.Equal(new[] { "zamknij", "release", "reset" }, calls);
    }

    [Fact]
    public void Close_ReleaseThrows_Swallowed_ResultFromZamknij()
    {
        // Mutant: wyjątek ReleaseComObject propaguje z finally (zasłania decyzję) -> pada.
        bool ok = ReceiptCloseGuard.CloseVerified(() => { }, () => throw new InvalidOperationException("RCW already released"), () => { }, NullLogger.Instance);
        Assert.True(ok);
        bool ok2 = ReceiptCloseGuard.CloseVerified(() => throw new COMException("x", 1), () => throw new InvalidOperationException("RCW"), () => { }, NullLogger.Instance);
        Assert.False(ok2);
    }

    [Fact]
    public void Close_ResetThrows_Swallowed_False()
    {
        // Mutant: wyjątek resetu propaguje -> pada (decyzja 502 musi powstać z faktów, nie z wyjątku cleanupu).
        bool ok = ReceiptCloseGuard.CloseVerified(() => throw new COMException("x", 1), () => { }, () => throw new InvalidOperationException("reset"), NullLogger.Instance);
        Assert.False(ok);
    }

    // ----------------------------- BridgeIds (r4 #6: zakres 1..int.MaxValue przed COM) -----------------------------

    [Theory]
    [InlineData("sub_1", true, 1L)]
    [InlineData("sub_2147483647", true, 2147483647L)]
    [InlineData("sub_2147483648", false, 0L)]   // > int.MaxValue: COM Long jest 32-bit
    [InlineData("sub_4294967297", false, 0L)]   // sonda Codexa: (int)4294967297 == 1 -> Istnieje sprawdzalby inny dokument
    [InlineData("sub_0", false, 0L)]
    [InlineData("sub_-5", false, 0L)]
    [InlineData("sub_", false, 0L)]
    [InlineData("sub_x", false, 0L)]
    [InlineData("abc", false, 0L)]
    [InlineData("", false, 0L)]
    [InlineData(null, false, 0L)]
    public void BridgeIds_RangeAndFormat(string? raw, bool expected, long id)
    {
        // Mutant: `(int)id` bez kontroli zakresu -> "sub_4294967297" przechodzi -> pada; brak dolnej granicy -> "sub_0" przechodzi -> pada.
        Assert.Equal(expected, BridgeIds.TryParseSubiektId(raw, out long parsed));
        Assert.Equal(id, parsed);
    }
}
