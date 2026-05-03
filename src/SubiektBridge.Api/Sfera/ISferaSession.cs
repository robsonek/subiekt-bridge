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

    /// <summary>Znajdź towar po EAN. Zwraca null gdy nie istnieje.</summary>
    Task<ProductDto?> FindProductByEanAsync(string ean, CancellationToken ct);

    /// <summary>Znajdź kontrahenta po NIP. Zwraca null gdy nie istnieje.</summary>
    Task<ContractorDto?> FindContractorByNipAsync(string nip, CancellationToken ct);

    /// <summary>Health: zwraca wersję Subiekta + status sesji.</summary>
    Task<SferaHealthDto> HealthAsync(CancellationToken ct);

    /// <summary>Escape hatch - generic call do dowolnej metody Sfery (whitelist w configu).</summary>
    Task<object?> InvokeRawAsync(string method, IReadOnlyList<object?> args, CancellationToken ct);
}

public sealed record SferaHealthDto(
    string SubiektVersion,
    bool SessionActive,
    DateTimeOffset? LastInvoiceAt,
    string? LastError = null);
