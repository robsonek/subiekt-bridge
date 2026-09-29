namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Sesja Sfery jest martwa albo nie da się jej otworzyć (operator zamknął Subiekta, RPC disconnected, pad MSSQL).
/// Rzucany w dwóch miejscach:
/// (1) po wyjątku odczytu (WczytajDokument/Istnieje), gdy SONDA SESJI (IsSessionAlive: OtworzKolekcje dok_Id=-1,
///     runda do SQL) też pada - wcześniej każdy wyjątek odczytu znaczył „dokument nie istnieje": replay idempotencji
///     kasował klucz i wystawiał dokument od nowa (duplikat), a GET zwracał 404 (klient: nie retry'uj);
/// (2) z PREFLIGHTU mutacji (EnsureSessionForMutation) PRZED pierwszym zapisem - FS/KFS/PZ/MM, settlements, /book.
/// Kontroler mapuje na 503 SUBIEKT_UNAVAILABLE (do v0.18.0: SFERA_UNAVAILABLE, tylko odczyty). Gwarancja kontraktu:
/// w tym żądaniu NIC nie zapisano - klient ponawia tym samym Idempotency-Key (503 nie trafia do cache).
/// Sesja jest resetowana (kolejne wywołanie otworzy ją na nowo).
/// </summary>
public sealed class SferaUnavailableException : Exception
{
    public SferaUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
