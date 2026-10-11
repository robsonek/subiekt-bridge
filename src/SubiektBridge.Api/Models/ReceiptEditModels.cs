using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubiektBridge.Api.Models;

// ----------------------------- Receipt detail (GET /receipts/{id}) -----------------------------
// Spec: docs/specs/2026-10-11-receipts-edit-pz-0.21-design.md §4.1. Nadzbiór InvoiceQueryItemDto (pola 1:1), plus
// nagłówek PZ, pozycje read-only, powiązania (dok_DoDokId: FZ/KFS/inny), fakty edytowalności, revision i fakty rozchodu.

public sealed record LinkedDocumentDto(
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subiekt_id")] long SubiektId,
    [property: JsonPropertyName("number")] string? Number
);

public sealed record ReceiptEditabilityDto(
    [property: JsonPropertyName("sfera_can_edit")] bool? SferaCanEdit,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons
);

public sealed record ReceiptItemDto(
    [property: JsonPropertyName("position_id")] long PositionId,
    [property: JsonPropertyName("lp")] int Lp,
    [property: JsonPropertyName("product_subiekt_id")] long? ProductSubiektId,
    [property: JsonPropertyName("ean")] string? Ean,
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("quantity_in_stock_units")] decimal QuantityInStockUnits,
    [property: JsonPropertyName("unit_price_net")] decimal UnitPriceNet,
    [property: JsonPropertyName("discount_percent")] decimal DiscountPercent,
    [property: JsonPropertyName("vat_rate")] decimal VatRate,
    [property: JsonPropertyName("value_net")] decimal? ValueNet,
    [property: JsonPropertyName("value_gross")] decimal? ValueGross,
    [property: JsonPropertyName("issued_stock_units")] decimal? IssuedStockUnits,
    [property: JsonPropertyName("issued_quantity")] decimal? IssuedQuantity
);

public sealed record ReceiptDetailDto(
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
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("original_number")] string OriginalNumber,
    [property: JsonPropertyName("external_reference")] string? ExternalReference,
    [property: JsonPropertyName("warehouse_id")] int? WarehouseId,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("booking_status")] int? BookingStatus,
    [property: JsonPropertyName("period_locked")] bool? PeriodLocked,
    [property: JsonPropertyName("linked_documents")] IReadOnlyList<LinkedDocumentDto> LinkedDocuments,
    [property: JsonPropertyName("editability")] ReceiptEditabilityDto Editability,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("items")] IReadOnlyList<ReceiptItemDto> Items,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings
);

// ----------------------------- PATCH /receipts/{id} -----------------------------

/// <summary>
/// `original_number` jako nie-nullowalny JsonElement: brak pola = ValueKind.Undefined (422 EMPTY_UPDATE), jawne null = ValueKind.Null
/// (422 INVALID_ORIGINAL_NUMBER). Nullable&lt;JsonElement&gt; nie odróżniałby tych przypadków (STJ zostawia null dla obu).
/// </summary>
public sealed record ReceiptHeaderPatchDto(
    [property: JsonPropertyName("original_number")] JsonElement OriginalNumber,
    [property: JsonPropertyName("expected_revision")] string? ExpectedRevision = null
);
