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
/// Testy rozliczeń przeciw FakeSferaSession (logika sesji) + bezpośrednio przeciw kontrolerom
/// (mapowanie HTTP + idempotency). Bez WebApplicationFactory - controller instancjonujemy wprost.
/// RealSferaSession (COM) jest [SupportedOSPlatform(windows)] i testuje się tylko na Windowsie.
/// </summary>
public class SettlementsTests
{
    public SettlementsTests() => FakeSferaSession.ResetSettlementsForTests();

    private static SettlementCreateRequestDto Req(long bankOp, decimal amount, string? extRef = null)
        => new(bankOp, amount, extRef);

    private static IdempotencyStore NewStore()
    {
        var opts = new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_test_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        };
        return new IdempotencyStore(opts, NullLogger<IdempotencyStore>.Instance);
    }

    private static SettlementsController NewSettlementsController(FakeSferaSession fake)
        => new(fake, NewStore(), NullLogger<SettlementsController>.Instance);

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

    // ----------------------------- FakeSfera logic -----------------------------

    [Fact]
    public async Task CreateSettlement_Partial_ReturnsRemaining()
    {
        var fake = new FakeSferaSession();
        var r = await fake.CreateSettlementAsync(1_000_001, Req(7001, 30m), CancellationToken.None);

        Assert.True(r.RozliczenieId > 0);
        Assert.Equal(30m, r.Amount);
        Assert.Equal(70m, r.RemainingAfter);
        Assert.False(r.IsFullySettled);
    }

    [Fact]
    public async Task CreateSettlement_FullAmount_MarksFullySettled()
    {
        var fake = new FakeSferaSession();
        var r = await fake.CreateSettlementAsync(1_000_002, Req(7001, 100m), CancellationToken.None);

        Assert.Equal(0m, r.RemainingAfter);
        Assert.True(r.IsFullySettled);
    }

    [Fact]
    public async Task CreateSettlement_PartialThenFull_TwoLines()
    {
        var fake = new FakeSferaSession();
        await fake.CreateSettlementAsync(1_000_003, Req(7001, 60m), CancellationToken.None);
        var second = await fake.CreateSettlementAsync(1_000_003, Req(7002, 40m), CancellationToken.None);

        Assert.Equal(0m, second.RemainingAfter);
        Assert.True(second.IsFullySettled);

        var state = await fake.GetSettlementsAsync(1_000_003, CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(2, state!.Settlements.Count);
        Assert.Equal(0m, state.RemainingAmount);
    }

    [Fact]
    public async Task CreateSettlement_SameBankOpTwice_ThrowsDuplicate()
    {
        var fake = new FakeSferaSession();
        await fake.CreateSettlementAsync(1_000_004, Req(7001, 30m), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuplicateSettlementException>(
            () => fake.CreateSettlementAsync(1_000_004, Req(7001, 10m), CancellationToken.None));
        Assert.True(ex.ExistingRozliczenieId > 0);
        Assert.Equal(7001, ex.BankOperationSubiektId);
    }

    [Fact]
    public async Task CreateSettlement_AmountExceedsRemaining_Throws()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<SettlementException>(
            () => fake.CreateSettlementAsync(1_000_005, Req(7001, 150m), CancellationToken.None));
        Assert.Equal(SettlementError.AmountExceedsRemaining, ex.Reason);
    }

    [Fact]
    public async Task CreateSettlement_DocumentWithoutRozrachunek_Throws()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<SettlementException>(
            () => fake.CreateSettlementAsync(3_000_005, Req(7001, 30m), CancellationToken.None));
        Assert.Equal(SettlementError.NoRozrachunek, ex.Reason);
    }

    [Fact]
    public async Task CreateSettlement_UnsupportedDocType_ThrowsUnsupportedType()
    {
        // Typ nieobsługiwany (np. korekta KFS/KFZ). FS (Typ=2) i FZ (Typ=1) są wspierane.
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<SettlementException>(
            () => fake.CreateSettlementAsync(2_000_005, Req(7001, 30m), CancellationToken.None));
        Assert.Equal(SettlementError.UnsupportedDocumentType, ex.Reason);
    }

    [Fact]
    public async Task CreateSettlement_AlreadySettled_Throws()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<SettlementException>(
            () => fake.CreateSettlementAsync(1_900_001, Req(7001, 30m), CancellationToken.None));
        Assert.Equal(SettlementError.AlreadySettled, ex.Reason);
    }

    [Fact]
    public async Task CreateSettlement_BankOperationMissing_Throws()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<SettlementException>(
            () => fake.CreateSettlementAsync(1_000_006, Req(-1, 30m), CancellationToken.None));
        Assert.Equal(SettlementError.BankOperationNotFound, ex.Reason);
    }

    [Fact]
    public async Task CreateSettlement_BankOperationExhausted_Throws()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<SettlementException>(
            () => fake.CreateSettlementAsync(1_000_007, Req(0, 30m), CancellationToken.None));
        Assert.Equal(SettlementError.BankOperationExhausted, ex.Reason);
    }

    [Fact]
    public async Task CreateSettlement_InvalidAmount_Throws()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<SettlementException>(
            () => fake.CreateSettlementAsync(1_000_008, Req(7001, 0m), CancellationToken.None));
        Assert.Equal(SettlementError.InvalidAmount, ex.Reason);
    }

    [Fact]
    public async Task GetSettlements_ReturnsOriginalAndRemaining()
    {
        var fake = new FakeSferaSession();
        await fake.CreateSettlementAsync(1_000_009, Req(7001, 30m), CancellationToken.None);

        var state = await fake.GetSettlementsAsync(1_000_009, CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(100m, state!.OriginalAmount);
        Assert.Equal(70m, state.RemainingAmount);
        Assert.Single(state.Settlements);
        Assert.NotNull(state.LastSettlementAt);
    }

    [Fact]
    public async Task DeleteSettlement_RemovesLine_RestoresRemaining()
    {
        var fake = new FakeSferaSession();
        var created = await fake.CreateSettlementAsync(1_000_010, Req(7001, 30m), CancellationToken.None);

        await fake.DeleteSettlementAsync(1_000_010, created.RozliczenieId, CancellationToken.None);

        var state = await fake.GetSettlementsAsync(1_000_010, CancellationToken.None);
        Assert.NotNull(state);
        Assert.Empty(state!.Settlements);
        Assert.Equal(100m, state.RemainingAmount);
    }

    [Fact]
    public async Task DeleteSettlement_NotFound_Throws()
    {
        var fake = new FakeSferaSession();
        await fake.CreateSettlementAsync(1_000_011, Req(7001, 30m), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<SettlementException>(
            () => fake.DeleteSettlementAsync(1_000_011, 999_999, CancellationToken.None));
        Assert.Equal(SettlementError.SettlementNotFound, ex.Reason);
    }

    [Fact]
    public async Task QueryBankOperations_UnsettledOnly_FiltersOut()
    {
        var fake = new FakeSferaSession();
        var all = await fake.QueryBankOperationsAsync(new BankOperationQueryRequestDto(null, null, null), CancellationToken.None);
        var unsettled = await fake.QueryBankOperationsAsync(new BankOperationQueryRequestDto(null, null, null, UnsettledOnly: true), CancellationToken.None);

        Assert.Equal(2, all.Count);
        Assert.Single(unsettled);
        Assert.All(unsettled, o => Assert.True((o.Remaining ?? 0m) > 0.005m));
    }

    // ----------------------------- Controller (HTTP mapping + idempotency) -----------------------------

    [Fact]
    public async Task Controller_Post_MissingIdempotencyKey_Returns400()
    {
        var controller = NewSettlementsController(new FakeSferaSession());
        var r = await controller.Create("sub_1000020", Req(7001, 30m), idempotencyKey: null, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(400, status);
        Assert.Equal("MISSING_IDEMPOTENCY_KEY", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Post_InvalidBridgeId_Returns422()
    {
        var controller = NewSettlementsController(new FakeSferaSession());
        var r = await controller.Create("garbage", Req(7001, 30m), idempotencyKey: "k1", CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(422, status);
        Assert.Equal("INVALID_BRIDGE_ID", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Post_Success_Returns201()
    {
        var controller = NewSettlementsController(new FakeSferaSession());
        var r = await controller.Create("sub_1000021", Req(7001, 30m), idempotencyKey: "k-success", CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(201, status);
        var dto = Assert.IsType<SettlementResponseDto>(value);
        Assert.Equal(30m, dto.Amount);
        Assert.Equal(70m, dto.RemainingAfter);
    }

    [Fact]
    public async Task Controller_Post_IdempotentReplay_Returns200_NoSecondLine()
    {
        var fake = new FakeSferaSession();
        var controller = new SettlementsController(fake, NewStore_Shared(out var store), NullLogger<SettlementsController>.Instance);
        // wspolny store dla obu wywolan
        var c1 = await controller.Create("sub_1000022", Req(7001, 30m), idempotencyKey: "k-replay", CancellationToken.None);
        var c2 = await controller.Create("sub_1000022", Req(7001, 30m), idempotencyKey: "k-replay", CancellationToken.None);

        var (s1, _) = Unwrap(c1.Result);
        var (s2, _) = Unwrap(c2.Result);
        Assert.Equal(201, s1);
        Assert.Equal(200, s2); // replay

        var state = await fake.GetSettlementsAsync(1_000_022, CancellationToken.None);
        Assert.Single(state!.Settlements);
        GC.KeepAlive(store);
    }

    [Fact]
    public async Task Controller_Post_CrashRecovery_ReturnsDuplicate409()
    {
        var fake = new FakeSferaSession();
        // Symuluj rozliczenie utrwalone w Subiekcie, ale klucz idempotency NIEzapisany (crash).
        FakeSferaSession.InjectSettlementLineForTests(1_000_023, bankOpId: 7001, amount: 30m);

        var controller = NewSettlementsController(fake);
        var r = await controller.Create("sub_1000023", Req(7001, 30m), idempotencyKey: "k-fresh", CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(409, status);
        Assert.Equal("DUPLICATE_SETTLEMENT", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Post_DocumentWithoutRozrachunek_Returns422()
    {
        var controller = NewSettlementsController(new FakeSferaSession());
        var r = await controller.Create("sub_3000023", Req(7001, 30m), idempotencyKey: "k-pz", CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(422, status);
        Assert.Equal("SETTLEMENT_NOT_SUPPORTED", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Get_DocumentNotFound_Returns404()
    {
        var controller = NewSettlementsController(new FakeSferaSession());
        var r = await controller.GetState("sub_-1", CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(404, status);
        Assert.Equal("INVOICE_NOT_FOUND", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Delete_Success_Returns204()
    {
        var fake = new FakeSferaSession();
        var created = await fake.CreateSettlementAsync(1_000_024, Req(7001, 30m), CancellationToken.None);
        var controller = NewSettlementsController(fake);

        var r = await controller.Delete("sub_1000024", created.RozliczenieId, CancellationToken.None);

        var (status, _) = Unwrap(r);
        Assert.Equal(204, status);
    }

    [Fact]
    public async Task Controller_Delete_NotFound_Returns404()
    {
        var fake = new FakeSferaSession();
        await fake.CreateSettlementAsync(1_000_025, Req(7001, 30m), CancellationToken.None);
        var controller = NewSettlementsController(fake);

        var r = await controller.Delete("sub_1000025", 999_999, CancellationToken.None);

        var (status, value) = Unwrap(r);
        Assert.Equal(404, status);
        Assert.Equal("SETTLEMENT_NOT_FOUND", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_BankOperations_Returns200()
    {
        var controller = new BankOperationsController(new FakeSferaSession(), NullLogger<BankOperationsController>.Instance);
        var r = await controller.Query(from: null, to: null, direction: null, unsettledOnly: false, limit: 0, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        var list = Assert.IsAssignableFrom<IReadOnlyList<BankOperationDto>>(value);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Controller_Delete_InvalidBridgeId_Returns422()
    {
        var controller = NewSettlementsController(new FakeSferaSession());
        var r = await controller.Delete("garbage", 1, CancellationToken.None);

        var (status, value) = Unwrap(r);
        Assert.Equal(422, status);
        Assert.Equal("INVALID_BRIDGE_ID", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Delete_Twice_SecondReturns404()
    {
        var fake = new FakeSferaSession();
        var created = await fake.CreateSettlementAsync(1_000_026, Req(7001, 30m), CancellationToken.None);
        var controller = NewSettlementsController(fake);

        var first = await controller.Delete("sub_1000026", created.RozliczenieId, CancellationToken.None);
        var second = await controller.Delete("sub_1000026", created.RozliczenieId, CancellationToken.None);

        Assert.Equal(204, Unwrap(first).status);
        // DELETE idempotentny: powtorne usuniecie tego samego rozliczenia -> 404 (NIE 500).
        var (status, value) = Unwrap(second);
        Assert.Equal(404, status);
        Assert.Equal("SETTLEMENT_NOT_FOUND", ((ErrorResponseDto)value!).Code);
    }

    [Fact]
    public async Task Controller_Get_DocumentWithNoSettlements_Returns200_EmptyState()
    {
        var controller = NewSettlementsController(new FakeSferaSession());
        var r = await controller.GetState("sub_1999000", CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        var state = Assert.IsType<SettlementStateResponseDto>(value);
        Assert.Empty(state.Settlements);
        Assert.Equal(100m, state.RemainingAmount);
        Assert.Null(state.LastSettlementAt);
        Assert.False(state.IsFullySettled);
    }

    [Fact]
    public async Task Controller_BankOperations_DirectionOut_ReturnsEmpty()
    {
        var controller = new BankOperationsController(new FakeSferaSession(), NullLogger<BankOperationsController>.Instance);
        var r = await controller.Query(from: null, to: null, direction: "out", unsettledOnly: false, limit: 0, CancellationToken.None);

        var (status, value) = Unwrap(r.Result);
        Assert.Equal(200, status);
        var list = Assert.IsAssignableFrom<IReadOnlyList<BankOperationDto>>(value);
        Assert.Empty(list);
    }

    private static IdempotencyStore NewStore_Shared(out IdempotencyStore store)
    {
        store = NewStore();
        return store;
    }
}
