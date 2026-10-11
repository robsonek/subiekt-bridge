using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
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
/// Edycja istniejącego PZ (v0.21.0): GET szczegółów + PATCH original_number. Spec docs/specs/2026-10-11-receipts-edit-pz-0.21-design.md §7
/// (T1–T22). Logika COM jest windows-only — testujemy helpery pure (ReceiptEdit.cs), Fake (ta sama maszyna decyzji co Real) i kontroler.
/// Każdy test ma w specu mutanta (zepsuta implementacja → asercja).
/// </summary>
[Collection("FakeSferaSharedState")]
public class ReceiptEditTests
{
    private const int F19 = unchecked((int)0x80040F19); // INS_E_CANNOTLOCKOBJECT
    private const int F1A = unchecked((int)0x80040F1A); // INS_E_CONCURRENCYVIOLATION
    private const int F1D = unchecked((int)0x80040F1D); // INS_E_PERMISSIONDENIED
    private const int F64 = unchecked((int)0x80040F64); // INS_E_SU_OKRES_ZABLOKOWANY
    private const int F99 = unchecked((int)0x80040F99); // INS_E_DATA_W_OKRESIE_ZABLOKOWANYM
    private const int E14E7 = unchecked((int)0x800414E7); // INS_E_SU_OBJETYKORKOSZT_ZABLOKOWANY
    private const int FC3 = unchecked((int)0x80040FC3); // INS_E_EDYCJA_ZAKSIEGOWANEGO
    private const int F8A = unchecked((int)0x80040F8A); // INS_E_SU_DOK_BRAK_NR_ORYG
    private const int F62 = unchecked((int)0x80040F62); // INS_E_SU_DOK_NIE_ISTNIEJE
    private const int F60 = unchecked((int)0x80040F60); // INS_E_SU_BRAK_TOWARU
    private const int EFAIL = unchecked((int)0x80004005);

    public ReceiptEditTests()
    {
        FakeSferaSession.ResetReceiptsForTests();
    }

    // ----------------------------- pomocniki -----------------------------

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_receipt_edit_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static ReceiptsController Controller(FakeSferaSession fake, IdempotencyStore? store = null)
        => new(fake, store ?? NewStore(), NullLogger<ReceiptsController>.Instance);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
    private static ReceiptHeaderPatchDto Patch(string value, string? rev = null) => new(Json(JsonSerializer.Serialize(value)), rev);
    private static ReceiptHeaderPatchDto PatchNull() => new(Json("null"), null);
    private static ReceiptHeaderPatchDto PatchMissing() => new(default, null);

    private static ContractorDto Supplier() => new(
        IsPerson: false, Symbol: "DOST1", Nip: "1111111111", Name: "Dostawca Testowy Sp. z o.o.", FullName: null, FirstName: null,
        LastName: null, Email: null, Address: new AddressDto("ul. Testowa 1", "00-001", "Warszawa", "PL"));

    private static ReceiptIssueRequestDto Receipt(string originalNumber = "Client:test_buyer", int lines = 1)
    {
        var l = new List<LineDto>();
        for (int i = 0; i < lines; i++)
            l.Add(new LineDto($"590123412345{i}", $"Towar {i}", 2m + i, "szt.", 123m, 23m, UnitPriceNet: 100m + i));
        return new("", null, Supplier(), l, $"test:pz:{Guid.NewGuid():N}", "PZ testowe", originalNumber);
    }

    private static async Task<long> CreatePz(FakeSferaSession fake, string originalNumber = "Client:test_buyer", int lines = 1)
        => (await fake.CreateReceiptAsync(Receipt(originalNumber, lines), CancellationToken.None)).SubiektId;

    private static ErrorResponseDto Err(object? value) => Assert.IsType<ErrorResponseDto>(value);

    private static JsonElement DetailsOf(object? value)
    {
        var err = Err(value);
        Assert.NotNull(err.Details);
        return JsonSerializer.SerializeToElement(err.Details);
    }

    private static ReceiptItemSnapshot Item(long id, int lp = 1, long? product = 6931, decimal qty = 2m, decimal stock = 2m,
        string unit = "szt.", decimal price = 100m, decimal discount = 0m, decimal vat = 23m, ReceiptItemKind kind = ReceiptItemKind.Product)
        => new(id, lp, product, kind, qty, stock, unit, price, discount, vat);

    private static ReceiptStateSnapshot State(string originalNumber, params ReceiptItemSnapshot[] items)
        => new(originalNumber, 1, 0, items);

    private static TaskCompletionSource<bool> Tcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Bramka STA otwierana przy wyjsciu z testu (takze po padnietej asercji) - inaczej job trzymalby statyczny semafor Fake i wieszal kolejne testy.</summary>
    private static IDisposable OpenOnExit(TaskCompletionSource<bool> gate) => new GateGuard(gate);
    private sealed class GateGuard(TaskCompletionSource<bool> gate) : IDisposable { public void Dispose() => gate.TrySetResult(true); }
    private static readonly TimeSpan T5 = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Exception Com(int hresult, string message = "COM") => new COMException(message, hresult);
    private static Exception Wrapped(int hresult, string message = "COM") => new TargetInvocationException(new COMException(message, hresult));

    // ----------------------------- T1 / T1b: walidacja original_number -----------------------------

    [Fact]
    public void T1_ValidateOriginalNumber_MissingField_EmptyUpdate()
    {
        var ex = ReceiptEditFields.ValidateOriginalNumber(default);
        Assert.NotNull(ex);
        Assert.Equal(ReceiptEditError.Validation, ex!.Reason);
        Assert.Equal("EMPTY_UPDATE", ex.Code);
    }

    [Theory]
    [InlineData("null", "null")]
    [InlineData("123", "type")]
    [InlineData("{\"a\":1}", "type")]
    [InlineData("[\"x\"]", "type")]
    [InlineData("true", "type")]
    [InlineData("\"\"", "empty")]
    [InlineData("\"   \"", "empty")]
    public void T1_ValidateOriginalNumber_Invalid(string raw, string reason)
    {
        var ex = ReceiptEditFields.ValidateOriginalNumber(Json(raw));
        Assert.NotNull(ex);
        Assert.Equal("INVALID_ORIGINAL_NUMBER", ex!.Code);
        Assert.Equal(reason, JsonSerializer.SerializeToElement(ex.Details).GetProperty("reason").GetString());
    }

    [Fact]
    public void T1_ValidateOriginalNumber_Length30Ok_31TooLong()
    {
        Assert.Null(ReceiptEditFields.ValidateOriginalNumber(Json(JsonSerializer.Serialize(new string('X', 30)))));
        var ex = ReceiptEditFields.ValidateOriginalNumber(Json(JsonSerializer.Serialize(new string('X', 31))));
        Assert.NotNull(ex);
        Assert.Equal("ORIGINAL_NUMBER_TOO_LONG", ex!.Code);
        var d = JsonSerializer.SerializeToElement(ex.Details);
        Assert.Equal(30, d.GetProperty("max_length").GetInt32());
        Assert.Equal(31, d.GetProperty("length").GetInt32());
        // Trim przed liczeniem: 30 znaków + spacje = OK
        Assert.Null(ReceiptEditFields.ValidateOriginalNumber(Json(JsonSerializer.Serialize("  " + new string('X', 30) + " "))));
        Assert.Equal("X", ReceiptEditFields.NormalizeOriginalNumber(" X "));
    }

    [Fact]
    public void T1b_Deserialization_RealJson_DistinguishesMissingFromNull()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var missing = JsonSerializer.Deserialize<ReceiptHeaderPatchDto>("{}", web)!;
        Assert.Equal(JsonValueKind.Undefined, missing.OriginalNumber.ValueKind);
        var nul = JsonSerializer.Deserialize<ReceiptHeaderPatchDto>("{\"original_number\":null}", web)!;
        Assert.Equal(JsonValueKind.Null, nul.OriginalNumber.ValueKind);
        var str = JsonSerializer.Deserialize<ReceiptHeaderPatchDto>("{\"original_number\":\"X\",\"unknown\":1}", web)!;
        Assert.Equal(JsonValueKind.String, str.OriginalNumber.ValueKind);
        Assert.Equal("X", str.OriginalNumber.GetString());
        Assert.Null(str.ExpectedRevision);
        var rev = JsonSerializer.Deserialize<ReceiptHeaderPatchDto>("{\"original_number\":\"X\",\"expected_revision\":\"r1:abc\"}", web)!;
        Assert.Equal("r1:abc", rev.ExpectedRevision);
    }

    // ----------------------------- T2: external_reference z Uwag -----------------------------

    [Theory]
    [InlineData("a | ref: sys:o:1", "sys:o:1")]
    [InlineData("ref: x", "x")]
    [InlineData("sys:o:1", null)]                 // brak markera - NIE zgadujemy
    [InlineData("ref: a | ref: b", "a")]          // pierwszy marker
    [InlineData("", null)]
    [InlineData(null, null)]
    public void T2_ExtractExternalReference(string? uwagi, string? expected)
        => Assert.Equal(expected, ReceiptEditFields.ExtractExternalReference(uwagi));

    [Fact]
    public void T2_RoundTrip_WithBuild_OnlyWhenSuffixAppended()
    {
        Assert.Equal("sys:o:1", ReceiptEditFields.ExtractExternalReference(UwagiFields.Build("notatka", "sys:o:1")));
        // Build nie dokleja sufiksu, gdy token już jest w notatkach bez "ref:" - wtedy ekstraktor zwraca null (świadomie)
        Assert.Null(ReceiptEditFields.ExtractExternalReference(UwagiFields.Build("sys:o:1", "sys:o:1")));
    }

    // ----------------------------- T3: revision -----------------------------

    [Fact]
    public void T3_Revision_DeterministicAndSensitiveToEveryField()
    {
        var baseState = State("A", Item(1), Item(2, lp: 2, product: 7000));
        var r = ReceiptRevision.Compute(baseState);
        Assert.StartsWith("r1:", r);
        Assert.Equal(3 + 16, r.Length);
        Assert.Equal(r, ReceiptRevision.Compute(baseState));
        Assert.Equal(r, ReceiptRevision.Compute(State("A", Item(2, lp: 2, product: 7000), Item(1)))); // kolejność pozycji
        Assert.Equal(r, ReceiptRevision.Compute(baseState with { BookingStatus = 3 }));             // BookingStatus poza revision
        Assert.Equal(r, ReceiptRevision.Compute(State("A", Item(1, qty: 2.00004m, stock: 2.00004m), Item(2, lp: 2, product: 7000)))); // normalizacja 4 dp

        // Każde chronione pole OSOBNO (Codex r4 #3: brakowało ProductId i QuantityInStockUnits). Mutant: usunięcie pola z hasha -> pada na jego wierszu.
        var second = Item(2, lp: 2, product: 7000);
        var mutations = new (string Field, ReceiptStateSnapshot State)[]
        {
            ("OriginalNumber", State("B", Item(1), second)),
            ("Status", baseState with { Status = 0 }),
            ("PositionId", State("A", Item(3), second)),
            ("ProductId", State("A", Item(1, product: 6932), second)),
            ("ProductId->null", State("A", Item(1, product: null), second)),
            ("Kind", State("A", Item(1, kind: ReceiptItemKind.Bundle), second)),
            ("Quantity", State("A", Item(1, qty: 2.0001m), second)),
            ("QuantityInStockUnits", State("A", Item(1, stock: 2.0001m), second)),
            ("Unit", State("A", Item(1, unit: "kg"), second)),
            ("UnitPriceNet", State("A", Item(1, price: 100.0001m), second)),
            ("DiscountPercent", State("A", Item(1, discount: 1m), second)),
            ("VatRate", State("A", Item(1, vat: 8m), second)),
            ("ItemRemoved", State("A", Item(1))),
            ("ItemAdded", State("A", Item(1), second, Item(4, lp: 3))),
        };
        foreach (var (field, state) in mutations)
        {
            Assert.True(r != ReceiptRevision.Compute(state), $"revision nie reaguje na zmiane pola {field}");
        }
        Assert.Equal(mutations.Length, mutations.Select(m => ReceiptRevision.Compute(m.State)).Distinct().Count()); // każda zmiana = inny hash
    }

    // ----------------------------- T4: fakty rozchodu -----------------------------

    [Fact]
    public void T4_FromRows_StockUnitsAndDocumentUnits()
    {
        var current = new ReceiptStateSnapshot("A", 1, 0, new[] { Item(1, qty: 2m, stock: 20m) });
        var (facts, inconsistent) = ReceiptStockFacts.FromRows(new[] { new StockRow(1, 20m, 8m, 1) }, current);
        Assert.False(inconsistent);
        Assert.Equal(12m, facts[1]!.IssuedStockUnits);
        Assert.Equal(1.2m, facts[1]!.IssuedQuantity);
    }

    [Theory]
    [InlineData(1, 19, 8, true)]    // Σilosc != Ilosc
    [InlineData(1, 20, 21, true)]   // pozostalo > ilosc
    [InlineData(1, 20, -1, true)]   // pozostalo < 0
    public void T4_FromRows_InconsistentRowsGiveNull(int status, double ilosc, double pozostalo, bool expectInconsistent)
    {
        var current = new ReceiptStateSnapshot("A", status, 0, new[] { Item(1, qty: 20m, stock: 20m) });
        var (facts, inconsistent) = ReceiptStockFacts.FromRows(new[] { new StockRow(1, (decimal)ilosc, (decimal)pozostalo, 1) }, current);
        Assert.Equal(expectInconsistent, inconsistent);
        Assert.Null(facts[1]);
    }

    [Fact]
    public void T4_FromRows_StatusRules()
    {
        // status 0/3 bez wierszy -> (0, 0); z wierszami -> niespójne
        var s0 = new ReceiptStateSnapshot("A", 0, 0, new[] { Item(1, qty: 5m, stock: 5m) });
        var r0 = ReceiptStockFacts.FromRows(Array.Empty<StockRow>(), s0);
        Assert.False(r0.AnyInconsistent); Assert.Equal(0m, r0.Facts[1]!.IssuedStockUnits); Assert.Equal(0m, r0.Facts[1]!.IssuedQuantity);
        var r0rows = ReceiptStockFacts.FromRows(new[] { new StockRow(1, 5m, 5m, 1) }, s0);
        Assert.True(r0rows.AnyInconsistent); Assert.Null(r0rows.Facts[1]);
        var s3 = s0 with { Status = 3 };
        Assert.False(ReceiptStockFacts.FromRows(Array.Empty<StockRow>(), s3).AnyInconsistent);
        // status 1 bez wierszy -> niespójne
        var s1 = s0 with { Status = 1 };
        var r1 = ReceiptStockFacts.FromRows(Array.Empty<StockRow>(), s1);
        Assert.True(r1.AnyInconsistent); Assert.Null(r1.Facts[1]);
        // status 2 (unieważniony): null BEZ niespójności, z wierszami i bez
        var s2 = s0 with { Status = 2 };
        var r2a = ReceiptStockFacts.FromRows(Array.Empty<StockRow>(), s2);
        var r2b = ReceiptStockFacts.FromRows(new[] { new StockRow(1, 5m, 5m, 1) }, s2);
        Assert.False(r2a.AnyInconsistent); Assert.Null(r2a.Facts[1]);
        Assert.False(r2b.AnyInconsistent); Assert.Null(r2b.Facts[1]);
        // status 5 (zamówienie) -> null bez niespójności
        var s5 = s0 with { Status = 5 };
        var r5 = ReceiptStockFacts.FromRows(new[] { new StockRow(1, 5m, 5m, 1) }, s5);
        Assert.False(r5.AnyInconsistent); Assert.Null(r5.Facts[1]);
        // Ilosc = 0 -> Quantity null, Stock liczony
        var sz = new ReceiptStateSnapshot("A", 1, 0, new[] { Item(1, qty: 0m, stock: 0m) });
        var rz = ReceiptStockFacts.FromRows(new[] { new StockRow(1, 0m, 0m, 1) }, sz);
        Assert.False(rz.AnyInconsistent); Assert.Equal(0m, rz.Facts[1]!.IssuedStockUnits); Assert.Null(rz.Facts[1]!.IssuedQuantity);
        // kind != product -> pominięta (brak klucza), bez niespójności mimo braku wierszy
        var ss = new ReceiptStateSnapshot("A", 1, 0, new[] { Item(1, kind: ReceiptItemKind.OneOffService, product: null) });
        var rs = ReceiptStockFacts.FromRows(Array.Empty<StockRow>(), ss);
        Assert.False(rs.AnyInconsistent); Assert.False(rs.Facts.ContainsKey(1));
    }

    // ----------------------------- T5: komparator -----------------------------

    [Fact]
    public void T5_Compare_IntendedUnchangedDiverged()
    {
        var before = State("A", Item(1), Item(2, lp: 2, product: 7000));
        var intended = before with { OriginalNumber = "B" };
        Assert.Equal(ReceiptStateMatch.Intended, ReceiptStateComparer.Compare(before, intended, State("B", Item(2, lp: 2, product: 7000), Item(1))));
        Assert.Equal(ReceiptStateMatch.Unchanged, ReceiptStateComparer.Compare(before, intended, State("A", Item(1), Item(2, lp: 2, product: 7000))));
        // BookingStatus 1->2 nadal Intended (księgowość zmienia go niezależnie)
        Assert.Equal(ReceiptStateMatch.Intended, ReceiptStateComparer.Compare(before, intended, State("B", Item(1), Item(2, lp: 2, product: 7000)) with { BookingStatus = 2 }));
        // normalizacja 4 dp: 10.00004 == 10; 10.0001 != 10
        Assert.Equal(ReceiptStateMatch.Intended, ReceiptStateComparer.Compare(before, intended, State("B", Item(1, price: 100.00004m), Item(2, lp: 2, product: 7000))));
        Assert.Equal(ReceiptStateMatch.Diverged, ReceiptStateComparer.Compare(before, intended, State("B", Item(1, price: 100.0001m), Item(2, lp: 2, product: 7000))));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("unit")]
    [InlineData("vat")]
    [InlineData("discount")]
    [InlineData("kind")]
    [InlineData("status")]
    [InlineData("stock")]
    [InlineData("product")]
    [InlineData("quantity")]
    public void T5_Compare_DivergedOnAnyProtectedField(string change)
    {
        var before = State("A", Item(1), Item(2, lp: 2, product: 7000));
        var intended = before with { OriginalNumber = "B" };
        ReceiptStateSnapshot after = change switch
        {
            "missing" => State("B", Item(1)),
            "extra" => State("B", Item(1), Item(2, lp: 2, product: 7000), Item(3, lp: 3)),
            "unit" => State("B", Item(1, unit: "kg"), Item(2, lp: 2, product: 7000)),
            "vat" => State("B", Item(1, vat: 8m), Item(2, lp: 2, product: 7000)),
            "discount" => State("B", Item(1, discount: 5m), Item(2, lp: 2, product: 7000)),
            "kind" => State("B", Item(1, kind: ReceiptItemKind.Bundle), Item(2, lp: 2, product: 7000)),
            "status" => State("B", Item(1), Item(2, lp: 2, product: 7000)) with { Status = 0 },
            "stock" => State("B", Item(1, stock: 3m), Item(2, lp: 2, product: 7000)),
            "product" => State("B", Item(1, product: 1), Item(2, lp: 2, product: 7000)),
            "quantity" => State("B", Item(1, qty: 2.0001m), Item(2, lp: 2, product: 7000)),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Equal(ReceiptStateMatch.Diverged, ReceiptStateComparer.Compare(before, intended, after));
    }

    // ----------------------------- T6: powody edytowalności -----------------------------

    [Theory]
    [InlineData(10, 1, false, 0, null, true, "")]
    [InlineData(10, 2, false, 0, null, true, "CANCELLED")]
    [InlineData(2, 1, false, 0, null, true, "NOT_A_RECEIPT")]
    [InlineData(10, 1, true, 0, null, true, "PERIOD_LOCKED")]
    [InlineData(10, 1, false, 3, null, true, "BOOKED")]
    [InlineData(10, 1, false, 7, null, true, "BOOKED")]
    [InlineData(10, 1, false, 1, null, true, "EXPORTED_TO_ACCOUNTING")]
    [InlineData(10, 1, false, 2, null, true, "EXPORTED_TO_ACCOUNTING")]
    [InlineData(10, 1, false, 6, null, true, "EXPORTED_TO_ACCOUNTING")]
    [InlineData(10, 1, false, 5, null, true, "")]
    [InlineData(10, 1, false, 0, 60001L, true, "LINKED_DOCUMENT")]
    [InlineData(10, 1, false, 0, null, false, "SFERA_NOT_EDITABLE")]
    [InlineData(10, 1, null, null, null, null, "")]
    public void T6_Reasons_ExactlyOnePerFact(int dokTyp, int status, bool? periodLocked, int? booking, long? linked, bool? canEdit, string expected)
    {
        var reasons = ReceiptEditability.Reasons(dokTyp, status, periodLocked, booking, linked, canEdit);
        Assert.Equal(expected == "" ? Array.Empty<string>() : new[] { expected }, reasons);
    }

    // ----------------------------- T7 / T8: klasyfikator -----------------------------

    [Theory]
    [InlineData(F19, "", ReceiptLockReason.LockedBySession)]
    [InlineData(F1A, "", ReceiptLockReason.ConcurrentModification)]
    [InlineData(F1D, "", ReceiptLockReason.PermissionDenied)]
    [InlineData(F64, "", ReceiptLockReason.PeriodLocked)]
    [InlineData(F99, "", ReceiptLockReason.PeriodLocked)]
    [InlineData(E14E7, "", ReceiptLockReason.PeriodLocked)]
    [InlineData(FC3, "", ReceiptLockReason.Booked)]
    [InlineData(F19, "Dokument zaksięgowany", ReceiptLockReason.LockedBySession)]     // HRESULT ma pierwszeństwo nad frazą
    [InlineData(EFAIL, "Dokument jest zablokowany do edycji", ReceiptLockReason.NotEditable)]
    [InlineData(EFAIL, "Nie można edytować dokumentu", ReceiptLockReason.NotEditable)]
    [InlineData(EFAIL, "Edycja jest ograniczona", ReceiptLockReason.NotEditable)]
    [InlineData(EFAIL, "Dane zostały zmienione w tle.", ReceiptLockReason.ConcurrentModification)]
    [InlineData(EFAIL, "Okres jest zablokowany.", ReceiptLockReason.PeriodLocked)]
    [InlineData(EFAIL, "Data jest sprzed daty blokady.", ReceiptLockReason.PeriodLocked)]
    [InlineData(EFAIL, "Dostęp zabroniony.", ReceiptLockReason.PermissionDenied)]
    [InlineData(EFAIL, "Dokument zaksięgowany", ReceiptLockReason.Booked)]
    [InlineData(EFAIL, "Nie można edytować zaksięgowanego obiektu.", ReceiptLockReason.NotEditable)]
    [InlineData(EFAIL, "Dokument niezaksięgowany", null)]                               // granica słowa: negacja NIE pasuje
    [InlineData(EFAIL, "edycja rabatu", null)]                                          // fragment "edyc" nie wystarcza
    [InlineData(EFAIL, "", null)]
    [InlineData(null, "cokolwiek", null)]
    public void T7_ClassifyLock_HresultThenPhrases(int? hresult, string message, ReceiptLockReason? expected)
        => Assert.Equal(expected, ReceiptSaveFailureClassifier.ClassifyLock(hresult, message));

    [Fact]
    public void T7_Classify_UnwrapsTargetInvocation()
    {
        var c = ReceiptSaveFailureClassifier.Classify(Wrapped(F19, "lock"));
        Assert.Equal(ReceiptFailureKind.Lock, c.Kind);
        Assert.Equal(ReceiptLockReason.LockedBySession, c.LockReason);
        Assert.Equal(F19, c.Hresult);
        Assert.Equal("lock", c.Message);
        var (hr, msg) = ReceiptSaveFailureClassifier.Describe(new TargetInvocationException(new InvalidOperationException("x")));
        Assert.Null(hr); Assert.Equal("x", msg);
        Assert.IsType<COMException>(ReceiptSaveFailureClassifier.Unwrap(new TargetInvocationException(new TargetInvocationException(Com(F19)))));
    }

    [Fact]
    public void T8_Classify_HresultPrecedenceOverPhrases_ForValidationAndNotFound()
    {
        var v = ReceiptSaveFailureClassifier.Classify(Com(F8A, "Nie można edytować: należy podać numer oryginalnego dokumentu."));
        Assert.Equal(ReceiptFailureKind.Validation, v.Kind);
        Assert.Equal("INVALID_ORIGINAL_NUMBER", v.ValidationCode);
        var nf = ReceiptSaveFailureClassifier.Classify(Com(F62, "Dokument jest zablokowany do edycji"));
        Assert.Equal(ReceiptFailureKind.NotFound, nf.Kind);
        var l = ReceiptSaveFailureClassifier.Classify(Com(EFAIL, "Dokument jest zablokowany do edycji"));
        Assert.Equal(ReceiptFailureKind.Lock, l.Kind);
        Assert.Equal(ReceiptLockReason.NotEditable, l.LockReason);
        var u = ReceiptSaveFailureClassifier.Classify(Com(F60, "Brak towaru w magazynie."));
        Assert.Equal(ReceiptFailureKind.Unclassified, u.Kind);
        Assert.Equal(ReceiptFailureKind.Unclassified, ReceiptSaveFailureClassifier.Classify(new InvalidOperationException("x")).Kind);
        Assert.Equal("INVALID_ORIGINAL_NUMBER", ReceiptSaveFailureClassifier.ClassifyValidation(F8A, ""));
        Assert.Null(ReceiptSaveFailureClassifier.ClassifyValidation(F60, ""));
        Assert.True(ReceiptSaveFailureClassifier.IsNotFound(F62));
        Assert.False(ReceiptSaveFailureClassifier.IsNotFound(F19));
        Assert.Null(ReceiptSaveFailureClassifier.ClassifyLock(F8A, "Nie można edytować")); // HRESULT znany innej tabeli -> frazy nie decydują
    }

    // ----------------------------- T9: Decide -----------------------------

    private static Exception? ExOf(string kind) => kind switch
    {
        "none" => null,
        "lock" => Com(F19, "lock"),
        "lockphrase" => Com(EFAIL, "Dokument jest zablokowany do edycji"),
        "validation" => Com(F8A, "brak numeru"),
        "validationphrase" => Com(F8A, "Nie można edytować: należy podać numer oryginalnego dokumentu"),
        "notfound" => Com(F62, "usunięty"),
        "other" => Com(EFAIL, "Bład"),
        "plain" => new InvalidOperationException("x"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    // R1: fazy przed Zapisz - bez odczytu kontrolnego
    [InlineData(ReceiptSavePhase.BeforeFirstSet, "lock", null, true, false, null, ReceiptEditError.Locked, "LockedBySession", null)]
    [InlineData(ReceiptSavePhase.Modify, "lock", null, true, false, null, ReceiptEditError.Locked, "LockedBySession", null)]
    [InlineData(ReceiptSavePhase.Modify, "lockphrase", null, true, false, null, ReceiptEditError.Locked, "NotEditable", null)]
    [InlineData(ReceiptSavePhase.Modify, "validation", null, true, false, null, ReceiptEditError.Validation, null, null)]
    [InlineData(ReceiptSavePhase.Modify, "validationphrase", null, true, false, null, ReceiptEditError.Validation, null, null)]
    [InlineData(ReceiptSavePhase.Modify, "notfound", null, true, false, false, ReceiptEditError.NotFound, null, null)]
    [InlineData(ReceiptSavePhase.Modify, "notfound", null, true, false, null, ReceiptEditError.UpdateUnverified, null, false)]
    [InlineData(ReceiptSavePhase.Modify, "other", null, true, false, null, ReceiptEditError.UpdateFailed, null, null)]
    [InlineData(ReceiptSavePhase.Modify, "plain", null, true, false, null, ReceiptEditError.UpdateFailed, null, null)]
    // R2 sessionReset ma pierwszeństwo nad wszystkim (także nad existsAfter=false i Intended)
    [InlineData(ReceiptSavePhase.Save, "none", ReceiptStateMatch.Intended, true, true, true, ReceiptEditError.UpdateUnverified, null, true)]
    [InlineData(ReceiptSavePhase.Save, "other", null, true, true, false, ReceiptEditError.UpdateUnverified, null, false)]
    [InlineData(ReceiptSavePhase.ReadBack, "none", null, true, true, null, ReceiptEditError.UpdateUnverified, null, true)]
    // R3 !closeConfirmed
    [InlineData(ReceiptSavePhase.Save, "none", ReceiptStateMatch.Intended, false, false, true, ReceiptEditError.UpdateUnverified, null, true)]
    [InlineData(ReceiptSavePhase.ReadBack, "lock", ReceiptStateMatch.Unchanged, false, false, true, ReceiptEditError.UpdateUnverified, null, false)]
    // R4 existsAfter=false -> 404 (przed match==null)
    [InlineData(ReceiptSavePhase.ReadBack, "none", null, true, false, false, ReceiptEditError.NotFound, null, null)]
    [InlineData(ReceiptSavePhase.ReadBack, "notfound", null, true, false, false, ReceiptEditError.NotFound, null, null)]
    // R5 match==null
    [InlineData(ReceiptSavePhase.ReadBack, "none", null, true, false, null, ReceiptEditError.UpdateUnverified, null, true)]
    [InlineData(ReceiptSavePhase.ReadBack, "other", null, true, false, true, ReceiptEditError.UpdateUnverified, null, false)]
    // R6 Intended -> 200 (null), także z wyjątkiem
    [InlineData(ReceiptSavePhase.ReadBack, "none", ReceiptStateMatch.Intended, true, false, true, null, null, null)]
    [InlineData(ReceiptSavePhase.ReadBack, "other", ReceiptStateMatch.Intended, true, false, true, null, null, null)]
    [InlineData(ReceiptSavePhase.Save, "lock", ReceiptStateMatch.Intended, true, false, true, null, null, null)]
    // R7 Diverged
    [InlineData(ReceiptSavePhase.ReadBack, "none", ReceiptStateMatch.Diverged, true, false, true, ReceiptEditError.UpdateUnverified, null, null)]
    [InlineData(ReceiptSavePhase.ReadBack, "other", ReceiptStateMatch.Diverged, true, false, true, ReceiptEditError.UpdateUnverified, null, null)]
    // R8 Unchanged
    [InlineData(ReceiptSavePhase.ReadBack, "none", ReceiptStateMatch.Unchanged, true, false, true, ReceiptEditError.Locked, "NotSaved", null)]
    [InlineData(ReceiptSavePhase.ReadBack, "lock", ReceiptStateMatch.Unchanged, true, false, true, ReceiptEditError.Locked, "LockedBySession", null)]
    [InlineData(ReceiptSavePhase.ReadBack, "lockphrase", ReceiptStateMatch.Unchanged, true, false, true, ReceiptEditError.Locked, "NotEditable", null)]
    [InlineData(ReceiptSavePhase.ReadBack, "validation", ReceiptStateMatch.Unchanged, true, false, true, ReceiptEditError.Validation, null, null)]
    [InlineData(ReceiptSavePhase.ReadBack, "validationphrase", ReceiptStateMatch.Unchanged, true, false, true, ReceiptEditError.Validation, null, null)]
    [InlineData(ReceiptSavePhase.ReadBack, "notfound", ReceiptStateMatch.Unchanged, true, false, true, ReceiptEditError.UpdateUnverified, null, false)]
    [InlineData(ReceiptSavePhase.ReadBack, "other", ReceiptStateMatch.Unchanged, true, false, true, ReceiptEditError.UpdateFailed, null, null)]
    [InlineData(ReceiptSavePhase.ReadBack, "plain", ReceiptStateMatch.Unchanged, true, false, true, ReceiptEditError.UpdateFailed, null, null)]
    public void T9_Decide_Rules(ReceiptSavePhase phase, string exKind, ReceiptStateMatch? match, bool closeConfirmed, bool sessionReset, bool? existsAfter,
        ReceiptEditError? expectedReason, string? expectedLock, bool? expectedSavedProbably)
    {
        var ex = ExOf(exKind);
        var result = ReceiptEditOutcome.Decide(phase, ex, match, closeConfirmed, sessionReset, existsAfter);
        if (expectedReason is null) { Assert.Null(result); return; }
        Assert.NotNull(result);
        Assert.Equal(expectedReason, result!.Reason);
        Assert.Equal(expectedLock, result.LockReason?.ToString());
        Assert.Equal(expectedSavedProbably, result.SavedProbably);
        if (expectedReason == ReceiptEditError.Validation)
        {
            Assert.Equal("INVALID_ORIGINAL_NUMBER", result.Code);
            var d = JsonSerializer.SerializeToElement(result.Details);
            Assert.Equal("sfera", d.GetProperty("source").GetString());
            Assert.Equal("0x80040F8A", d.GetProperty("hresult").GetString());
        }
        if (phase is ReceiptSavePhase.Save or ReceiptSavePhase.ReadBack && expectedReason is ReceiptEditError.UpdateFailed or ReceiptEditError.Locked)
        {
            Assert.Equal(phase, result.Phase);
        }
    }

    [Fact]
    public void T9_Decide_Invariants_OverFullMatrix()
    {
        var phases = new[] { ReceiptSavePhase.Save, ReceiptSavePhase.ReadBack };
        var exKinds = new[] { "none", "lock", "validation", "other" };
        var matches = new ReceiptStateMatch?[] { ReceiptStateMatch.Intended, ReceiptStateMatch.Unchanged, ReceiptStateMatch.Diverged, null };
        var bools = new[] { true, false };
        var exists = new bool?[] { null, false, true };
        int cells = 0;
        foreach (var ph in phases) foreach (var ek in exKinds) foreach (var m in matches) foreach (var close in bools) foreach (var reset in bools) foreach (var ea in exists)
        {
            if (ea == false && m is not null)
            {
                Assert.Throws<ArgumentException>(() => ReceiptEditOutcome.Decide(ph, ExOf(ek), m, close, reset, ea));
                continue;
            }
            var r = ReceiptEditOutcome.Decide(ph, ExOf(ek), m, close, reset, ea);
            cells++;
            if (reset) { Assert.Equal(ReceiptEditError.UpdateUnverified, r!.Reason); Assert.Equal(ek == "none", r.SavedProbably); continue; }
            if (!close) { Assert.Equal(ReceiptEditError.UpdateUnverified, r!.Reason); continue; }
            if (ea == false) { Assert.Equal(ReceiptEditError.NotFound, r!.Reason); continue; }
            if (m is null) { Assert.Equal(ReceiptEditError.UpdateUnverified, r!.Reason); continue; }
            if (m == ReceiptStateMatch.Intended) { Assert.Null(r); continue; }
            if (m == ReceiptStateMatch.Diverged) { Assert.Equal(ReceiptEditError.UpdateUnverified, r!.Reason); Assert.Null(r.SavedProbably); continue; }
            // Unchanged
            Assert.Equal(ek switch { "none" => ReceiptEditError.Locked, "lock" => ReceiptEditError.Locked, "validation" => ReceiptEditError.Validation, _ => ReceiptEditError.UpdateFailed }, r!.Reason);
            if (ek == "none") Assert.Equal(ReceiptLockReason.NotSaved, r.LockReason);
        }
        Assert.Equal(2 * 4 * (3 * 2 * 2 * 2 + 1 * 2 * 2 * 3) - 0, cells + 0 * cells);
        // 2 fazy × 4 wyjątki × [3 niepuste match × 2 close × 2 reset × 2 exists(null,true) + match null × 2 × 2 × 3 exists]
        Assert.Equal(2 * 4 * (3 * 2 * 2 * 2 + 2 * 2 * 3), cells);
    }

    [Fact]
    public void T9_Decide_PhaseBeforeSave_RequiresException()
        => Assert.Throws<ArgumentException>(() => ReceiptEditOutcome.Decide(ReceiptSavePhase.Modify, null, null, true, false, null));

    // ----------------------------- T10–T21: Fake + kontroler -----------------------------

    [Fact]
    public async Task T10_Patch_HappyPath_ThenSameValueIsNoOp()
    {
        var fake = new FakeSferaSession();
        long id = await CreatePz(fake);
        var c = Controller(fake);
        var before = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{id}", CancellationToken.None)).Result).value!;
        Assert.Equal("Client:test_buyer", before.OriginalNumber);

        var (status, value) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("FZ 1/01/2026"), "k10-a", CancellationToken.None)).Result);
        Assert.Equal(200, status);
        var dto = Assert.IsType<ReceiptDetailDto>(value);
        Assert.Equal("FZ 1/01/2026", dto.OriginalNumber);
        Assert.NotEqual(before.Revision, dto.Revision);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
        var after = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{id}", CancellationToken.None)).Result).value!;
        Assert.Equal(dto.Revision, after.Revision);
        Assert.Equal(dto.OriginalNumber, after.OriginalNumber);

        // ta sama wartość (nowy klucz = cache miss) -> 200 bez zapisu, nawet gdy GUI trzyma lock
        FakeSferaSession.SetReceiptFactsForTests(id, f => f.GuiLocked = true);
        var (s2, v2) = Unwrap((await c.PatchHeader($"sub_{id}", Patch(" FZ 1/01/2026 "), "k10-b", CancellationToken.None)).Result);
        Assert.Equal(200, s2);
        Assert.Equal("FZ 1/01/2026", Assert.IsType<ReceiptDetailDto>(v2).OriginalNumber);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
    }

    [Fact]
    public async Task T11_Patch_ValidationBeforeSfera_LoadCountZero()
    {
        var fake = new FakeSferaSession();
        long id = await CreatePz(fake);
        var c = Controller(fake);
        async Task Expect(ReceiptHeaderPatchDto body, int status, string code)
        {
            var (s, v) = Unwrap((await c.PatchHeader($"sub_{id}", body, $"k11-{Guid.NewGuid():N}", CancellationToken.None)).Result);
            Assert.Equal(status, s);
            Assert.Equal(code, Err(v).Code);
        }
        await Expect(PatchMissing(), 422, "EMPTY_UPDATE");
        await Expect(PatchNull(), 422, "INVALID_ORIGINAL_NUMBER");
        await Expect(new(Json("123"), null), 422, "INVALID_ORIGINAL_NUMBER");
        await Expect(new(Json("{\"a\":1}"), null), 422, "INVALID_ORIGINAL_NUMBER");
        await Expect(new(Json("[1]"), null), 422, "INVALID_ORIGINAL_NUMBER");
        await Expect(new(Json("false"), null), 422, "INVALID_ORIGINAL_NUMBER");
        await Expect(Patch(""), 422, "INVALID_ORIGINAL_NUMBER");
        await Expect(Patch("   "), 422, "INVALID_ORIGINAL_NUMBER");
        await Expect(Patch(new string('X', 31)), 422, "ORIGINAL_NUMBER_TOO_LONG");
        Assert.Equal(0, FakeSferaSession.LoadCountForTests(id));
        Assert.Equal(0, FakeSferaSession.SaveCountForTests(id));

        var (sb, vb) = Unwrap((await c.PatchHeader("abc", Patch("X"), "k11-id", CancellationToken.None)).Result);
        Assert.Equal(422, sb); Assert.Equal("INVALID_BRIDGE_ID", Err(vb).Code);
        var (sg, vg) = Unwrap((await c.Get("abc", CancellationToken.None)).Result);
        Assert.Equal(422, sg); Assert.Equal("INVALID_BRIDGE_ID", Err(vg).Code);
        var (sk, vk) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), null, CancellationToken.None)).Result);
        Assert.Equal(400, sk); Assert.Equal("MISSING_IDEMPOTENCY_KEY", Err(vk).Code);
        Assert.Equal(0, FakeSferaSession.LoadCountForTests(id));
    }

    [Fact]
    public async Task T12_Patch_NotFound_ReadFailed_LinkedIdReadFails_Cancelled_NotAReceipt_Revision()
    {
        var fake = new FakeSferaSession();
        var c = Controller(fake);
        var (s404, v404) = Unwrap((await c.PatchHeader("sub_999999999", Patch("X"), "k12-404", CancellationToken.None)).Result);
        Assert.Equal(404, s404); Assert.Equal("RECEIPT_NOT_FOUND", Err(v404).Code);
        var (g404, gv) = Unwrap((await c.Get("sub_999999999", CancellationToken.None)).Result);
        Assert.Equal(404, g404); Assert.Equal("RECEIPT_NOT_FOUND", Err(gv).Code);

        long idRead = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(idRead, f => f.ReadFails = true);
        var (s502, v502) = Unwrap((await c.PatchHeader($"sub_{idRead}", Patch("X"), "k12-502", CancellationToken.None)).Result);
        Assert.Equal(502, s502); Assert.Equal("SUBIEKT_QUERY_FAILED", Err(v502).Code);
        var (g502, gv2) = Unwrap((await c.Get($"sub_{idRead}", CancellationToken.None)).Result);
        Assert.Equal(502, g502); Assert.Equal("SUBIEKT_QUERY_FAILED", Err(gv2).Code);

        // awaria odczytu DoDokumentuId != "brak linku" (Codex r3 #1)
        long idLink = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(idLink, f => f.LinkedIdReadFails = true);
        var (gl, glv) = Unwrap((await c.Get($"sub_{idLink}", CancellationToken.None)).Result);
        Assert.Equal(502, gl); Assert.Equal("SUBIEKT_QUERY_FAILED", Err(glv).Code);
        var (pl, plv) = Unwrap((await c.PatchHeader($"sub_{idLink}", Patch("X"), "k12-link", CancellationToken.None)).Result);
        Assert.Equal(502, pl); Assert.Equal("SUBIEKT_QUERY_FAILED", Err(plv).Code);
        Assert.Equal(0, FakeSferaSession.SaveCountForTests(idLink));

        long idCanc = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(idCanc, f => f.Cancelled = true);
        var (sc, vc) = Unwrap((await c.PatchHeader($"sub_{idCanc}", Patch("X"), "k12-canc", CancellationToken.None)).Result);
        Assert.Equal(409, sc); Assert.Equal("RECEIPT_CANCELLED", Err(vc).Code);
        Assert.Equal(0, FakeSferaSession.SaveCountForTests(idCanc));

        long idFs = FakeSferaSession.SeedDocumentForTests(DokTyp.FS);
        var (sfs, vfs) = Unwrap((await c.PatchHeader($"sub_{idFs}", Patch("X"), "k12-fs", CancellationToken.None)).Result);
        Assert.Equal(422, sfs); Assert.Equal("UNSUPPORTED_DOCUMENT_TYPE", Err(vfs).Code);

        long idRev = await CreatePz(fake);
        var current = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{idRev}", CancellationToken.None)).Result).value!;
        var (sr, vr) = Unwrap((await c.PatchHeader($"sub_{idRev}", Patch("X", "r1:0000000000000000"), "k12-rev", CancellationToken.None)).Result);
        Assert.Equal(409, sr); Assert.Equal("RECEIPT_REVISION_MISMATCH", Err(vr).Code);
        Assert.Equal(current.Revision, DetailsOf(vr).GetProperty("current_revision").GetString());
        Assert.Equal(0, FakeSferaSession.SaveCountForTests(idRev));
        var (sok, vok) = Unwrap((await c.PatchHeader($"sub_{idRev}", Patch("X", current.Revision), "k12-rev2", CancellationToken.None)).Result);
        Assert.Equal(200, sok); Assert.Equal("X", Assert.IsType<ReceiptDetailDto>(vok).OriginalNumber);
    }

    [Fact]
    public async Task T13_Patch_GuiLocked_And_NotEditable()
    {
        var fake = new FakeSferaSession();
        long id = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(id, f => { f.GuiLocked = true; f.SferaCanEdit = true; f.LinkedDocument = (60001, "FZ 7/2026", "FZ"); });
        var c = Controller(fake);
        var (s, v) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k13-a", CancellationToken.None)).Result);
        Assert.Equal(409, s);
        var err = Err(v);
        Assert.Equal("RECEIPT_LOCKED", err.Code);
        Assert.Equal(30, err.RetryAfterSeconds);
        var d = DetailsOf(v);
        Assert.Equal("LOCKED_BY_SESSION", d.GetProperty("reason").GetString());
        Assert.Equal("Modify", d.GetProperty("phase").GetString());
        Assert.Equal("0x80040F19", d.GetProperty("hresult").GetString());
        Assert.True(d.GetProperty("sfera_can_edit").GetBoolean());
        Assert.Equal("FZ 7/2026", d.GetProperty("linked_documents")[0].GetProperty("number").GetString());
        Assert.Equal(0, FakeSferaSession.SaveCountForTests(id));
        Assert.Equal("Client:test_buyer", FakeSferaSession.OriginalNumberForTests(id));

        FakeSferaSession.SetReceiptFactsForTests(id, f => { f.GuiLocked = false; f.ZapiszThrows = (EFAIL, "Dokument jest zablokowany do edycji", false); });
        var (s2, v2) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k13-b", CancellationToken.None)).Result);
        Assert.Equal(409, s2);
        Assert.Equal("RECEIPT_LOCKED", Err(v2).Code);
        Assert.Null(Err(v2).RetryAfterSeconds);
        var d2 = DetailsOf(v2);
        Assert.Equal("NOT_EDITABLE", d2.GetProperty("reason").GetString());
        Assert.Equal("Save", d2.GetProperty("phase").GetString());
        Assert.Equal("Dokument jest zablokowany do edycji", d2.GetProperty("sfera_message").GetString());
        Assert.Equal("Client:test_buyer", FakeSferaSession.OriginalNumberForTests(id));
    }

    [Theory]
    [InlineData("FZ")]
    [InlineData("KFS")]
    public async Task T14_Patch_LinkedDocument_PolicyA_Allows(string type)
    {
        var fake = new FakeSferaSession();
        long id = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(id, f => f.LinkedDocument = (60001, $"{type} 7/2026", type));
        var c = Controller(fake);
        var get = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{id}", CancellationToken.None)).Result).value!;
        Assert.Single(get.LinkedDocuments);
        Assert.Equal(type, get.LinkedDocuments[0].Type);
        Assert.Equal("linked", get.LinkedDocuments[0].Relation);
        Assert.Equal(60001, get.LinkedDocuments[0].SubiektId);
        Assert.Contains("LINKED_DOCUMENT", get.Editability.Reasons);
        var (s, v) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("FZ 1/01/2026"), "k14", CancellationToken.None)).Result);
        Assert.Equal(200, s);
        Assert.Equal("FZ 1/01/2026", Assert.IsType<ReceiptDetailDto>(v).OriginalNumber);
    }

    [Fact]
    public async Task T15_Patch_SaveOutcomes_ThroughDecide()
    {
        var fake = new FakeSferaSession();
        var c = Controller(fake);

        // Zapisz rzuca PO commicie -> odczyt kontrolny = stan docelowy -> 200
        long id1 = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(id1, f => f.ZapiszThrows = (EFAIL, "timeout", true));
        var (s1, v1) = Unwrap((await c.PatchHeader($"sub_{id1}", Patch("X"), "k15-1", CancellationToken.None)).Result);
        Assert.Equal(200, s1); Assert.Equal("X", Assert.IsType<ReceiptDetailDto>(v1).OriginalNumber);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id1));

        // odczyt kontrolny pada -> 502, klucz NIE w cache, GET pokazuje nowy stan, ponowienie bez expected_revision -> 200 no-op
        long id2 = await CreatePz(fake);
        var store2 = NewStore();
        var c2 = Controller(fake, store2);
        FakeSferaSession.SetReceiptFactsForTests(id2, f => f.ReadBackFails = true);
        var (s2, v2) = Unwrap((await c2.PatchHeader($"sub_{id2}", Patch("X"), "k15-2", CancellationToken.None)).Result);
        Assert.Equal(502, s2); Assert.Equal("RECEIPT_UPDATE_UNVERIFIED", Err(v2).Code);
        Assert.True(DetailsOf(v2).GetProperty("saved_probably").GetBoolean());
        Assert.Null(await store2.TryGetAsync<ReceiptDetailDto>("k15-2", CancellationToken.None));
        FakeSferaSession.SetReceiptFactsForTests(id2, f => f.ReadBackFails = false);
        Assert.Equal("X", ((ReceiptDetailDto)Unwrap((await c2.Get($"sub_{id2}", CancellationToken.None)).Result).value!).OriginalNumber);
        var (s2b, v2b) = Unwrap((await c2.PatchHeader($"sub_{id2}", Patch("X"), "k15-2", CancellationToken.None)).Result);
        Assert.Equal(200, s2b); Assert.Equal(1, FakeSferaSession.SaveCountForTests(id2));

        // Zamknij niepotwierdzone -> 502 + sesja zresetowana (lock po mutacji nie może wisieć; Codex r4 #2) -> kolejne żądanie na świeżej sesji = 200
        long id3 = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(id3, f => f.CloseFails = true);
        var (s3, v3) = Unwrap((await c.PatchHeader($"sub_{id3}", Patch("X"), "k15-3", CancellationToken.None)).Result);
        Assert.Equal(502, s3); Assert.Equal("RECEIPT_UPDATE_UNVERIFIED", Err(v3).Code);
        Assert.Equal(1, FakeSferaSession.SessionResetCountForTests);
        FakeSferaSession.SetReceiptFactsForTests(id3, f => f.CloseFails = false);
        var (s3b, v3b) = Unwrap((await c.PatchHeader($"sub_{id3}", Patch("Y"), "k15-3b", CancellationToken.None)).Result);
        Assert.Equal(200, s3b); Assert.Equal("Y", Assert.IsType<ReceiptDetailDto>(v3b).OriginalNumber);
        Assert.Equal(2, FakeSferaSession.SaveCountForTests(id3));

        // reset sesji po zapisie -> 502
        long id4 = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(id4, f => f.SessionResetAfterSave = true);
        var (s4, v4) = Unwrap((await c.PatchHeader($"sub_{id4}", Patch("X"), "k15-4", CancellationToken.None)).Result);
        Assert.Equal(502, s4); Assert.Equal("RECEIPT_UPDATE_UNVERIFIED", Err(v4).Code);

        // cicha odmowa -> 409 NOT_SAVED, stan sprzed, revision bez zmian
        long id5 = await CreatePz(fake);
        var rev5 = ((ReceiptDetailDto)Unwrap((await c.Get($"sub_{id5}", CancellationToken.None)).Result).value!).Revision;
        FakeSferaSession.SetReceiptFactsForTests(id5, f => f.RefuseSaveSilently = true);
        var (s5, v5) = Unwrap((await c.PatchHeader($"sub_{id5}", Patch("X"), "k15-5", CancellationToken.None)).Result);
        Assert.Equal(409, s5); Assert.Equal("RECEIPT_LOCKED", Err(v5).Code);
        Assert.Equal("NOT_SAVED", DetailsOf(v5).GetProperty("reason").GetString());
        Assert.Null(Err(v5).RetryAfterSeconds);
        Assert.Equal("Client:test_buyer", FakeSferaSession.OriginalNumberForTests(id5));
        Assert.Equal(rev5, ((ReceiptDetailDto)Unwrap((await c.Get($"sub_{id5}", CancellationToken.None)).Result).value!).Revision);

        // Sfera odrzuca F8A -> 422 z details.source = sfera
        long id6 = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(id6, f => f.ZapiszThrows = (F8A, "Należy podać numer oryginalnego dokumentu.", false));
        var (s6, v6) = Unwrap((await c.PatchHeader($"sub_{id6}", Patch("X"), "k15-6", CancellationToken.None)).Result);
        Assert.Equal(422, s6); Assert.Equal("INVALID_ORIGINAL_NUMBER", Err(v6).Code);
        var d6 = DetailsOf(v6);
        Assert.Equal("sfera", d6.GetProperty("source").GetString());
        Assert.Equal("0x80040F8A", d6.GetProperty("hresult").GetString());
        Assert.Equal("Należy podać numer oryginalnego dokumentu.", d6.GetProperty("sfera_message").GetString());

        // Zapisz rzuca bez commitu, niesklasyfikowane -> 500 RECEIPT_UPDATE_FAILED
        long id7 = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(id7, f => f.ZapiszThrows = (EFAIL, "Nieznany blad", false));
        var (s7, v7) = Unwrap((await c.PatchHeader($"sub_{id7}", Patch("X"), "k15-7", CancellationToken.None)).Result);
        Assert.Equal(500, s7); Assert.Equal("RECEIPT_UPDATE_FAILED", Err(v7).Code);
        Assert.Equal("0x80004005", DetailsOf(v7).GetProperty("hresult").GetString());
        Assert.Equal("Save", DetailsOf(v7).GetProperty("phase").GetString());
    }

    [Fact]
    public async Task T16_Replay_Semantics()
    {
        var fake = new FakeSferaSession();
        var store = NewStore();
        var c = Controller(fake, store);
        long id = await CreatePz(fake);
        var (s1, v1) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k16", CancellationToken.None)).Result);
        Assert.Equal(200, s1);
        // drugi call tym samym kluczem i INNYM body -> cached 200 (bez zapisu)
        var (s2, v2) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("Y"), "k16", CancellationToken.None)).Result);
        Assert.Equal(200, s2);
        Assert.Equal("X", Assert.IsType<ReceiptDetailDto>(v2).OriginalNumber);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
        // SEKWENCYJNIE dwa wywołania tym samym body bez cache (dwa klucze) -> drugie no-op po wartości (współbieżność: T16b)
        var (s3, _) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k16-b", CancellationToken.None)).Result);
        Assert.Equal(200, s3);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
        // Subiekt niedostępny przy replay -> 503, klucz zostaje
        fake.SferaUnavailableForTests = true;
        var (s4, v4) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k16", CancellationToken.None)).Result);
        Assert.Equal(503, s4); Assert.Equal("SUBIEKT_UNAVAILABLE", Err(v4).Code);
        Assert.NotNull(await store.TryGetAsync<ReceiptDetailDto>("k16", CancellationToken.None));
        fake.SferaUnavailableForTests = false;
        // odczyt pada przy replay -> 502, klucz zostaje
        FakeSferaSession.SetReceiptFactsForTests(id, f => f.ReadFails = true);
        var (s5, v5) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k16", CancellationToken.None)).Result);
        Assert.Equal(502, s5); Assert.Equal("SUBIEKT_QUERY_FAILED", Err(v5).Code);
        Assert.NotNull(await store.TryGetAsync<ReceiptDetailDto>("k16", CancellationToken.None));
        FakeSferaSession.SetReceiptFactsForTests(id, f => f.ReadFails = false);
        // dokument usunięty -> klucz skasowany -> 404
        FakeSferaSession.DeleteDocumentForTests(id);
        var (s6, v6) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k16", CancellationToken.None)).Result);
        Assert.Equal(404, s6); Assert.Equal("RECEIPT_NOT_FOUND", Err(v6).Code);
        Assert.Null(await store.TryGetAsync<ReceiptDetailDto>("k16", CancellationToken.None));
    }

    [Fact]
    public async Task T16b_ConcurrentSameKey_NoCache_QueuedBehindGate_SingleSave()
    {
        // DOWODZI: drugie żądanie tym samym kluczem i body, WYSŁANE gdy pierwsze stoi na bramce STA przed Zapisz (cache pusty), kończy się 200 i nie
        // powoduje drugiego zapisu (no-op po wartości), a klucz ląduje w cache. NIE dowodzi, że drugi job czekał na semaforze (brak haka „job w kolejce”
        // — Codex r6; wariant (b)): Fake bez kolejki (MF4) czerwieni ten test tylko przy części harmonogramów.
        var fake = new FakeSferaSession();
        var store = NewStore();
        var c = Controller(fake, store);
        long id = await CreatePz(fake);
        var gate = Tcs(); var started = Tcs();
        using var _gate = OpenOnExit(gate);
        FakeSferaSession.SetReceiptFactsForTests(id, f => { f.StaGate = gate; f.StaStarted = started; });
        var first = c.PatchHeader($"sub_{id}", Patch("X"), "k16b", CancellationToken.None);
        await started.Task.WaitAsync(T5, Ct);
        var second = c.PatchHeader($"sub_{id}", Patch("X"), "k16b", CancellationToken.None);
        gate.SetResult(true);
        var (s1, v1) = Unwrap((await first.WaitAsync(T5, Ct)).Result);
        var (s2, v2) = Unwrap((await second.WaitAsync(T5, Ct)).Result);
        Assert.Equal(200, s1); Assert.Equal("X", Assert.IsType<ReceiptDetailDto>(v1).OriginalNumber);
        Assert.Equal(200, s2); Assert.Equal("X", Assert.IsType<ReceiptDetailDto>(v2).OriginalNumber);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
        Assert.NotNull(await store.TryGetAsync<ReceiptDetailDto>("k16b", CancellationToken.None));
    }

    [Fact]
    public async Task T16c_Replay_CachedDocumentGone_404_WithoutTouchingMutation()
    {
        // Spec §4.4: cached dokument pozytywnie nie istnieje -> DeleteAsync + 404 OD RAZU. Klucz z cache wskazuje dokument A; URL wskazuje B.
        // Mutant (r4 #7): po DeleteAsync kontroler idzie dalej do UpdateReceiptHeaderAsync(B) -> 200 i SaveCount(B)=1 -> pada.
        var fake = new FakeSferaSession();
        var store = NewStore();
        var c = Controller(fake, store);
        long a = await CreatePz(fake);
        long b = await CreatePz(fake);
        var (sa, _) = Unwrap((await c.PatchHeader($"sub_{a}", Patch("X"), "k16c", CancellationToken.None)).Result);
        Assert.Equal(200, sa);
        FakeSferaSession.DeleteDocumentForTests(a);
        int loadsB = FakeSferaSession.LoadCountForTests(b);
        var (sb, vb) = Unwrap((await c.PatchHeader($"sub_{b}", Patch("X"), "k16c", CancellationToken.None)).Result);
        Assert.Equal(404, sb); Assert.Equal("RECEIPT_NOT_FOUND", Err(vb).Code);
        Assert.Equal(0, FakeSferaSession.SaveCountForTests(b));
        Assert.Equal(loadsB, FakeSferaSession.LoadCountForTests(b));
        Assert.Equal("Client:test_buyer", FakeSferaSession.OriginalNumberForTests(b));
        Assert.Null(await store.TryGetAsync<ReceiptDetailDto>("k16c", CancellationToken.None));
    }

    [Fact]
    public async Task T17_Cancellation_DuringCom_Is502_NotCached()
    {
        var fake = new FakeSferaSession();
        var store = NewStore();
        var c = Controller(fake, store);
        // DOWODZI: anulowanie wołającego, gdy praca STA stoi na bramce przed Zapisz, daje 502 RECEIPT_UPDATE_UNVERIFIED (saved_probably=null) bez cache
        // i bez zapisu; po otwarciu bramki praca zapisuje, GET pokazuje nowy stan, a ponowienie tym samym kluczem = 200 no-op bez drugiego zapisu.
        // Mutant: Fake ignoruje ct (odpowiedź dopiero po bramce) -> timeout; kontroler mapuje OCE na 500 -> pada; cache przy OCE -> pada.
        long id2 = await CreatePz(fake);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _gate = OpenOnExit(gate);
        FakeSferaSession.SetReceiptFactsForTests(id2, f => { f.StaGate = gate; f.StaStarted = started; });
        using var cts = new CancellationTokenSource();
        var pending = c.PatchHeader($"sub_{id2}", Patch("X"), "k17-b", cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        cts.Cancel();
        var (s3, v3) = Unwrap((await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Result);
        Assert.Equal(502, s3); Assert.Equal("RECEIPT_UPDATE_UNVERIFIED", Err(v3).Code);
        Assert.Equal(JsonValueKind.Null, DetailsOf(v3).GetProperty("saved_probably").ValueKind);
        Assert.Null(await store.TryGetAsync<ReceiptDetailDto>("k17-b", CancellationToken.None));
        Assert.Equal(0, FakeSferaSession.SaveCountForTests(id2)); // STA jeszcze nie doszedł do Zapisz
        gate.SetResult(true);
        await FakeSferaSession.StaWorkForTests(id2)!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id2));
        Assert.Equal("X", ((ReceiptDetailDto)Unwrap((await c.Get($"sub_{id2}", CancellationToken.None)).Result).value!).OriginalNumber);
        var (s2b, _) = Unwrap((await c.PatchHeader($"sub_{id2}", Patch("X"), "k17-b", CancellationToken.None)).Result);
        Assert.Equal(200, s2b); Assert.Equal(1, FakeSferaSession.SaveCountForTests(id2));

        // ta sama sytuacja, ale Sfera cicho odmówiła -> 502, po bramce GET = stary stan
        long id3 = await CreatePz(fake);
        var gate3 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started3 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _gate3 = OpenOnExit(gate3);
        FakeSferaSession.SetReceiptFactsForTests(id3, f => { f.StaGate = gate3; f.StaStarted = started3; f.RefuseSaveSilently = true; });
        using var cts3 = new CancellationTokenSource();
        var pending3 = c.PatchHeader($"sub_{id3}", Patch("X"), "k17-c", cts3.Token);
        await started3.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cts3.Cancel();
        var (s4, _) = Unwrap((await pending3.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Result);
        Assert.Equal(502, s4);
        gate3.SetResult(true);
        // praca STA konczy sie decyzja 409 NOT_SAVED, ktorej nikt nie odbiera (wolajacy anulowany) - jak w Real tylko log z fazami
        var ex3 = await Assert.ThrowsAsync<ReceiptEditException>(() => FakeSferaSession.StaWorkForTests(id3)!.WaitAsync(T5, Ct));
        Assert.Equal(ReceiptEditError.Locked, ex3.Reason); Assert.Equal(ReceiptLockReason.NotSaved, ex3.LockReason);
        Assert.Equal(0, FakeSferaSession.SaveCountForTests(id3));
        Assert.Equal("Client:test_buyer", ((ReceiptDetailDto)Unwrap((await c.Get($"sub_{id3}", CancellationToken.None)).Result).value!).OriginalNumber);
    }

    [Fact]
    public async Task T17b_CancelWhileWaiting_PendingGetWaitsForStaWork()
    {
        // DOWODZI: GET wysłany PO anulowaniu wołającego (praca STA zaparkowana przed Zapisz) zwraca stan PO zapisie, gdy praca się skończy — nie stan
        // sprzed. NIE dowodzi, że job GET czekał na semaforze (Codex r6; wariant (b)). Mutant: GET omija kolejkę (synchroniczny, dawny Fake) → stara wartość → pada.
        var fake = new FakeSferaSession();
        var c = Controller(fake, NewStore());
        long id = await CreatePz(fake);
        var gate = Tcs(); var started = Tcs();
        using var _gate = OpenOnExit(gate);
        FakeSferaSession.SetReceiptFactsForTests(id, f => { f.StaGate = gate; f.StaStarted = started; });
        using var cts = new CancellationTokenSource();
        var pending = c.PatchHeader($"sub_{id}", Patch("X"), "k17b", cts.Token);
        await started.Task.WaitAsync(T5, Ct);
        cts.Cancel();
        Assert.Equal(502, Unwrap((await pending.WaitAsync(T5, Ct)).Result).status);
        var get = c.Get($"sub_{id}", CancellationToken.None);
        gate.SetResult(true);
        await FakeSferaSession.StaWorkForTests(id)!.WaitAsync(T5, Ct);
        Assert.Equal("X", ((ReceiptDetailDto)Unwrap((await get.WaitAsync(T5, Ct)).Result).value!).OriginalNumber);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
    }

    [Fact]
    public async Task T17c_CancelWhileWaiting_CloseFails_SessionResetStillHappens()
    {
        // DOWODZI: oderwana praca STA po anulowaniu wołającego wykonuje PEŁNY cykl (Zapisz → Zamknij → reset sesji → decyzja): niepotwierdzone Zamknij
        // resetuje sesję mimo że nikt nie czeka na wynik, a kolejne żądanie dostaje świeżą sesję (200).
        // Mutant: oderwana praca kończy się na Apply (dawny Fake) → SessionResetCount=0 → pada.
        var fake = new FakeSferaSession();
        var c = Controller(fake, NewStore());
        long id = await CreatePz(fake);
        var gate = Tcs(); var started = Tcs();
        using var _gate = OpenOnExit(gate);
        FakeSferaSession.SetReceiptFactsForTests(id, f => { f.StaGate = gate; f.StaStarted = started; f.CloseFails = true; });
        using var cts = new CancellationTokenSource();
        var pending = c.PatchHeader($"sub_{id}", Patch("X"), "k17c", cts.Token);
        await started.Task.WaitAsync(T5, Ct);
        cts.Cancel();
        Assert.Equal(502, Unwrap((await pending.WaitAsync(T5, Ct)).Result).status);
        Assert.Equal(0, FakeSferaSession.SessionResetCountForTests);
        gate.SetResult(true);
        var ex = await Assert.ThrowsAsync<ReceiptEditException>(() => FakeSferaSession.StaWorkForTests(id)!.WaitAsync(T5, Ct));
        Assert.Equal(ReceiptEditError.UpdateUnverified, ex.Reason);
        Assert.Equal(1, FakeSferaSession.SessionResetCountForTests);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
        FakeSferaSession.SetReceiptFactsForTests(id, f => { f.CloseFails = false; f.StaGate = null; });
        var (s2, v2) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("Y"), "k17c-2", CancellationToken.None)).Result);
        Assert.Equal(200, s2); Assert.Equal("Y", Assert.IsType<ReceiptDetailDto>(v2).OriginalNumber);
    }

    [Fact]
    public async Task T17d_CancelWhileWaiting_ZapiszRefused_StateUnchanged()
    {
        // DOWODZI: zapis na STA następuje dopiero PO bramce i podlega odmowie Sfery — anulowanie wołającego przed Zapisz, potem Zapisz rzuca bez commitu
        // (Commits=false) → dokument bez zmian, SaveCount=0, praca kończy się decyzją UpdateFailed (nie „zapisane przed odmową”).
        // Mutant: oderwana praca robi Apply przed obsługą ZapiszThrows (dawny Fake) → SaveCount=1, wartość "X" → pada.
        var fake = new FakeSferaSession();
        var c = Controller(fake, NewStore());
        long id = await CreatePz(fake);
        var gate = Tcs(); var started = Tcs();
        using var _gate = OpenOnExit(gate);
        FakeSferaSession.SetReceiptFactsForTests(id, f => { f.StaGate = gate; f.StaStarted = started; f.ZapiszThrows = (EFAIL, "Nieznany blad", false); });
        using var cts = new CancellationTokenSource();
        var pending = c.PatchHeader($"sub_{id}", Patch("X"), "k17d", cts.Token);
        await started.Task.WaitAsync(T5, Ct);
        cts.Cancel();
        var (s, v) = Unwrap((await pending.WaitAsync(T5, Ct)).Result);
        Assert.Equal(502, s); Assert.Equal(JsonValueKind.Null, DetailsOf(v).GetProperty("saved_probably").ValueKind);
        gate.SetResult(true);
        var ex = await Assert.ThrowsAsync<ReceiptEditException>(() => FakeSferaSession.StaWorkForTests(id)!.WaitAsync(T5, Ct));
        Assert.Equal(ReceiptEditError.UpdateFailed, ex.Reason);
        Assert.Equal(0, FakeSferaSession.SaveCountForTests(id));
        Assert.Equal("Client:test_buyer", ((ReceiptDetailDto)Unwrap((await c.Get($"sub_{id}", CancellationToken.None)).Result).value!).OriginalNumber);
    }

    [Fact]
    public async Task T18_CacheSaveFails_500_StateSaved_RetryNoOp_UnlessOperatorChanged()
    {
        var fake = new FakeSferaSession();
        var store = NewStore();
        var c = Controller(fake, store);
        long id = await CreatePz(fake);
        store.FailSaveForTests = new InvalidOperationException("disk full");
        var (s, v) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k18", CancellationToken.None)).Result);
        Assert.Equal(500, s); Assert.Equal("INTERNAL_ERROR", Err(v).Code);
        Assert.Equal("X", FakeSferaSession.OriginalNumberForTests(id));
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
        store.FailSaveForTests = null;
        var (s2, _) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k18", CancellationToken.None)).Result);
        Assert.Equal(200, s2); Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
        // operator zmienił wartość przed ponowieniem bez expected_revision -> PATCH nadpisuje (kontrakt §4.4 przypadek 3)
        FakeSferaSession.SetOriginalNumberForTests(id, "OPERATOR");
        var (s3, _) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k18-c", CancellationToken.None)).Result);
        Assert.Equal(200, s3); Assert.Equal(2, FakeSferaSession.SaveCountForTests(id));
        Assert.Equal("X", FakeSferaSession.OriginalNumberForTests(id));
    }

    [Fact]
    public async Task T19_ExpectedRevision_ThreeRetryCases()
    {
        var fake = new FakeSferaSession();
        var store = NewStore();
        var c = Controller(fake, store);
        long id = await CreatePz(fake);
        var rev0 = ((ReceiptDetailDto)Unwrap((await c.Get($"sub_{id}", CancellationToken.None)).Result).value!).Revision;
        var (s1, _) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X", rev0), "k19", CancellationToken.None)).Result);
        Assert.Equal(200, s1);
        // (1) cache hit + stare expected_revision -> cached 200
        var (s2, v2) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X", rev0), "k19", CancellationToken.None)).Result);
        Assert.Equal(200, s2); Assert.Equal("X", Assert.IsType<ReceiptDetailDto>(v2).OriginalNumber);
        Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
        // (2) cache miss + stare -> 409 celowo
        var (s3, v3) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X", rev0), "k19-miss", CancellationToken.None)).Result);
        Assert.Equal(409, s3); Assert.Equal("RECEIPT_REVISION_MISMATCH", Err(v3).Code);
        // (3) cache miss bez pola -> no-op gdy wartość równa; zapis gdy inna
        var (s4, _) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k19-miss2", CancellationToken.None)).Result);
        Assert.Equal(200, s4); Assert.Equal(1, FakeSferaSession.SaveCountForTests(id));
        var (s5, _) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("Y"), "k19-miss3", CancellationToken.None)).Result);
        Assert.Equal(200, s5); Assert.Equal(2, FakeSferaSession.SaveCountForTests(id));
    }

    [Fact]
    public async Task T20_Get_Fields_Facts_Warnings()
    {
        var fake = new FakeSferaSession();
        var c = Controller(fake);
        long id = await CreatePz(fake, lines: 2);
        var dto = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{id}", CancellationToken.None)).Result).value!;
        Assert.Equal("PZ", dto.Type);
        Assert.Equal(id, dto.SubiektId);
        Assert.Equal("Client:test_buyer", dto.OriginalNumber);
        Assert.StartsWith("test:pz:", dto.ExternalReference);
        Assert.Equal(1, dto.Status);
        Assert.Equal(0, dto.BookingStatus);
        Assert.StartsWith("r1:", dto.Revision);
        Assert.Empty(dto.LinkedDocuments);
        Assert.Empty(dto.Editability.Reasons);
        Assert.True(dto.Editability.SferaCanEdit);
        Assert.Empty(dto.Warnings);
        Assert.Equal(2, dto.Items.Count);
        var it = dto.Items[0];
        Assert.Equal(1, it.Lp); Assert.Equal("product", it.Kind); Assert.Equal(2m, it.Quantity); Assert.Equal(100m, it.UnitPriceNet);
        Assert.Equal(23m, it.VatRate); Assert.Equal("5901234123450", it.Ean); Assert.Equal(0m, it.IssuedStockUnits); Assert.Equal(0m, it.IssuedQuantity);
        Assert.NotEqual(dto.Items[0].PositionId, dto.Items[1].PositionId);
        Assert.Equal(dto.Items.Sum(i => i.Quantity * i.UnitPriceNet), dto.NetAmount);
        Assert.Equal(ReceiptRevision.Compute(new ReceiptStateSnapshot(dto.OriginalNumber, dto.Status, dto.BookingStatus,
            dto.Items.Select(i => new ReceiptItemSnapshot(i.PositionId, i.Lp, i.ProductSubiektId, ReceiptItemKind.Product, i.Quantity,
                i.QuantityInStockUnits, i.Unit, i.UnitPriceNet, i.DiscountPercent, i.VatRate)).ToList())), dto.Revision);

        // fakty rozchodu z surowych wierszy: 12 z 20 wydane przy IloscJm=2, Ilosc=20 -> 1.2 (liczy FromRows, nie flaga)
        long pid = dto.Items[0].PositionId;
        long pid2 = dto.Items[1].PositionId; // druga pozycja (qty 3) musi miec swoj wiersz - hak StockRows zastepuje WSZYSTKIE wiersze
        FakeSferaSession.SetReceiptFactsForTests(id, f => f.StockRows = new List<StockRow> { new(pid, 20m, 8m, 1), new(pid2, 3m, 3m, 1) });
        // pozycja ma Quantity=2 i Ilosc=2 w Fake -> Σilosc(20) != Ilosc(2) = niespójność -> null + warning
        var inc = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{id}", CancellationToken.None)).Result).value!;
        Assert.Null(inc.Items[0].IssuedQuantity);
        Assert.Contains("STOCK_FACTS_INCONSISTENT", inc.Warnings);
        FakeSferaSession.SetReceiptFactsForTests(id, f => f.StockRows = new List<StockRow> { new(pid, 2m, 1m, 1), new(pid2, 3m, 3m, 1) });
        var ok = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{id}", CancellationToken.None)).Result).value!;
        Assert.Equal(1m, ok.Items[0].IssuedStockUnits); Assert.Equal(1m, ok.Items[0].IssuedQuantity);
        Assert.Empty(ok.Warnings);

        // SQL niedostępny -> null + SQL_FACTS_UNAVAILABLE (fail-soft w GET)
        FakeSferaSession.SetReceiptFactsForTests(id, f => f.FailSql = true);
        var sql = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{id}", CancellationToken.None)).Result).value!;
        Assert.Contains("SQL_FACTS_UNAVAILABLE", sql.Warnings);
        Assert.Null(sql.Items[0].IssuedQuantity); Assert.Null(sql.WarehouseId); Assert.Null(sql.PeriodLocked); Assert.Null(sql.Items[0].Ean);
        FakeSferaSession.SetReceiptFactsForTests(id, f => f.FailSql = false);

        // każdy fakt -> reasons; link bez numeru -> wpis z number null
        FakeSferaSession.SetReceiptFactsForTests(id, f => { f.PeriodLocked = true; f.BookingStatus = 3; f.LinkedDocument = (60001, null, "FZ"); f.SferaCanEdit = false; });
        var facts = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{id}", CancellationToken.None)).Result).value!;
        Assert.Equal(new[] { "PERIOD_LOCKED", "BOOKED", "LINKED_DOCUMENT", "SFERA_NOT_EDITABLE" }, facts.Editability.Reasons);
        Assert.Single(facts.LinkedDocuments); Assert.Null(facts.LinkedDocuments[0].Number); Assert.Equal(60001, facts.LinkedDocuments[0].SubiektId);
        Assert.True(facts.PeriodLocked); Assert.Equal(3, facts.BookingStatus); Assert.False(facts.Editability.SferaCanEdit);

        // nie-PZ -> 200 + NOT_A_RECEIPT; unieważniony -> 200 + CANCELLED, issued_* null bez ostrzeżenia
        long fs = FakeSferaSession.SeedDocumentForTests(DokTyp.FS);
        var fsDto = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{fs}", CancellationToken.None)).Result).value!;
        Assert.Equal("FS", fsDto.Type); Assert.Equal(new[] { "NOT_A_RECEIPT" }, fsDto.Editability.Reasons);
        long canc = await CreatePz(fake);
        FakeSferaSession.SetReceiptFactsForTests(canc, f => f.Cancelled = true);
        var cDto = (ReceiptDetailDto)Unwrap((await c.Get($"sub_{canc}", CancellationToken.None)).Result).value!;
        Assert.Equal(2, cDto.Status); Assert.Equal(new[] { "CANCELLED" }, cDto.Editability.Reasons);
        Assert.Null(cDto.Items[0].IssuedQuantity); Assert.Null(cDto.Items[0].IssuedStockUnits); Assert.Empty(cDto.Warnings);
    }

    [Fact]
    public async Task T21_SubiektDown_503_NothingCached_ThenRetryWorks()
    {
        var fake = new FakeSferaSession();
        var store = NewStore();
        var c = Controller(fake, store);
        long id = await CreatePz(fake);
        fake.SferaUnavailableForTests = true;
        var (s, v) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k21", CancellationToken.None)).Result);
        Assert.Equal(503, s); Assert.Equal("SUBIEKT_UNAVAILABLE", Err(v).Code);
        Assert.Null(await store.TryGetAsync<ReceiptDetailDto>("k21", CancellationToken.None));
        Assert.Equal(0, FakeSferaSession.SaveCountForTests(id));
        fake.SferaUnavailableForTests = false;
        var (s2, _) = Unwrap((await c.PatchHeader($"sub_{id}", Patch("X"), "k21", CancellationToken.None)).Result);
        Assert.Equal(200, s2);
        Assert.NotNull(await store.TryGetAsync<ReceiptDetailDto>("k21", CancellationToken.None));
        var (sg, vg) = Unwrap((await Controller(new FakeSferaSession { SferaUnavailableForTests = true }).Get($"sub_{id}", CancellationToken.None)).Result);
        Assert.Equal(503, sg); Assert.Equal("SUBIEKT_UNAVAILABLE", Err(vg).Code);
    }

    // ----------------------------- T22: mapper + DokTyp -----------------------------

    [Theory]
    [InlineData(ReceiptEditError.NotFound, 404, "RECEIPT_NOT_FOUND")]
    [InlineData(ReceiptEditError.NotAReceipt, 422, "UNSUPPORTED_DOCUMENT_TYPE")]
    [InlineData(ReceiptEditError.Cancelled, 409, "RECEIPT_CANCELLED")]
    [InlineData(ReceiptEditError.RevisionMismatch, 409, "RECEIPT_REVISION_MISMATCH")]
    [InlineData(ReceiptEditError.Locked, 409, "RECEIPT_LOCKED")]
    [InlineData(ReceiptEditError.Validation, 422, "ORIGINAL_NUMBER_TOO_LONG")]
    [InlineData(ReceiptEditError.UpdateFailed, 500, "RECEIPT_UPDATE_FAILED")]
    [InlineData(ReceiptEditError.UpdateUnverified, 502, "RECEIPT_UPDATE_UNVERIFIED")]
    [InlineData(ReceiptEditError.ReadFailed, 502, "SUBIEKT_QUERY_FAILED")]
    public void T22_Mapper(ReceiptEditError reason, int status, string code)
    {
        var ex = new ReceiptEditException(reason, "msg", code: reason == ReceiptEditError.Validation ? "ORIGINAL_NUMBER_TOO_LONG" : null,
            lockReason: reason == ReceiptEditError.Locked ? ReceiptLockReason.NotEditable : null);
        var (s, err) = ReceiptEditErrorMapper.ToResult(ex);
        Assert.Equal(status, s);
        Assert.Equal(code, err.Code);
        Assert.Null(err.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(ReceiptLockReason.LockedBySession, 30)]
    [InlineData(ReceiptLockReason.ConcurrentModification, 30)]
    [InlineData(ReceiptLockReason.PermissionDenied, null)]
    [InlineData(ReceiptLockReason.PeriodLocked, null)]
    [InlineData(ReceiptLockReason.Booked, null)]
    [InlineData(ReceiptLockReason.NotEditable, null)]
    [InlineData(ReceiptLockReason.NotSaved, null)]
    public void T22_Mapper_RetryAfterOnlyForTransientLocks(ReceiptLockReason reason, int? retry)
    {
        var (s, err) = ReceiptEditErrorMapper.ToResult(new ReceiptEditException(ReceiptEditError.Locked, "m", lockReason: reason));
        Assert.Equal(409, s);
        Assert.Equal(retry, err.RetryAfterSeconds);
        Assert.Equal(reason.ToString().Replace("LockedBySession", "LOCKED_BY_SESSION").Replace("ConcurrentModification", "CONCURRENT_MODIFICATION")
            .Replace("PermissionDenied", "PERMISSION_DENIED").Replace("PeriodLocked", "PERIOD_LOCKED").Replace("Booked", "BOOKED")
            .Replace("NotEditable", "NOT_EDITABLE").Replace("NotSaved", "NOT_SAVED"), JsonSerializer.SerializeToElement(err.Details).GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(1, "FZ")]
    [InlineData(2, "FS")]
    [InlineData(6, "KFS")]
    [InlineData(9, "MM")]
    [InlineData(10, "PZ")]
    [InlineData(99, "TYP_99")]
    public void T22_DokTyp_NameOrUnknown(int typ, string expected) => Assert.Equal(expected, DokTyp.NameOrUnknown(typ));
}
