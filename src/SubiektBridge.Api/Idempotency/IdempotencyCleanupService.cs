namespace SubiektBridge.Api.Idempotency;

/// <summary>
/// Retencja idempotency.db: raz na dobę kasuje wpisy starsze niż TTL (<see cref="IdempotencyStore.PurgeExpiredAsync"/>)
/// i przy dużej fragmentacji oddaje miejsce na dysku (<see cref="IdempotencyStore.VacuumIfFragmentedAsync"/>).
/// </summary>
public sealed class IdempotencyCleanupService : BackgroundService
{
    // Nie od razu po starcie: self-update restartuje usługę, a klient zaraz ponawia zaległe żądania.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IdempotencyStore _store;
    private readonly ILogger<IdempotencyCleanupService> _logger;

    public IdempotencyCleanupService(IdempotencyStore store, ILogger<IdempotencyCleanupService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Efektywny TTL w logu - zdalna weryfikacja (GET /admin/logs?grep=Idempotency), czy konfiguracja
        // produkcyjna nie przypina starej wartości.
        _logger.LogInformation("Idempotency cleanup: TTL {TtlDays} dni, pierwszy przebieg za {DelayMinutes} min",
            _store.Ttl.TotalDays, StartupDelay.TotalMinutes);

        await Task.Delay(StartupDelay, stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Jeden przebieg. Nie rzuca (poza anulowaniem przy zatrzymaniu usługi): wyjątek z BackgroundService zatrzymuje
    /// cały host (domyślne BackgroundServiceExceptionBehavior.StopHost), a sprzątanie cache nie może położyć
    /// wystawiania dokumentów - błąd logujemy, kolejna próba za dobę.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var deleted = await _store.PurgeExpiredAsync(DateTimeOffset.UtcNow, ct);
            if (deleted > 0)
            {
                _logger.LogInformation("Idempotency cleanup: usunięto {Count} wpisów starszych niż TTL", deleted);
            }

            await _store.VacuumIfFragmentedAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Idempotency cleanup nie powiódł się - kolejna próba za {Hours} h", Interval.TotalHours);
        }
    }
}
