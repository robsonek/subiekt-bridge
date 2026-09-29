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
/// Poprawki z przeglądu RealSferaSession (2026-09-29), obszar rozliczeń: duplikat ma pierwszeństwo nad
/// ALREADY_SETTLED (retry pełnego rozliczenia = 409, nie 422), guard typu operacji bankowej.
/// Real (COM) jest windows-only - testujemy parytet w Fake + mapowanie HTTP. Unikalne documentSubiektId
/// (stan Fake jest statyczny).
/// </summary>
// Statyczny stan FakeSferaSession (rozliczenia/ksiegowanie) jest resetowany w konstruktorach - klasy
// dzielace go musza biec sekwencyjnie (xunit rownolegli KLASY; bez kolekcji reset z innej klasy trafial
// miedzy dwa wywolania testu - flaky na CI).
[Collection("FakeSferaSharedState")]
public class ReviewFixesSettlementsTests
{
    private static SettlementCreateRequestDto Req(long bankOp, decimal amount) => new(bankOp, amount, null);

    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_rsettl_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static SettlementsController NewController(FakeSferaSession fake, IdempotencyStore? store = null)
        => new(fake, store ?? NewStore(), NullLogger<SettlementsController>.Instance);

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

    [Fact]
    public async Task FullSettlement_RetrySameBankOp_IsDuplicate_NotAlreadySettled()
    {
        var fake = new FakeSferaSession();
        var first = await fake.CreateSettlementAsync(1_700_001, Req(7101, 100m), CancellationToken.None);
        Assert.True(first.IsFullySettled);

        // Pozostało = 0, ale ten sam przelew już rozliczony -> 409 (auto-recovery), nie 422 (koniec dla klienta).
        var ex = await Assert.ThrowsAsync<DuplicateSettlementException>(
            () => fake.CreateSettlementAsync(1_700_001, Req(7101, 100m), CancellationToken.None));
        Assert.Equal(first.RozliczenieId, ex.ExistingRozliczenieId);
    }

    [Fact]
    public async Task FullSettlement_RetryOtherBankOp_StillAlreadySettled()
    {
        var fake = new FakeSferaSession();
        await fake.CreateSettlementAsync(1_700_002, Req(7102, 100m), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<SettlementException>(
            () => fake.CreateSettlementAsync(1_700_002, Req(7103, 10m), CancellationToken.None));
        Assert.Equal(SettlementError.AlreadySettled, ex.Reason);
    }

    [Fact]
    public async Task Controller_FullSettlement_RetryWithNewKey_Returns409WithExistingId()
    {
        var fake = new FakeSferaSession();
        var controller = NewController(fake);
        var created = Unwrap((await controller.Create("sub_1700003", Req(7104, 100m), "k-1", CancellationToken.None)).Result);
        Assert.Equal(201, created.status);
        long rozliczenieId = Assert.IsType<SettlementResponseDto>(created.value).RozliczenieId;

        // Zgubione 201 / inny klucz -> retry. Wcześniej: 422 ALREADY_SETTLED (Laravel: płatność błędna).
        var (status, value) = Unwrap((await controller.Create("sub_1700003", Req(7104, 100m), "k-2", CancellationToken.None)).Result);

        Assert.Equal(409, status);
        var err = Assert.IsType<ErrorResponseDto>(value);
        Assert.Equal("DUPLICATE_SETTLEMENT", err.Code);
        Assert.Contains(rozliczenieId.ToString(), System.Text.Json.JsonSerializer.Serialize(err.Details));
    }

    [Fact]
    public async Task Controller_FullSettlement_ReplaySameKey_ReturnsCached()
    {
        var fake = new FakeSferaSession();
        var store = NewStore();
        var controller = NewController(fake, store);
        var created = Unwrap((await controller.Create("sub_1700004", Req(7105, 100m), "k-same", CancellationToken.None)).Result);
        long rozliczenieId = Assert.IsType<SettlementResponseDto>(created.value).RozliczenieId;

        // Replay-with-verify szuka RozliczenieId w GET settlements - po pełnym rozliczeniu musi go znaleźć.
        var (status, value) = Unwrap((await NewController(fake, store).Create("sub_1700004", Req(7105, 100m), "k-same", CancellationToken.None)).Result);

        Assert.True(status is 200 or 201, $"status={status}");
        Assert.Equal(rozliczenieId, Assert.IsType<SettlementResponseDto>(value).RozliczenieId);
    }

    [Fact]
    public async Task Controller_BankOperationNotBpBw_Returns422()
    {
        var controller = NewController(new FakeSferaSession());

        var (status, value) = Unwrap((await controller.Create("sub_1700005", Req(9_000_001, 10m), "k-type", CancellationToken.None)).Result);

        Assert.Equal(422, status);
        Assert.Equal("UNSUPPORTED_BANK_OPERATION_TYPE", Assert.IsType<ErrorResponseDto>(value).Code);
    }
}
