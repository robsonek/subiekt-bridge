using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Idempotency;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Retencja cache idempotency: wpisy starsze niz TTL sa kasowane (wczesniej TTL dzialal tylko przy odczycie,
/// a kazda FS/KFS z pdf_base64 zostawala w pliku na zawsze - ~400 MB po 5 miesiacach), VACUUM oddaje miejsce
/// na dysku tylko przy duzej fragmentacji, journal /book (pending_bookings) jest nietykalny.
/// </summary>
public class IdempotencyCleanupTests
{
    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"idem_cleanup_{Guid.NewGuid():N}.db");

    private static IdempotencyStore NewStore(string path, int ttlDays = 30) => new(
        new BridgeOptions { IdempotencyStorePath = path, IdempotencyTtlDays = ttlDays },
        NullLogger<IdempotencyStore>.Instance);

    // Wpis z jawnym created_at (SaveAsync stempluje UtcNow) - ten sam format "O" co w produkcji.
    private static void InsertRow(string path, string key, DateTimeOffset createdAt, int payloadBytes = 16)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO idempotency (key, response_json, created_at) VALUES ($k, $j, $c)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$j", $"{{\"pdf_base64\":\"{new string('A', payloadBytes)}\"}}");
        cmd.Parameters.AddWithValue("$c", createdAt.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static long Count(string path, string table)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    private static bool KeyExists(string path, string key)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM idempotency WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    [Fact]
    public async Task PurgeExpired_DeletesOnlyRowsOlderThanTtl()
    {
        var path = NewDbPath();
        var store = NewStore(path, ttlDays: 30);
        var now = DateTimeOffset.UtcNow;

        InsertRow(path, "old", now.AddDays(-31));
        InsertRow(path, "just-expired", now.AddDays(-30).AddSeconds(-1));
        InsertRow(path, "fresh", now.AddDays(-29));
        InsertRow(path, "new", now);

        var deleted = await store.PurgeExpiredAsync(now, CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.False(KeyExists(path, "old"));
        Assert.False(KeyExists(path, "just-expired"));
        Assert.True(KeyExists(path, "fresh"));
        Assert.True(KeyExists(path, "new"));
    }

    [Fact]
    public async Task PurgeExpired_RowSavedBySaveAsync_SurvivesUntilTtl()
    {
        var path = NewDbPath();
        var store = NewStore(path, ttlDays: 30);
        await store.SaveAsync("k1", new { subiekt_id = 1 }, CancellationToken.None);

        Assert.Equal(0, await store.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(29), CancellationToken.None));
        Assert.True(KeyExists(path, "k1"));

        Assert.Equal(1, await store.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(31), CancellationToken.None));
        Assert.False(KeyExists(path, "k1"));
    }

    [Fact]
    public async Task PurgeExpired_MoreRowsThanOneBatch_DeletesAll()
    {
        var path = NewDbPath();
        var store = NewStore(path);
        var old = DateTimeOffset.UtcNow.AddDays(-60);
        var total = IdempotencyStore.PurgeBatchSize * 2 + 7;
        for (var i = 0; i < total; i++)
        {
            InsertRow(path, $"k{i}", old);
        }

        var deleted = await store.PurgeExpiredAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(total, deleted);
        Assert.Equal(0, Count(path, "idempotency"));
    }

    [Fact]
    public async Task PurgeExpired_NeverTouchesPendingBookingsJournal()
    {
        var path = NewDbPath();
        var store = NewStore(path);
        await store.SavePendingBookingAsync(hbId: 501, nzfId: 9001, CancellationToken.None);
        InsertRow(path, "old", DateTimeOffset.UtcNow.AddDays(-90));

        await store.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddYears(1), CancellationToken.None);

        Assert.Equal(9001, await store.TryGetPendingBookingAsync(501, CancellationToken.None));
    }

    [Fact]
    public async Task PurgeExpired_NonPositiveTtl_DeletesNothing()
    {
        var path = NewDbPath();
        var store = NewStore(path, ttlDays: 0);
        InsertRow(path, "old", DateTimeOffset.UtcNow.AddDays(-90));

        Assert.Equal(0, await store.PurgeExpiredAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.True(KeyExists(path, "old"));
    }

    [Fact]
    public async Task VacuumIfFragmented_AfterLargePurge_ShrinksFile()
    {
        var path = NewDbPath();
        var store = NewStore(path);
        var old = DateTimeOffset.UtcNow.AddDays(-60);
        for (var i = 0; i < 100; i++)
        {
            InsertRow(path, $"old{i}", old, payloadBytes: 50_000);
        }
        InsertRow(path, "fresh", DateTimeOffset.UtcNow, payloadBytes: 50_000);

        await store.PurgeExpiredAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        var sizeBefore = new FileInfo(path).Length;

        Assert.True(await store.VacuumIfFragmentedAsync(CancellationToken.None));

        var sizeAfter = new FileInfo(path).Length;
        Assert.True(sizeAfter < sizeBefore / 10, $"plik {sizeBefore} B -> {sizeAfter} B");
        Assert.True(KeyExists(path, "fresh"));
        // Po VACUUM freelist jest pusty - drugi przebieg nie przepisuje pliku.
        Assert.False(await store.VacuumIfFragmentedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VacuumIfFragmented_SmallFreelist_SkipsVacuum()
    {
        var path = NewDbPath();
        var store = NewStore(path);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            // 2 z 20 wpisow przeterminowane = ~10% stron wolnych, ponizej progu.
            InsertRow(path, $"k{i}", i < 2 ? now.AddDays(-60) : now, payloadBytes: 50_000);
        }

        await store.PurgeExpiredAsync(now, CancellationToken.None);

        Assert.False(await store.VacuumIfFragmentedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CleanupService_RunOnce_PurgesAndVacuums()
    {
        var path = NewDbPath();
        var store = NewStore(path);
        for (var i = 0; i < 50; i++)
        {
            InsertRow(path, $"old{i}", DateTimeOffset.UtcNow.AddDays(-60), payloadBytes: 50_000);
        }
        var sizeBefore = new FileInfo(path).Length;
        var service = new IdempotencyCleanupService(store, NullLogger<IdempotencyCleanupService>.Instance);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, Count(path, "idempotency"));
        Assert.True(new FileInfo(path).Length < sizeBefore / 10);
    }

    [Fact]
    public async Task CleanupService_RunOnce_StoreFailure_IsLoggedNotThrown()
    {
        // Wyjatek w BackgroundService zatrzymalby caly host (domyslne BackgroundServiceExceptionBehavior.StopHost)
        // - sprzatanie cache nie moze polozyc wystawiania faktur.
        var path = NewDbPath();
        var store = NewStore(path);
        SqliteConnection.ClearAllPools();
        File.Delete(path); // SQLite odtworzy pusty plik bez tabeli -> "no such table: idempotency"
        var service = new IdempotencyCleanupService(store, NullLogger<IdempotencyCleanupService>.Instance);

        var ex = await Record.ExceptionAsync(() => service.RunOnceAsync(CancellationToken.None));

        Assert.Null(ex);
    }
}
