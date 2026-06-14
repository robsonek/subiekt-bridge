namespace SubiektBridge.Api.Sfera;

public enum BookError
{
    /// <summary>hb_Transakcja {hb_id} nie istnieje -> 404 BANK_TRANSACTION_NOT_FOUND.</summary>
    TransactionNotFound,
    /// <summary>Transakcja nie ma powiązanego konta wyciągu (hb_NaglowekIStopka.hb_IdRachunku) -> 422 NO_BANK_ACCOUNT.</summary>
    NoAccount,
    /// <summary>hb_Oznaczenie inne niż 'C'/'D' (puste/nieznane) -> 422 INVALID_DIRECTION (NIE tworzymy BP po cichu).</summary>
    InvalidDirection,
    /// <summary>
    /// Rachunek wyciągu jest walutowy (rb_IdWaluty != 'PLN') -> 422 UNSUPPORTED_FOREIGN_ACCOUNT.
    /// Wariant B obsługuje tylko PLN; dla waluty trigger tr_NzFinanse_OpBank i tak zrolluje INSERT
    /// (RAISERROR), więc odrzucamy wcześniej z czytelnym błędem (fail-fast, bez tworzenia BP).
    /// </summary>
    ForeignAccount,
    /// <summary>
    /// hb_Status spoza {0=NOWA, 4=WSTĘPNIESKOJARZONA} przy braku hb_idOperacjiBankowej -> 422 UNSUPPORTED_HB_STATUS.
    /// Nie nadpisujemy linii w nietypowym stanie (np. 2=SKOJARZONA z wyciągiem) - świadomie poza zakresem.
    /// </summary>
    UnsupportedStatus,
    /// <summary>
    /// COM/Sfera padło, ALBO raw UPDATE padł a BP został CZYSTO cofnięty (brak orphana) -> 500 HB_BOOKING_FAILED.
    /// Stan spójny (operacja nie istnieje) → bezpieczny retry po stronie klienta.
    /// </summary>
    Internal,
    /// <summary>
    /// Operacja bankowa powstała, ale linku NIE udało się domknąć I rollback BP zawiódł (ALBO anomalia) ->
    /// 500 HB_BOOKING_ORPHAN. Stan NIESPÓJNY: operacja bez powiązania w księgach. Wymaga ręcznej interwencji
    /// (usuń operację w module Bankowość). Klient NIE może retry'ować na ślepo (zrobiłby kolejny BP).
    /// </summary>
    Orphan,
}

/// <summary>
/// Błąd księgowania surowego przelewu (hb_Transakcja) na operację bankową przez Sferę.
/// </summary>
public sealed class BankBookingException : Exception
{
    public BookError Reason { get; }

    public BankBookingException(BookError reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }
}
