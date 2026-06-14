namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Powód niepowodzenia rozliczenia rozrachunku - mapowany przez SettlementsController na
/// (status HTTP, code). Jeden enum zamiast wielu typów wyjątków (mniej plików, łatwy switch).
/// </summary>
public enum SettlementError
{
    /// <summary>Dokument handlowy {id} nie istnieje -> 404 INVOICE_NOT_FOUND.</summary>
    DocumentNotFound,
    /// <summary>Dokument istnieje, ale nie ma rozrachunku (goły PZ/MM) -> 422 SETTLEMENT_NOT_SUPPORTED.</summary>
    NoRozrachunek,
    /// <summary>Rozrachunek nie na kontrahencie dokumentu (karta/raty -> centrum) -> 422 SETTLEMENT_NOT_SUPPORTED.</summary>
    SettlementNotSupported,
    /// <summary>Rozrachunek już rozliczony (WartoscBiezaca&lt;=0, np. gotówka auto) -> 422 ALREADY_SETTLED.</summary>
    AlreadySettled,
    /// <summary>MVP obsługuje tylko FS; FZ/inne -> 422 UNSUPPORTED_DOCUMENT_TYPE.</summary>
    UnsupportedDocumentType,
    /// <summary>Operacja bankowa nie istnieje w nz__Finanse -> 422 BANK_OPERATION_NOT_FOUND.</summary>
    BankOperationNotFound,
    /// <summary>Operacja bankowa skonsumowana (dostępne saldo &lt; amount) -> 422 BANK_OPERATION_EXHAUSTED.</summary>
    BankOperationExhausted,
    /// <summary>Kontrahent operacji bankowej != kontrahent rozrachunku -> 422 BANK_OPERATION_CONTRACTOR_MISMATCH.</summary>
    BankOperationContractorMismatch,
    /// <summary>amount > pozostało do zapłaty -> 422 AMOUNT_EXCEEDS_REMAINING.</summary>
    AmountExceedsRemaining,
    /// <summary>Waluta != PLN (RozliczWaluta poza zakresem MVP) -> 422 UNSUPPORTED_CURRENCY.</summary>
    UnsupportedCurrency,
    /// <summary>amount &lt;= 0 -> 422 INVALID_AMOUNT.</summary>
    InvalidAmount,
    /// <summary>Rozliczenie {rozliczenie_id} nie istnieje (DELETE) -> 404 SETTLEMENT_NOT_FOUND.</summary>
    SettlementNotFound,
    /// <summary>Skan anti-duplicate / odczyt stanu padł (FAIL-CLOSED) -> 502 SUBIEKT_QUERY_FAILED.</summary>
    ScanFailed,
    /// <summary>Nieoczekiwany błąd (Rozlicz/Zapisz padł, RozliczenieId=-1 po reload) -> 500 INTERNAL_ERROR.</summary>
    Internal,
}

/// <summary>
/// Błąd domenowy rozliczania rozrachunków. <see cref="Reason"/> determinuje mapowanie HTTP
/// w SettlementsController. Anti-duplicate ma OSOBNY typ <see cref="DuplicateSettlementException"/>
/// (409), bo niesie dane istniejącego rozliczenia do auto-recovery klienta.
/// </summary>
public sealed class SettlementException : Exception
{
    public SettlementError Reason { get; }
    public object? Details { get; }

    public SettlementException(SettlementError reason, string message, Exception? inner = null, object? details = null)
        : base(message, inner)
    {
        Reason = reason;
        Details = details;
    }
}
