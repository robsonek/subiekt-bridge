namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Rzucany, gdy odczyt dokumentu padł i SONDA SESJI (Session.Aplikacja.Wersja) też pada - sesja Sfery jest
/// martwa (operator zamknął Subiekta, RPC disconnected). Wcześniej każdy wyjątek WczytajDokument znaczył
/// "dokument nie istnieje": replay idempotencji kasował klucz i wystawiał dokument od nowa (duplikat), a GET
/// zwracał 404 (klient: nie retry'uj). Controller mapuje na 503 SFERA_UNAVAILABLE (retry z backoff).
/// Sesja jest resetowana (kolejne wywołanie otworzy ją na nowo).
/// </summary>
public sealed class SferaUnavailableException : Exception
{
    public SferaUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
