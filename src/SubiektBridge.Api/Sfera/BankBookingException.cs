namespace SubiektBridge.Api.Sfera;

public enum BookError
{
    /// <summary>hb_Transakcja {hb_id} nie istnieje -> 404 BANK_TRANSACTION_NOT_FOUND.</summary>
    TransactionNotFound,
    /// <summary>Transakcja nie ma powiązanego konta wyciągu (hb_NaglowekIStopka.hb_IdRachunku) -> 422 NO_BANK_ACCOUNT.</summary>
    NoAccount,
    /// <summary>hb_Oznaczenie inne niż 'C'/'D' (puste/nieznane) -> 422 INVALID_DIRECTION (NIE tworzymy BP po cichu).</summary>
    InvalidDirection,
    /// <summary>COM/Sfera padło przy tworzeniu operacji bankowej -> 500 INTERNAL_ERROR.</summary>
    Internal,
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
