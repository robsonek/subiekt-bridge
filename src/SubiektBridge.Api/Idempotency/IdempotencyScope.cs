namespace SubiektBridge.Api.Idempotency;

/// <summary>
/// Przestrzeń, w której żyje <c>Idempotency-Key</c> (od v0.21.0): operacja = metoda + ścieżka z kanonicznym id zasobu.
/// Ta sama wartość klucza na innym endpoincie albo na innym dokumencie = osobny wpis, więc replay nigdy nie oddaje odpowiedzi
/// INNEJ operacji. Do v0.21.0 cache był wspólny: PATCH /receipts/{id} kluczem z POST /receipts dostawał zcache'owane
/// InvoiceResponseDto jako „200 zapisane” bez zapisu (Greptile PR #20 W1), a KFS faktury 1 wracała jako korekta faktury 2.
/// Każdy endpoint z Idempotency-Key ma tu swoją fabrykę - <see cref="IdempotencyStore"/> nie przyjmuje klucza bez zakresu.
/// </summary>
public sealed class IdempotencyScope
{
    private IdempotencyScope(string operation, bool readsUnscopedEntries)
    {
        Operation = operation;
        ReadsUnscopedEntries = readsUnscopedEntries;
    }

    /// <summary>Np. <c>PATCH /api/v1/receipts/sub_123</c>; nie zawiera '\n', więc <see cref="StorageKey"/> jest jednoznaczny.</summary>
    public string Operation { get; }

    /// <summary>
    /// Wpisy sprzed v0.21.0 (zapisane pod samym kluczem, bez zakresu) czyta TYLKO operacja istniejąca przed v0.21.0 - ponowienie
    /// przechodzące przez aktualizację dostaje replay jak dotąd. PATCH ich nie czyta (stary wpis to zawsze odpowiedź któregoś POST).
    /// Do usunięcia w v0.22.0: wpisy bez zakresu wygasają po TTL (14 dni od wdrożenia v0.21.0).
    /// </summary>
    internal bool ReadsUnscopedEntries { get; }

    public static readonly IdempotencyScope CreateInvoice = new("POST /api/v1/invoices", readsUnscopedEntries: true);
    public static readonly IdempotencyScope CreateReceipt = new("POST /api/v1/receipts", readsUnscopedEntries: true);
    public static readonly IdempotencyScope CreateTransfer = new("POST /api/v1/transfers", readsUnscopedEntries: true);

    public static IdempotencyScope CreateCorrection(long invoiceSubiektId)
        => new($"POST /api/v1/invoices/sub_{invoiceSubiektId}/corrections", readsUnscopedEntries: true);

    public static IdempotencyScope CreateSettlement(long documentSubiektId)
        => new($"POST /api/v1/invoices/sub_{documentSubiektId}/settlements", readsUnscopedEntries: true);

    public static IdempotencyScope BookBankTransaction(long hbId)
        => new($"POST /api/v1/bank-transactions/{hbId}/book", readsUnscopedEntries: true);

    public static IdempotencyScope UpdateReceiptHeader(long receiptSubiektId)
        => new($"PATCH /api/v1/receipts/sub_{receiptSubiektId}", readsUnscopedEntries: false);

    /// <summary>Klucz wiersza w idempotency.db. Klucz z nagłówka HTTP nie zawiera '\n', więc nie koliduje z wpisami bez zakresu.</summary>
    internal string StorageKey(string idempotencyKey) => Operation + "\n" + idempotencyKey;

    public override string ToString() => Operation;
}
