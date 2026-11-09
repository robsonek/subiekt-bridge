using SubiektBridge.Api.Models;

namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Rdzeń pętli listingu GET /invoices - bez COM (delegaty), testowalny cross-platform. Jedno wywołanie = jeden job na wątku STA,
/// a pojedynczy job blokuje WSZYSTKIE pozostałe wywołania Sfery (także sondę /health), więc pętla:
/// - respektuje anulowanie wołającego na granicy dokumentu (incydent 2026-11-09: klient rozłączył się po 30 s, a job mielił dalej
///   do 1000 dokumentów - RunOnStaAsync sprawdza token tylko PRZED startem); sprawdzenie PRZED mapowaniem (dokument już
///   załadowany przez MoveNext - zamykamy go i wychodzimy, bez Kontrahenci.Wczytaj) i PO zamknięciu (nie ładujemy następnego);
/// - przerywa po osiągnięciu <paramref name="limit"/> wyników albo <paramref name="fetchCap"/> przejrzanych dokumentów BEZ pobierania
///   kolejnego (każdy MoveNext/Element(i) to pełne wczytanie dokumentu przez Sferę);
/// - błąd mapowania pojedynczego dokumentu pomija ten dokument (<paramref name="onMapError"/>), anulowanie nigdy nie jest „błędem mapowania";
/// - każdy pobrany dokument jest zamykany (<paramref name="close"/>) niezależnie od wyniku - bez wycieku RCW na STA.
/// Akumulator <paramref name="items"/> należy do wołającego: Real po InvalidCastException przy pustym wyniku ponawia pętlę
/// na Element(i) (brak IEnumVARIANT), dokładając do tej samej listy.
/// </summary>
public static class InvoiceListingScan
{
    public static void Collect<TDoc>(
        IEnumerable<TDoc> documents,
        List<InvoiceQueryItemDto> items,
        Func<TDoc, InvoiceQueryItemDto> map,
        string? clientTypePrefix,
        int fetchCap,
        int limit,
        CancellationToken ct,
        Action<Exception> onMapError,
        Action<TDoc> close)
    {
        ct.ThrowIfCancellationRequested();
        if (items.Count >= limit || fetchCap <= 0) return;

        int seen = 0;
        foreach (var dok in documents)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                seen++;
                InvoiceQueryItemDto? mapped = null;
                try
                {
                    mapped = map(dok);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Bez `continue`: pominięty dokument też liczy się do fetchCap i nie omija sprawdzenia anulowania niżej.
                    onMapError(ex);
                }
                if (mapped is not null && (clientTypePrefix == null || mapped.Type == clientTypePrefix))
                {
                    items.Add(mapped);
                }
            }
            finally
            {
                close(dok);
            }

            if (items.Count >= limit || seen >= fetchCap) break;
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Leniwa enumeracja Element(1..take) dla fallbacku bez IEnumVARIANT (kolekcje Sfery są 1-indeksowane). Token sprawdzany
    /// PRZED każdym odczytem, poza catch: nieudany Element(i) to też wywołanie COM, a seria błędów/nulli nie dochodzi do pętli
    /// <see cref="Collect{TDoc}"/> i bez tego sprawdzenia po rozłączeniu klienta job STA odpytywałby kolekcję do końca
    /// (Greptile PR #26). Błąd odczytu pomija element (<paramref name="onReadError"/>), null jest pomijany bez raportu.
    /// </summary>
    public static IEnumerable<object> ElementsByIndex(int take, Func<int, object?> element, CancellationToken ct, Action<int, Exception> onReadError)
    {
        for (int i = 1; i <= take; i++)
        {
            ct.ThrowIfCancellationRequested();
            object? dok;
            try { dok = element(i); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                onReadError(i, ex);
                continue;
            }
            if (dok is null) continue;
            yield return dok;
        }
    }
}
