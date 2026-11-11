namespace SubiektBridge.Api.Controllers;

/// <summary>
/// Bramka POST /admin/update: w oknie <c>window</c> od zaplanowania aktualizacji kolejne żądanie NIE uruchamia drugiego skryptu.
/// Prod 2026-11-09: dwa POST-y w odstępie 49 s (ponowienie z panelu, gdy pierwszy skrypt pobierał jeszcze paczkę) uruchomiły dwa
/// równoległe update-bridge.ps1 - oba pobierały ZIP w to samo miejsce i oba robiły stop/start usługi.
/// Proces normalnie ginie przy aktualizacji, więc bramka znika razem z nim; okno chroni tylko przed zakleszczeniem, gdyby skrypt
/// padł, a usługa działała dalej na starej wersji. Czysta logika z zegarem z zewnątrz - testowalna bez procesu.
/// </summary>
public sealed class SelfUpdateGate
{
    private readonly object _gate = new();
    private readonly TimeSpan _window;
    private DateTimeOffset? _scheduledAt;

    public SelfUpdateGate(TimeSpan window) => _window = window;

    /// <summary>
    /// true = to żądanie planuje aktualizację (<paramref name="scheduledAt"/> = now). false = aktualizacja już zaplanowana
    /// (<paramref name="scheduledAt"/> = moment pierwszego żądania).
    /// </summary>
    public bool TryAcquire(DateTimeOffset now, out DateTimeOffset scheduledAt)
    {
        lock (_gate)
        {
            if (_scheduledAt is { } since && now - since <= _window)
            {
                scheduledAt = since;
                return false;
            }
            _scheduledAt = now;
            scheduledAt = now;
            return true;
        }
    }

    /// <summary>Skrypt nie został uruchomiony (brak pliku, błąd spawnu, zły tag) - kolejna próba jest dozwolona od razu.</summary>
    public void Release()
    {
        lock (_gate) _scheduledAt = null;
    }
}
