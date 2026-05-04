using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SubiektBridge.Api.Models;

/// <summary>
/// Mirror Laravel-side `App\Modules\Invoicing\Bridge\DTOs\InvoiceRequest`.
/// Snake_case JSON do zgodności z konwencjami Laravelowego klienta.
/// </summary>
public sealed record InvoiceRequestDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("issue_date")] string IssueDate,
    [property: JsonPropertyName("sale_date")] string SaleDate,
    [property: JsonPropertyName("payment")] PaymentDto Payment,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("contractor")] ContractorDto Contractor,
    [property: JsonPropertyName("lines")] IReadOnlyList<LineDto> Lines,
    [property: JsonPropertyName("shipping")] ShippingDto Shipping,
    [property: JsonPropertyName("totals")] InvoiceTotalsDto Totals,
    [property: JsonPropertyName("external_reference")] string ExternalReference,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("warehouse_subiekt_id")] int? WarehouseSubiektId = null
);

public sealed record InvoiceCorrectionRequestDto(
    [property: JsonPropertyName("issue_date")] string IssueDate,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("source_is_external")] bool SourceIsExternal,
    [property: JsonPropertyName("source_invoice_number")] string? SourceInvoiceNumber,
    [property: JsonPropertyName("source_invoice_date")] string? SourceInvoiceDate,
    [property: JsonPropertyName("lines")] IReadOnlyList<CorrectionLineDto> Lines,
    [property: JsonPropertyName("external_reference")] string ExternalReference
);

public sealed record PaymentDto(
    [property: JsonPropertyName("attribute")] string Attribute,
    // Null gdy atrybut nie ma odpowiednika *Id w Sferze (PlatnoscGotowka, PlatnoscPrzelew).
    // Wtedy Bridge ustawia tylko {Attribute}Kwota.
    [property: JsonPropertyName("method_subiekt_id")] int? MethodSubiektId,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("is_settled")] bool IsSettled
);

public sealed record ContractorDto(
    [property: JsonPropertyName("is_person")] bool IsPerson,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("nip")] string? Nip,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("full_name")] string? FullName,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")] string? LastName,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("address")] AddressDto Address
);

public sealed record AddressDto(
    [property: JsonPropertyName("street")] string Street,
    [property: JsonPropertyName("post_code")] string PostCode,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("country_code")] string CountryCode
);

public sealed record LineDto(
    [property: JsonPropertyName("ean")] string? Ean,
    [property: JsonPropertyName("name_fallback")] string NameFallback,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("unit_price_gross")] decimal UnitPriceGross,
    [property: JsonPropertyName("vat_rate")] decimal VatRate
);

/// <summary>
/// Pozycja korygująca - ten sam shape JSON co <see cref="LineDto"/>, ujemna `quantity`
/// dla zwrotu (pomniejszenie). Laravel używa wspólnego LineDto z tym samym kluczem
/// `quantity` dla FS/KFS/PZ - C# musi zachować tę zgodność, inaczej KFS ląduje
/// z pustą ilością i Subiekt księguje 0 sztuk korekty.
/// </summary>
public sealed record CorrectionLineDto(
    [property: JsonPropertyName("ean")] string? Ean,
    [property: JsonPropertyName("name_fallback")] string NameFallback,
    [property: JsonPropertyName("quantity")] int QuantityChange,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("unit_price_gross")] decimal UnitPriceGross,
    [property: JsonPropertyName("vat_rate")] decimal VatRate
);

public sealed record ShippingDto(
    [property: JsonPropertyName("include")] bool Include,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("unit_price_gross")] decimal UnitPriceGross,
    [property: JsonPropertyName("vat_rate")] decimal VatRate
);

public sealed record InvoiceTotalsDto(
    [property: JsonPropertyName("net")] decimal? Net = null,
    [property: JsonPropertyName("vat")] decimal? Vat = null,
    [property: JsonPropertyName("gross")] decimal Gross = 0m
);

// ----------------------------- Response -----------------------------

public sealed record InvoiceResponseDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("subiekt_id")] long SubiektId,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("contractor_subiekt_id")] long ContractorSubiektId,
    [property: JsonPropertyName("totals")] InvoiceTotalsDto Totals,
    [property: JsonPropertyName("pdf_url")] string? PdfUrl,
    [property: JsonPropertyName("pdf_base64")] string? PdfBase64
);

// ----------------------------- Receipt (PZ) -----------------------------

/// <summary>
/// PZ - Przyjęcie Zewnętrzne. Dropshipping flow: dodaje towar na magazyn z ceną zakupu
/// po zakupie u dostawcy. Jedno PZ per dostawca (unique supplier per zamówienie).
/// </summary>
public sealed record ReceiptIssueRequestDto(
    [property: JsonPropertyName("issue_date")] string IssueDate,
    [property: JsonPropertyName("warehouse_subiekt_id")] int? WarehouseSubiektId,
    [property: JsonPropertyName("supplier")] ContractorDto Supplier,
    [property: JsonPropertyName("lines")] IReadOnlyList<LineDto> Lines,
    [property: JsonPropertyName("source_invoice_subiekt_id")] long? SourceInvoiceSubiektId,
    [property: JsonPropertyName("external_reference")] string ExternalReference,
    [property: JsonPropertyName("notes")] string Notes
);

// ----------------------------- Query (GET /invoices) -----------------------------

/// <summary>
/// Filtr zapytania o istniejące FV w Subiekcie. Bridge wykonuje przez Sferę
/// SuDokumentyManager.OtworzKolekcje(filtr, sort) - filtr to SQL WHERE clause
/// budowany z białej listy pól (klient nie podaje raw SQL).
/// </summary>
public sealed record InvoiceQueryRequestDto(
    [property: JsonPropertyName("from")] string? From,                    // YYYY-MM-DD
    [property: JsonPropertyName("to")] string? To,                        // YYYY-MM-DD
    [property: JsonPropertyName("type")] string? Type,                    // FS / KFS / null=oba
    [property: JsonPropertyName("notes_contains")] string? NotesContains, // LIKE %X% w dok_Uwagi
    [property: JsonPropertyName("nip")] string? Nip,                      // NIP kontrahenta
    [property: JsonPropertyName("limit")] int Limit = 200                 // hard cap 1000
);

/// <summary>Pojedynczy wpis listy FV - wystarczające metadata do dopasowania do Order.</summary>
public sealed record InvoiceQueryItemDto(
    [property: JsonPropertyName("subiekt_id")] long SubiektId,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("issue_date")] string? IssueDate,
    [property: JsonPropertyName("contractor_id")] long? ContractorId,
    [property: JsonPropertyName("contractor_nip")] string? ContractorNip,
    [property: JsonPropertyName("contractor_name")] string? ContractorName,
    [property: JsonPropertyName("net_amount")] decimal? NetAmount,
    [property: JsonPropertyName("vat_amount")] decimal? VatAmount,
    [property: JsonPropertyName("gross_amount")] decimal? GrossAmount,
    [property: JsonPropertyName("notes")] string? Notes
);

// ----------------------------- Reference -----------------------------

public sealed record ProductDto(
    [property: JsonPropertyName("subiekt_id")] long SubiektId,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("ean")] string Ean,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("vat_rate")] decimal VatRate,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("is_active")] bool IsActive
);

public sealed record WarehouseDto(
    [property: JsonPropertyName("subiekt_id")] int SubiektId,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_main")] bool IsMain
);

// ----------------------------- Error -----------------------------

public sealed record ErrorResponseDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] object? Details = null,
    [property: JsonPropertyName("retry_after_seconds")] int? RetryAfterSeconds = null
);

// ----------------------------- Health -----------------------------

public sealed record HealthResponseDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("bridge_version")] string BridgeVersion,
    [property: JsonPropertyName("subiekt_version")] string SubiektVersion,
    [property: JsonPropertyName("sfera_session")] string SferaSession,
    [property: JsonPropertyName("last_invoice_at")] DateTimeOffset? LastInvoiceAt,
    [property: JsonPropertyName("queue_depth")] int QueueDepth,
    [property: JsonPropertyName("last_error")] string? LastError = null
);

// ----------------------------- Raw -----------------------------

public sealed record SferaRawRequestDto(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("args")] IReadOnlyList<object?> Args
);
