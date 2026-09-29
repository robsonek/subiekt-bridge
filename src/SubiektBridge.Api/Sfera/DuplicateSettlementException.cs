namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Rzucane gdy ta operacja bankowa jest już rozliczona z tym rozrachunkiem (wykryte przez
/// odczyt stanu FinDokument.Rozliczenia po SplataId - FAIL-CLOSED, w odróżnieniu od fail-open
/// anti-duplicate dla FS: podwójne rozliczenie tej samej kwoty to realny błąd księgowy).
///
/// Controller mapuje na 409 DUPLICATE_SETTLEMENT z existing_rozliczenie_id do auto-recovery
/// po stronie klienta (Laravel oznacza płatność jako rozliczoną zamiast retry'ować).
/// </summary>
public sealed class DuplicateSettlementException : Exception
{
    /// <summary>
    /// RozliczenieId istniejącej linii; null = istnieje, ale numeru nie odczytano (kontrakt: `int | null`,
    /// do v0.18.0 sentinel -1 - klient i tak przyjmował tylko wartości > 0).
    /// </summary>
    public long? ExistingRozliczenieId { get; }
    public long RozrachunekSubiektId { get; }
    public long BankOperationSubiektId { get; }

    public DuplicateSettlementException(long? existingRozliczenieId, long rozrachunekSubiektId, long bankOperationSubiektId)
        : base($"Operacja bankowa {bankOperationSubiektId} jest juz rozliczona z rozrachunkiem {rozrachunekSubiektId} (rozliczenie_id={(existingRozliczenieId?.ToString() ?? "nieznane")}).")
    {
        ExistingRozliczenieId = existingRozliczenieId;
        RozrachunekSubiektId = rozrachunekSubiektId;
        BankOperationSubiektId = bankOperationSubiektId;
    }
}
