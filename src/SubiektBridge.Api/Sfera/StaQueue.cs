using System.Collections;

namespace SubiektBridge.Api.Sfera;

/// <summary>Stan kolejki STA: ile jobów czeka lub trwa i od kiedy trwa bieżący (null = wątek wolny).</summary>
public readonly record struct StaQueueSnapshot(int Depth, TimeSpan? BusyFor);

/// <summary>
/// Licznik kolejki wątku STA Sfery (jeden wątek, jeden job naraz - AGENTS.md „Krytyczne wzorce COM"). Do v0.23.0 /health zwracał
/// `queue_depth: 0` na sztywno, a sam stał w tej kolejce: przy długim jobie (incydent 2026-11-09: listing ~20 min) wisiał do limitu
/// klienta i wyglądał jak martwy most. Monitor daje prawdziwą głębokość i czas trwania bieżącego joba bez dotykania STA.
/// Czysta logika z zegarem przekazywanym z zewnątrz - testowalna cross-platform; RealSferaSession woła ją z RunOnStaAsync/WorkerLoop.
/// </summary>
public sealed class StaQueueMonitor
{
    private readonly object _gate = new();
    private int _depth;
    private DateTimeOffset? _runningSince;

    /// <summary>Job trafia do kolejki (PRZED Add - worker może go podjąć natychmiast).</summary>
    public void Enqueued()
    {
        lock (_gate) _depth++;
    }

    /// <summary>Add do kolejki się nie powiódł (kolejka zamknięta przy Dispose) - job nigdy nie ruszy.</summary>
    public void Abandoned()
    {
        lock (_gate) _depth = Math.Max(0, _depth - 1);
    }

    /// <summary>Worker zaczyna wykonywać job.</summary>
    public void Started(DateTimeOffset now)
    {
        lock (_gate) _runningSince = now;
    }

    /// <summary>Worker skończył job (także wyjątkiem albo pominięciem po anulowaniu).</summary>
    public void Finished()
    {
        lock (_gate)
        {
            _depth = Math.Max(0, _depth - 1);
            _runningSince = null;
        }
    }

    public StaQueueSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            return new StaQueueSnapshot(_depth, _runningSince is { } since ? now - since : null);
        }
    }
}

/// <summary>
/// Sonda z budżetem czasu: czeka na <c>probe</c> najwyżej <c>budget</c>. Po budżecie zwraca (false, default) i ANULUJE token sondy -
/// job sondy stoi wtedy w kolejce STA za długim zadaniem, a RunOnStaAsync pomija job z anulowanym tokenem, więc nie dokładamy
/// pracy zajętemu wątkowi. Anulowanie wołającego (rozłączenie klienta) to nie „zajęty": przerywa oczekiwanie wyjątkiem.
/// </summary>
public static class StaProbe
{
    public static async Task<(bool Completed, T? Result)> RunAsync<T>(Func<CancellationToken, Task<T>> probe, TimeSpan budget, CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probeTask = probe(probeCts.Token);
        var budgetTask = Task.Delay(budget, ct);

        var first = await Task.WhenAny(probeTask, budgetTask);
        if (first == probeTask)
        {
            return (true, await probeTask);
        }

        await budgetTask; // rzuca OperationCanceledException, gdy to wołający anulował (a nie minął budżet)
        probeCts.Cancel();
        // Sonda zakończy się anulowaniem albo (gdy już trwała) wynikiem, którego nikt nie odbierze - obserwujemy, żeby wyjątek nie został sierotą.
        _ = probeTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return (false, default);
    }
}

/// <summary>
/// Enumeracja kolekcji COM z anulowaniem: token sprawdzany PRZED pobraniem każdego elementu (każdy MoveNext to wywołanie Sfery),
/// więc po rozłączeniu klienta job STA kończy się przy najbliższym elemencie. RunOnStaAsync sprawdza token tylko przed startem joba -
/// pętla musi go dostać sama (incydent 2026-11-09). Dla listingu faktur robi to InvoiceListingScan; ten wrapper jest dla pozostałych
/// pętli (bank-operations, open-receivables/payables), które mają własną logikę w ciele.
/// </summary>
public static class CancellableEnumeration
{
    public static IEnumerable Each(IEnumerable source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var enumerator = source.GetEnumerator();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext()) yield break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }
}
