using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Domknięcie klasy problemu z incydentu 2026-11-09 („jedno długie zadanie na STA wygląda jak martwy most"):
/// 1) /health nie czeka w kolejce STA do limitu klienta - po budżecie sondy zgłasza "busy" z prawdziwą głębokością kolejki,
/// 2) pozostałe pętle po kolekcjach COM (bank-operations, open-receivables/payables) dają się przerwać anulowaniem,
/// 3) drugi POST /admin/update w krótkim oknie nie uruchamia drugiego skryptu aktualizacji.
/// RealSferaSession jest windows-only, więc testujemy wydzielone, czyste elementy.
/// </summary>
public class StaBusyAndUpdateGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    // ----------------------------- 1a. Licznik kolejki STA -----------------------------

    [Fact]
    public void Monitor_Idle_DepthZero_NotBusy()
    {
        var snap = new StaQueueMonitor().Snapshot(T0);

        Assert.Equal(0, snap.Depth);
        Assert.Null(snap.BusyFor);
    }

    [Fact]
    public void Monitor_CountsWaitingAndRunning_AndReportsHowLongCurrentJobRuns()
    {
        var m = new StaQueueMonitor();
        m.Enqueued();
        m.Enqueued();
        m.Started(T0);

        var snap = m.Snapshot(T0.AddSeconds(7));

        Assert.Equal(2, snap.Depth); // 1 wykonywany + 1 czekający
        Assert.Equal(TimeSpan.FromSeconds(7), snap.BusyFor);
    }

    [Fact]
    public void Monitor_Finished_DropsDepth_AndClearsBusy_UntilNextJobStarts()
    {
        var m = new StaQueueMonitor();
        m.Enqueued();
        m.Enqueued();
        m.Started(T0);
        m.Finished();

        var between = m.Snapshot(T0.AddSeconds(3));
        Assert.Equal(1, between.Depth);
        Assert.Null(between.BusyFor);

        m.Started(T0.AddSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(2), m.Snapshot(T0.AddSeconds(5)).BusyFor);
    }

    [Fact]
    public void Monitor_Abandoned_UndoesEnqueue_AndDepthNeverGoesNegative()
    {
        var m = new StaQueueMonitor();
        m.Enqueued();
        m.Abandoned(); // kolejka zamknięta w trakcie Add - job nigdy nie ruszy
        m.Finished();  // nadmiarowe wywołanie nie może zejść poniżej zera

        Assert.Equal(0, m.Snapshot(T0).Depth);
    }

    // ----------------------------- 1b. Sonda z budżetem -----------------------------

    [Fact]
    public async Task Probe_CompletesWithinBudget_ReturnsResult()
    {
        var (completed, result) = await StaProbe.RunAsync(_ => Task.FromResult(42), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(completed);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Probe_ExceedsBudget_ReturnsNotCompleted_AndCancelsProbeToken()
    {
        // Sonda stoi w kolejce za długim jobem. Po budżecie wołający dostaje "nie zdążyła", a token sondy jest anulowany,
        // więc gdy job dojdzie do czoła kolejki, RunOnStaAsync go pominie (nie dokłada pracy zajętemu STA).
        CancellationToken probeToken = default;
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (completed, result) = await StaProbe.RunAsync(token => { probeToken = token; return never.Task; },
            TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(completed);
        Assert.Equal(0, result);
        Assert.True(probeToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Probe_Throws_ExceptionPropagates()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => StaProbe.RunAsync<int>(
            _ => Task.FromException<int>(new InvalidOperationException("probe")), TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Probe_CallerCancelled_Throws_NotBusy()
    {
        // Rozłączenie klienta to nie "most zajęty" - anulowanie wołającego przerywa oczekiwanie wyjątkiem.
        using var cts = new CancellationTokenSource();
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = StaProbe.RunAsync(_ => never.Task, TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // ----------------------------- 1c. Mapowanie /health -----------------------------

    [Fact]
    public void Health_StaBusy_Returns200Busy_WithQueueDepthAndBusySeconds()
    {
        var (status, body) = HealthController.Map(new SferaHealthDto("1.89 HF2", true, null, null, SqlConnectionOk: true,
            QueueDepth: 4, StaBusyForSeconds: 612.4, StaBusy: true));

        Assert.Equal(200, status);
        Assert.Equal("busy", body.Status);
        Assert.Equal("busy", body.SferaSession);
        Assert.Equal(4, body.QueueDepth);
        Assert.Equal(612.4, body.BusyForSeconds);
        Assert.Equal("ok", body.SqlConnection);
    }

    [Fact]
    public void Health_NotBusy_ReportsRealQueueDepth_AndNullBusySeconds()
    {
        var (status, body) = HealthController.Map(new SferaHealthDto("1.89 HF2", true, null, null, SqlConnectionOk: true, QueueDepth: 1));

        Assert.Equal(200, status);
        Assert.Equal("ok", body.Status);
        Assert.Equal("active", body.SferaSession);
        Assert.Equal(1, body.QueueDepth);
        Assert.Null(body.BusyForSeconds);
    }

    [Fact]
    public void Health_SessionDown_StillWins_503()
    {
        // Sonda wykonała się i orzekła "sesja martwa" - to nie jest "busy".
        var (status, body) = HealthController.Map(new SferaHealthDto("unknown", false, null, "COMException: x", SqlConnectionOk: true));

        Assert.Equal(503, status);
        Assert.Equal("down", body.SferaSession);
    }

    // ----------------------------- 2. Enumeracja z anulowaniem -----------------------------

    private sealed class Counting
    {
        public int Pulled;

        public System.Collections.IEnumerable Of(int count)
        {
            for (int i = 1; i <= count; i++)
            {
                Pulled++;
                yield return i;
            }
        }
    }

    [Fact]
    public void Cancellable_NoCancellation_YieldsEverything()
    {
        var src = new Counting();

        var items = CancellableEnumeration.Each(src.Of(5), CancellationToken.None).Cast<int>().ToList();

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, items);
    }

    [Fact]
    public void Cancellable_CancelledWhileProcessing_DoesNotPullNextElement()
    {
        // Każde pobranie elementu kolekcji Sfery to wywołanie COM - po anulowaniu nie pobieramy kolejnego.
        using var cts = new CancellationTokenSource();
        var src = new Counting();
        var seen = new List<int>();

        Assert.Throws<OperationCanceledException>(() =>
        {
            foreach (int i in CancellableEnumeration.Each(src.Of(10), cts.Token))
            {
                seen.Add(i);
                if (i == 3) cts.Cancel();
            }
        });

        Assert.Equal(new[] { 1, 2, 3 }, seen);
        Assert.Equal(3, src.Pulled);
    }

    [Fact]
    public void Cancellable_AlreadyCancelled_PullsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var src = new Counting();

        Assert.Throws<OperationCanceledException>(() => CancellableEnumeration.Each(src.Of(10), cts.Token).Cast<int>().ToList());
        Assert.Equal(0, src.Pulled);
    }

    // ----------------------------- 3. Bramka self-update -----------------------------

    [Fact]
    public void Gate_FirstRequest_Acquires()
    {
        var gate = new SelfUpdateGate(TimeSpan.FromMinutes(10));

        Assert.True(gate.TryAcquire(T0, out var since));
        Assert.Equal(T0, since);
    }

    [Fact]
    public void Gate_SecondRequestWithinWindow_IsRejected_AndReportsFirstSchedule()
    {
        // Prod 2026-11-09: dwa POST /admin/update w odstępie 49 s uruchomiły dwa równoległe update-bridge.ps1.
        var gate = new SelfUpdateGate(TimeSpan.FromMinutes(10));
        gate.TryAcquire(T0, out _);

        Assert.False(gate.TryAcquire(T0.AddSeconds(49), out var since));
        Assert.Equal(T0, since);
    }

    [Fact]
    public void Gate_AfterWindow_AcquiresAgain()
    {
        // Proces normalnie ginie przy aktualizacji (bramka znika razem z nim); okno chroni przed zakleszczeniem,
        // gdyby skrypt padł i usługa działała dalej na starej wersji.
        var gate = new SelfUpdateGate(TimeSpan.FromMinutes(10));
        gate.TryAcquire(T0, out _);

        Assert.True(gate.TryAcquire(T0.AddMinutes(10).AddSeconds(1), out var since));
        Assert.Equal(T0.AddMinutes(10).AddSeconds(1), since);
    }

    [Fact]
    public void Gate_Release_AllowsImmediateRetry()
    {
        // Nieudane uruchomienie skryptu (SPAWN_FAILED, SCRIPT_NOT_FOUND) nie może blokować kolejnej próby.
        var gate = new SelfUpdateGate(TimeSpan.FromMinutes(10));
        gate.TryAcquire(T0, out _);
        gate.Release();

        Assert.True(gate.TryAcquire(T0.AddSeconds(1), out _));
    }

    [Fact]
    public void Gate_ConcurrentRequests_ExactlyOneAcquires()
    {
        var gate = new SelfUpdateGate(TimeSpan.FromMinutes(10));
        int acquired = 0;

        Parallel.For(0, 64, _ => { if (gate.TryAcquire(T0, out DateTimeOffset _)) Interlocked.Increment(ref acquired); });

        Assert.Equal(1, acquired);
    }
}
