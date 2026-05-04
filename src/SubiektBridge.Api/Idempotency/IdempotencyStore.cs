using System.Text.Json;
using Microsoft.Data.Sqlite;
using SubiektBridge.Api.Configuration;

namespace SubiektBridge.Api.Idempotency;

/// <summary>
/// Lokalny SQLite store dla idempotency keys: klucz -> zapamiętany response (JSON).
///
/// Powtórny POST z tym samym <c>Idempotency-Key</c> dostaje to samo body co pierwszy
/// - zapobiega podwójnemu wystawieniu FV przy retry po stronie Laravela.
///
/// TTL z BridgeOptions (domyślnie 30 dni) - po przekroczeniu wpis jest ignorowany
/// (cron czyszczący dorobimy w Hosted Service później).
/// </summary>
public sealed class IdempotencyStore
{
    private readonly string _connectionString;
    private readonly ILogger<IdempotencyStore> _logger;
    private readonly TimeSpan _ttl;

    public IdempotencyStore(BridgeOptions options, ILogger<IdempotencyStore> logger)
    {
        var path = options.IdempotencyStorePath;

        // SQLite tworzy plik bazy automatycznie, ale NIE tworzy katalogu rodzica.
        // Jeśli config wskazuje 'C:\SubiektBridge\data\idempotency.db' a folder data\
        // nie istnieje - dostaniemy 'SQLite Error 14: unable to open database file'.
        // Tworzymy folder tu, żeby Bridge działał nawet bez install-windows.ps1.
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            logger.LogInformation("Created idempotency store directory: {Directory}", directory);
        }

        _connectionString = $"Data Source={path}";
        _logger = logger;
        _ttl = TimeSpan.FromDays(options.IdempotencyTtlDays);
        EnsureSchema();
    }

    public async Task<TResponse?> TryGetAsync<TResponse>(string key, CancellationToken ct)
        where TResponse : class
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Krok 1: pobierz JSON i timestamp. Zamykamy reader przed dalszymi operacjami,
        // bo SQLite nie pozwala na concurrent commands na tym samym connection.
        string? json = null;
        DateTimeOffset? createdAt = null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT response_json, created_at FROM idempotency WHERE key = $key LIMIT 1";
            cmd.Parameters.AddWithValue("$key", key);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                json = reader.GetString(0);
                createdAt = DateTimeOffset.Parse(reader.GetString(1));
            }
        }

        if (json is null || createdAt is null)
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - createdAt.Value > _ttl)
        {
            _logger.LogInformation("Idempotency key {Key} expired (age > {Ttl} days), ignoring",
                key, _ttl.TotalDays);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(json);
        }
        catch (JsonException ex)
        {
            // Korupcja JSON-u (np. po crash/disk full). Bez tego catch'a Bridge zwracałby
            // 500 → Laravel BridgeUnavailableException → retry → znowu corrupt → infinite loop.
            // Usuwamy zepsuty wpis i zwracamy null - request zostanie wykonany od nowa.
            _logger.LogError(ex,
                "Corrupt idempotency entry {Key} (JsonException: {Message}). " +
                "Usuwam wpis - kolejne wywołanie wykona pełen flow.",
                key, ex.Message);

            await using var deleteCmd = conn.CreateCommand();
            deleteCmd.CommandText = "DELETE FROM idempotency WHERE key = $key";
            deleteCmd.Parameters.AddWithValue("$key", key);
            await deleteCmd.ExecuteNonQueryAsync(ct);

            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM idempotency WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAsync<TResponse>(string key, TResponse response, CancellationToken ct)
        where TResponse : class
    {
        var json = JsonSerializer.Serialize(response);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO idempotency (key, response_json, created_at)
            VALUES ($key, $json, $created)
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS idempotency (
                key         TEXT PRIMARY KEY,
                response_json TEXT NOT NULL,
                created_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_idempotency_created ON idempotency(created_at);
            """;
        cmd.ExecuteNonQuery();
    }
}
