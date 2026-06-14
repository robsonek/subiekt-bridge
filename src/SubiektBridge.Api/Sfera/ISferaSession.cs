using SubiektBridge.Api.Models;

namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Abstrakcja nad sesją Sfery. Implementacje:
/// - <see cref="RealSferaSession"/> - prawdziwa sesja COM, tylko Windows.
/// - <see cref="FakeSferaSession"/> - mock dla dev (macOS/Linux) i testów.
///
/// Wszystkie operacje gwarantowane single-threaded przez <c>SferaWorker</c>
/// (nie wołać bezpośrednio z innego wątku niż dedykowany STA worker).
/// </summary>
public interface ISferaSession : IAsyncDisposable
{
    /// <summary>Wystaw fakturę sprzedaży (FS) i zwróć jej dane.</summary>
    Task<InvoiceResponseDto> CreateInvoiceAsync(InvoiceRequestDto request, CancellationToken ct);

    /// <summary>Wystaw fakturę korygującą (KFS) do istniejącej FV.</summary>
    Task<InvoiceResponseDto> CreateCorrectionAsync(
        long sourceSubiektId,
        InvoiceCorrectionRequestDto request,
        CancellationToken ct);

    /// <summary>
    /// Wystaw PZ - Przyjęcie Zewnętrzne. Dodaje towar na magazyn z ceną zakupu (dropshipping).
    /// </summary>
    Task<InvoiceResponseDto> CreateReceiptAsync(ReceiptIssueRequestDto request, CancellationToken ct);

    /// <summary>
    /// Wystaw MM - Przesunięcie Międzymagazynowe. Przenosi stan towaru między magazynami
    /// (source -> dest). Dokument wewnętrzny magazynowy, NIE idzie do KSeF.
    /// </summary>
    Task<TransferResponseDto> CreateTransferAsync(TransferRequestDto request, CancellationToken ct);

    /// <summary>Znajdź towar po EAN. Zwraca null gdy nie istnieje.</summary>
    Task<ProductDto?> FindProductByEanAsync(string ean, CancellationToken ct);

    /// <summary>Znajdź kontrahenta po NIP. Zwraca null gdy nie istnieje.</summary>
    Task<ContractorDto?> FindContractorByNipAsync(string nip, CancellationToken ct);

    /// <summary>
    /// Pobierz listę istniejących FV/KFS z Subiekta (read-only, do dopasowania do
    /// zamówień zafakturowanych przez inny system).
    /// </summary>
    Task<IReadOnlyList<InvoiceQueryItemDto>> QueryInvoicesAsync(InvoiceQueryRequestDto request, CancellationToken ct);

    /// <summary>Pobierz metadata pojedynczej FV po Subiekt ID. Null gdy nie istnieje.</summary>
    Task<InvoiceQueryItemDto?> FindInvoiceByIdAsync(long subiektId, CancellationToken ct);

    /// <summary>Wygeneruj PDF pojedynczej FV (retroaktywnie). Null gdy nie istnieje lub generowanie padło.</summary>
    Task<byte[]?> GetInvoicePdfAsync(long subiektId, CancellationToken ct);

    /// <summary>Health: zwraca wersję Subiekta + status sesji.</summary>
    Task<SferaHealthDto> HealthAsync(CancellationToken ct);

    /// <summary>
    /// Wykonaj read-only SQL query na bazie Subiekta przez Subiekt.Baza.PolaczenieAdoNet.
    /// Tylko SELECT/WITH (whitelist po stronie controllera). Hard cap maxRows=1000.
    /// </summary>
    Task<QueryResultDto> QueryAsync(string sql, int maxRows, CancellationToken ct);

    /// <summary>Escape hatch - generic call do dowolnej metody Sfery (whitelist w configu).</summary>
    Task<object?> InvokeRawAsync(string method, IReadOnlyList<object?> args, CancellationToken ct);

    /// <summary>
    /// Rozlicz rozrachunek dokumentu {documentSubiektId} z istniejącą operacją bankową
    /// (zaimportowanym z wyciągu przelewem). MVP: tylko FS (należność, wpłata BP).
    /// Rzuca <see cref="SettlementException"/> (mapowane na 4xx/5xx) lub
    /// <see cref="DuplicateSettlementException"/> (409) gdy operacja już rozliczona z tym rozrachunkiem.
    /// </summary>
    Task<SettlementResponseDto> CreateSettlementAsync(
        long documentSubiektId,
        SettlementCreateRequestDto request,
        CancellationToken ct);

    /// <summary>
    /// Stan rozliczenia dokumentu: pozostała kwota + lista rozliczeń. Zwraca null gdy dokument
    /// nie istnieje (-> 404). Rzuca <see cref="SettlementException"/>(NoRozrachunek) gdy dokument
    /// bez rozrachunku (-> 422).
    /// </summary>
    Task<SettlementStateResponseDto?> GetSettlementsAsync(long documentSubiektId, CancellationToken ct);

    /// <summary>
    /// Cofnij rozliczenie {rozliczenieId} rozrachunku dokumentu {documentSubiektId}
    /// (FinRozliczenie.Usun - rozkojarza rozrachunek/spłatę, NIE kasuje dokumentów).
    /// Rzuca <see cref="SettlementException"/>(SettlementNotFound/DocumentNotFound).
    /// </summary>
    Task DeleteSettlementAsync(long documentSubiektId, long rozliczenieId, CancellationToken ct);

    /// <summary>
    /// Lista operacji bankowych (BP/BW) z wyciągu - źródło bank_operation_subiekt_id do rozliczeń.
    /// </summary>
    Task<IReadOnlyList<BankOperationDto>> QueryBankOperationsAsync(
        BankOperationQueryRequestDto request,
        CancellationToken ct);

    /// <summary>
    /// Lista SUROWYCH przelewów z wyciągu (hb_Transakcja) - read-only, pula "do zaksięgowania".
    /// Sfera nie wystawia API do hb_Transakcja, więc most czyta przez read-only SQL.
    /// </summary>
    Task<IReadOnlyList<BankTransactionDto>> QueryBankTransactionsAsync(
        BankTransactionQueryRequestDto request,
        CancellationToken ct);

    /// <summary>
    /// Zaksięguj surowy przelew (hb_Transakcja) na operację bankową BP/BW przez Sferę.
    /// Zwraca nzf_Id operacji + flagę `linked` (czy Subiekt ustawił hb_idOperacjiBankowej).
    /// Jeśli transakcja już zaksięgowana → zwraca istniejącą (AlreadyBooked). Most NIE matchuje,
    /// dostaje rozkaz "zaksięguj hb_id". Rzuca <see cref="BankBookingException"/>.
    /// </summary>
    Task<BookResultDto> BookBankTransactionAsync(long hbId, long? contractorSubiektId, bool keepUnlinked, CancellationToken ct);

    /// <summary>
    /// Zwraca hb_idOperacjiBankowej dla transakcji (live), albo null gdy niezaksięgowana. Do re-walidacji
    /// idempotency-replay (czy zacache'owana operacja wciąż istnieje/jest powiązana).
    /// </summary>
    Task<long?> GetBookedOperationIdAsync(long hbId, CancellationToken ct);
}

public sealed record SferaHealthDto(
    string SubiektVersion,
    bool SessionActive,
    DateTimeOffset? LastInvoiceAt,
    string? LastError = null);
