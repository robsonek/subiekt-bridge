using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Poprawki z przeglądu RealSferaSession (2026-09-29), obszar home banking: `hb_status` w listingu i filtr
/// `unbooked_only` (bez linii pominiętych), 422 INVALID_HB_AMOUNT, journal /book w SQLite. Ścieżka COM/raw
/// UPDATE (link-state check przed rollbackiem) jest windows-only - tu testujemy Fake, kontroler i store.
/// </summary>
// Statyczny stan FakeSferaSession (rozliczenia/ksiegowanie) jest resetowany w konstruktorach - klasy
// dzielace go musza biec sekwencyjnie (xunit rownolegli KLASY; bez kolekcji reset z innej klasy trafial
// miedzy dwa wywolania testu - flaky na CI).
[Collection("FakeSferaSharedState")]
public class ReviewFixesHomeBankingTests
{
    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_rhb_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static BankTransactionsController NewController(FakeSferaSession fake)
        => new(fake, NewStore(), Options.Create(new BridgeOptions { EnableHbBooking = true }),
               NullLogger<BankTransactionsController>.Instance);

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

    [Fact]
    public async Task BankTransactions_UnbookedOnly_ExcludesSkippedByOperator()
    {
        var fake = new FakeSferaSession();

        var all = await fake.QueryBankTransactionsAsync(new BankTransactionQueryRequestDto(null, UnbookedOnly: false), CancellationToken.None);
        var unbooked = await fake.QueryBankTransactionsAsync(new BankTransactionQueryRequestDto(null, UnbookedOnly: true), CancellationToken.None);

        var skipped = Assert.Single(all, t => t.HbId == 13200);
        Assert.Equal(3, skipped.HbStatus);
        Assert.False(skipped.Booked);
        // Bez linku, ale pominięta (hb_Status=3) - /book i tak odrzuci 422, więc nie jest kandydatem.
        Assert.DoesNotContain(unbooked, t => t.HbId == 13200);
        Assert.All(unbooked, t => Assert.True(t.HbStatus is 0 or 4));
    }

    [Fact]
    public async Task Book_NullAmount_Returns422InvalidHbAmount()
    {
        var controller = NewController(new FakeSferaSession());

        var (status, value) = Unwrap((await controller.Book(33_333, new BookRequestDto(), "k-amount", CancellationToken.None)).Result);

        Assert.Equal(422, status);
        Assert.Equal("INVALID_HB_AMOUNT", Assert.IsType<ErrorResponseDto>(value).Code);
    }

    [Fact]
    public async Task Journal_PendingBooking_SaveGetDelete()
    {
        var store = NewStore();

        Assert.Null(await store.TryGetPendingBookingAsync(13128, CancellationToken.None));

        await store.SavePendingBookingAsync(13128, 90_001, CancellationToken.None);
        Assert.Equal(90_001, await store.TryGetPendingBookingAsync(13128, CancellationToken.None));

        // INSERT OR REPLACE - ponowna próba tej samej linii nadpisuje wpis.
        await store.SavePendingBookingAsync(13128, 90_002, CancellationToken.None);
        Assert.Equal(90_002, await store.TryGetPendingBookingAsync(13128, CancellationToken.None));

        await store.DeletePendingBookingAsync(13128, CancellationToken.None);
        Assert.Null(await store.TryGetPendingBookingAsync(13128, CancellationToken.None));
        // Delete nieistniejącego wpisu jest bezpieczny (best-effort w finally).
        await store.DeletePendingBookingAsync(13128, CancellationToken.None);
    }

    [Fact]
    public void BankTransactionDto_ExposesHbStatus_DefaultZero()
    {
        var dto = new BankTransactionDto(1, null, 1m, "in", null, null, null, null, false, null, null, null);
        Assert.Equal(0, dto.HbStatus);
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.Contains("\"hb_status\":0", json);
    }
}
