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
        _connectionString = $"Data Source={options.IdempotencyStorePath}";
        _logger = logger;
        _ttl = TimeSpan.FromDays(options.IdempotencyTtlDays);
        EnsureSchema();
    }

    public async Task<TResponse?> TryGetAsync<TResponse>(string key, CancellationToken ct)
        where TResponse : class
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT response_json, created_at FROM idempotency WHERE key = $key LIMIT 1";
        cmd.Parameters.AddWithValue("$key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var json = reader.GetString(0);
        var createdAt = DateTimeOffset.Parse(reader.GetString(1));

        if (DateTimeOffset.UtcNow - createdAt > _ttl)
        {
            _logger.LogInformation("Idempotency key {Key} expired (age > {Ttl} days), ignoring",
                key, _ttl.TotalDays);
            return null;
        }

        return JsonSerializer.Deserialize<TResponse>(json);
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
