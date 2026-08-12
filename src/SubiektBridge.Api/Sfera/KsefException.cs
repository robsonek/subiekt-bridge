namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Powod niepowodzenia wysylki do KSeF - mapowany przez KsefController na (status HTTP, code).
/// Wzorzec jak <see cref="SettlementError"/>: jeden enum, latwy switch.
/// </summary>
public enum KsefError
{
    /// <summary>Dokument {id} nie istnieje (duchy z OtworzKolekcje tez) -> 404 INVOICE_NOT_FOUND.</summary>
    DocumentNotFound,
    /// <summary>Typ != FS(2)/KFS(6) -> 422 UNSUPPORTED_DOCUMENT_TYPE.</summary>
    UnsupportedDocumentType,
    /// <summary>FormaDokumentu != 1 (faktura nie-KSeF, np. konsumencka) -> 422 NOT_KSEF_INVOICE.</summary>
    NotKsefInvoice,
    /// <summary>Schema e-Faktury niespelniona (StatusKSeF=7 / wyjatek Sprawdz/Generuj) -> 422 KSEF_VALIDATION_FAILED.</summary>
    ValidationFailed,
    /// <summary>KSeF odrzucil dokument (StatusKSeF=6 po biezacej wysylce) -> 422 KSEF_REJECTED.</summary>
    Rejected,
    /// <summary>Blad komunikacji z KSeF / brak Konta InsERT (StatusKSeF=8 / wyjatek Wyslij) -> 502 KSEF_COMMUNICATION_ERROR.</summary>
    CommunicationError,
    /// <summary>Nieoczekiwany stan/blad -> 500 SFERA_ERROR.</summary>
    Internal,
}

/// <summary>
/// Blad domenowy wysylki do KSeF. <see cref="Reason"/> determinuje mapowanie HTTP w KsefController.
/// </summary>
public sealed class KsefException : Exception
{
    public KsefError Reason { get; }

    public KsefException(KsefError reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }
}
