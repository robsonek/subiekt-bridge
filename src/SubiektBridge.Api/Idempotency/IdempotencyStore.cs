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
/// TTL z BridgeOptions (domyślnie 14 dni) - po przekroczeniu wpis jest ignorowany przy odczycie,
/// a <see cref="IdempotencyCleanupService"/> raz na dobę go kasuje (<see cref="PurgeExpiredAsync"/>).
/// </summary>
public sealed class IdempotencyStore
{
    /// <summary>
    /// Wierszy kasowanych w jednej instrukcji - krótkie blokady zapisu, żeby równoległy SaveAsync po wystawionej
    /// FV nie czekał na usunięcie całego zaległego ogona (pierwszy przebieg po wdrożeniu: miesiące wpisów z PDF).
    /// </summary>
    internal const int PurgeBatchSize = 200;

    private readonly string _connectionString;
    private readonly string _fullPath;
    private readonly ILogger<IdempotencyStore> _logger;
    private readonly TimeSpan _ttl;

    /// <summary>Efektywny TTL (appsettings.Production.json nadpisuje domyślny) - logowany przez cleanup.</summary>
    public TimeSpan Ttl => _ttl;

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
        _fullPath = fullPath;
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

    // ----------------------------- Retencja (IdempotencyCleanupService) -----------------------------
    //
    // Każda FS/KFS zostawia tu odpowiedź z pełnym pdf_base64 (dziesiątki KB). Bez kasowania plik rósł bez końca
    // (~400 MB po 5 miesiącach) - przy małym dysku hosta kończyło się "SQLite Error 13: database or disk is full".

    /// <summary>
    /// Kasuje wpisy starsze niż TTL - te same, które <see cref="TryGetAsync{TResponse}"/> już ignoruje, więc dla
    /// klienta nic się nie zmienia. Tylko tabela idempotency: pending_bookings to journal /book (wpis = niedomknięty
    /// BP do dokończenia przy retry), nie cache - nie ma TTL.
    /// </summary>
    /// <returns>Liczba usuniętych wpisów.</returns>
    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        // TTL <= 0 to błąd konfiguracji, nie polecenie "skasuj wszystko" - nie kasujemy nic.
        if (_ttl <= TimeSpan.Zero)
        {
            return 0;
        }

        // created_at zapisuje SaveAsync jako UtcNow.ToString("O") - stała szerokość, zawsze +00:00, więc porównanie
        // tekstowe jest chronologiczne (i idzie po idx_idempotency_created). Granica jak w TryGetAsync: wiek > TTL.
        var cutoff = (now.ToUniversalTime() - _ttl).ToString("O");

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var total = 0;
        int deleted;
        do
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM idempotency WHERE key IN (
                    SELECT key FROM idempotency WHERE created_at < $cutoff LIMIT $batch)
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$batch", PurgeBatchSize);
            deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;
        }
        while (deleted == PurgeBatchSize);

        return total;
    }

    /// <summary>
    /// VACUUM tylko gdy co najmniej 1/4 stron pliku leży na freeliście. W stanie ustalonym dobowy purge zwalnia
    /// ~1/TTL pliku, a nowe wpisy zajmują te strony ponownie - plik przestaje rosnąć bez VACUUM. VACUUM jest
    /// potrzebny po pierwszym purge zaległego ogona (albo po dłuższej przerwie w działaniu usługi).
    /// Przepisuje całą bazę pod wyłączną blokadą; równoległe zapytania czekają (Microsoft.Data.Sqlite ponawia
    /// SQLITE_BUSY do CommandTimeout, domyślnie 30 s). Potrzebuje wolnego miejsca ~ rozmiar żywych danych -
    /// przy pełnym dysku padnie bez szkody (VACUUM jest atomowy), a strony z freelisty i tak są ponownie używane.
    /// </summary>
    /// <returns>True, gdy VACUUM został wykonany.</returns>
    public async Task<bool> VacuumIfFragmentedAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var freePages = await PragmaLongAsync(conn, "freelist_count", ct);
        var totalPages = await PragmaLongAsync(conn, "page_count", ct);
        if (freePages == 0 || freePages < totalPages / 4)
        {
            return false;
        }

        var sizeBefore = new FileInfo(_fullPath).Length;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "VACUUM";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var sizeAfter = new FileInfo(_fullPath).Length;

        _logger.LogInformation(
            "Idempotency store VACUUM: {FreePages}/{TotalPages} stron wolnych, plik {BeforeMb:F1} MB -> {AfterMb:F1} MB",
            freePages, totalPages, sizeBefore / 1048576.0, sizeAfter / 1048576.0);
        return true;
    }

    private static async Task<long> PragmaLongAsync(SqliteConnection conn, string pragma, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    // ----------------------------- Journal ksiegowania /book (write-ahead) -----------------------------
    //
    // Miedzy bp.Zapisz() (BP w Subiekcie) a raw UPDATE hb_Transakcja (link) nie ma zadnego sladu "BP nzf_id
    // nalezy do hb_id X" - BP nie ma pola na ref (Opis niedostepny dla op. bankowych, Tytulem celowo surowy).
    // Smierc procesu w tym oknie (Stop-Service przy self-update, crash) zostawiala osierocony BP, a retry
    // klienta tworzyl DRUGI. Journal: wpis (hb_id -> nzf_id) zaraz po Zapisz; usuwany po sukcesie/rollbacku;
    // na wejsciu /book pending wpis + istniejacy BP = dokonczenie linku zamiast nowego BP.

    public async Task SavePendingBookingAsync(long hbId, long nzfId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO pending_bookings (hb_id, nzf_id, created_at) VALUES ($hb, $nzf, $created)";
        cmd.Parameters.AddWithValue("$hb", hbId);
        cmd.Parameters.AddWithValue("$nzf", nzfId);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> TryGetPendingBookingAsync(long hbId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT nzf_id FROM pending_bookings WHERE hb_id = $hb LIMIT 1";
        cmd.Parameters.AddWithValue("$hb", hbId);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null || v is DBNull ? null : Convert.ToInt64(v);
    }

    public async Task DeletePendingBookingAsync(long hbId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_bookings WHERE hb_id = $hb";
        cmd.Parameters.AddWithValue("$hb", hbId);
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
            CREATE TABLE IF NOT EXISTS pending_bookings (
                hb_id       INTEGER PRIMARY KEY,
                nzf_id      INTEGER NOT NULL,
                created_at  TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
