namespace SubiektBridge.Api.Idempotency;

/// <summary>
/// Przestrzeń, w której żyje <c>Idempotency-Key</c> (od v0.21.0): operacja = metoda + ścieżka z kanonicznym id zasobu.
/// Ta sama wartość klucza na innym endpoincie albo na innym dokumencie = osobny wpis, więc replay nigdy nie oddaje odpowiedzi
/// INNEJ operacji. Do v0.21.0 cache był wspólny: PATCH /receipts/{id} kluczem z POST /receipts dostawał zcache'owane
/// InvoiceResponseDto jako „200 zapisane” bez zapisu (Greptile PR #20 W1), a KFS faktury 1 wracała jako korekta faktury 2.
/// Każdy endpoint z Idempotency-Key ma tu swoją fabrykę - <see cref="IdempotencyStore"/> nie przyjmuje klucza bez zakresu.
/// Wpisy sprzed v0.21.0 (sam klucz, bez zakresu) wygasły po TTL i od v0.22.0 nie są czytane ani kasowane przez żadną operację.
/// </summary>
public sealed class IdempotencyScope
{
    private IdempotencyScope(string operation) => Operation = operation;

    /// <summary>Np. <c>PATCH /api/v1/receipts/sub_123</c>; nie zawiera '\n', więc <see cref="StorageKey"/> jest jednoznaczny.</summary>
    public string Operation { get; }

    public static readonly IdempotencyScope CreateInvoice = new("POST /api/v1/invoices");
    public static readonly IdempotencyScope CreateReceipt = new("POST /api/v1/receipts");
    public static readonly IdempotencyScope CreateTransfer = new("POST /api/v1/transfers");

    public static IdempotencyScope CreateCorrection(long invoiceSubiektId)
        => new($"POST /api/v1/invoices/sub_{invoiceSubiektId}/corrections");

    public static IdempotencyScope CreateSettlement(long documentSubiektId)
        => new($"POST /api/v1/invoices/sub_{documentSubiektId}/settlements");

    public static IdempotencyScope BookBankTransaction(long hbId)
        => new($"POST /api/v1/bank-transactions/{hbId}/book");

    public static IdempotencyScope UpdateReceiptHeader(long receiptSubiektId)
        => new($"PATCH /api/v1/receipts/sub_{receiptSubiektId}");

    /// <summary>Klucz wiersza w idempotency.db. Klucz z nagłówka HTTP nie zawiera '\n', więc nie koliduje z żadnym kluczem klienta.</summary>
    internal string StorageKey(string idempotencyKey) => Operation + "\n" + idempotencyKey;

    public override string ToString() => Operation;
}
