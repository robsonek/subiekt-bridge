using System.Text.Json.Serialization;

namespace SubiektBridge.Api.Models;

// ----------------------------- Settlement (rozliczenie rozrachunku) -----------------------------

/// <summary>
/// Request rozliczenia: spina ISTNIEJĄCĄ operację bankową (zaimportowany z wyciągu przelew BP)
/// z rozrachunkiem dokumentu {id}. MVP: tylko FS (należność, wpłata BP).
/// </summary>
public sealed record SettlementCreateRequestDto(
    // nzf_Id zaimportowanej operacji bankowej w nz__Finanse (klient bierze je z GET /bank-operations
    // albo z importu wyciągu w Subiekcie).
    [property: JsonPropertyName("bank_operation_subiekt_id")] long BankOperationSubiektId,
    // Kwota rozliczenia w PLN (waluta rozrachunku). Zaokrąglana do 2 miejsc przed Rozlicz.
    [property: JsonPropertyName("amount")] decimal Amount,
    // Opcjonalne - służy WYŁĄCZNIE jako składnik logicznego Idempotency-Key po stronie klienta
    // i do audytu w logach. Rozliczenie NIE ma pola Uwagi, więc ref nigdzie nie zapisuje się w Subiekcie.
    [property: JsonPropertyName("external_reference")] string? ExternalReference = null
);

public sealed record SettlementResponseDto(
    // FinRozliczenie.RozliczenieId (= nzs_Id) odczytany PO Zapisz+przeładowaniu (przed zapisem = -1).
    [property: JsonPropertyName("rozliczenie_id")] long RozliczenieId,
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("document_subiekt_id")] long DocumentSubiektId,
    [property: JsonPropertyName("rozrachunek_subiekt_id")] long RozrachunekSubiektId,
    [property: JsonPropertyName("bank_operation_subiekt_id")] long BankOperationSubiektId,
    [property: JsonPropertyName("amount")] decimal Amount,
    // WartoscBiezaca rozrachunku po rozliczeniu (pozostało do zapłaty).
    [property: JsonPropertyName("remaining_after")] decimal RemainingAfter,
    [property: JsonPropertyName("is_fully_settled")] bool IsFullySettled,
    [property: JsonPropertyName("settled_at")] DateTimeOffset SettledAt
);

public sealed record SettlementStateResponseDto(
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("document_subiekt_id")] long DocumentSubiektId,
    [property: JsonPropertyName("rozrachunek_subiekt_id")] long RozrachunekSubiektId,
    // WartoscPoczatkowa rozrachunku (nie maleje przy rozliczaniu).
    [property: JsonPropertyName("original_amount")] decimal OriginalAmount,
    // WartoscBiezaca rozrachunku (maleje wraz z rozliczeniami).
    [property: JsonPropertyName("remaining_amount")] decimal RemainingAmount,
    [property: JsonPropertyName("is_fully_settled")] bool IsFullySettled,
    [property: JsonPropertyName("last_settlement_at")] DateTimeOffset? LastSettlementAt,
    [property: JsonPropertyName("settlements")] IReadOnlyList<SettlementLineDto> Settlements
);

/// <summary>Pojedynczy rekord powiązania rozrachunek↔spłata (FinRozliczenie).</summary>
public sealed record SettlementLineDto(
    [property: JsonPropertyName("rozliczenie_id")] long RozliczenieId,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("settled_at")] DateTimeOffset? SettledAt,
    // FinRozliczenie.SplataId -> nz_FinanseSplata.nzs_IdSplaty (nzf_Id operacji bankowej).
    [property: JsonPropertyName("splata_subiekt_id")] long? SplataSubiektId,
    // FinRozliczenie.DlugId -> nz_FinanseSplata.nzs_IdDlugu (nzf_Id rozrachunku).
    [property: JsonPropertyName("dlug_subiekt_id")] long? DlugSubiektId,
    // FinRozliczenie.Typ (FinRozliczenieTypEnum).
    [property: JsonPropertyName("type")] int? Type
);

// ----------------------------- Bank operations (operacje bankowe z wyciągu) -----------------------------

/// <summary>
/// Filtr listy operacji bankowych (BP/BW) do dopasowania z fakturami. Bridge wykonuje przez
/// FinManager.OtworzKolekcje na tabeli nz__Finanse (filtr po nzf_Typ/nzf_Data, whitelist pól).
/// </summary>
public sealed record BankOperationQueryRequestDto(
    [property: JsonPropertyName("from")] string? From,               // YYYY-MM-DD (nzf_Data)
    [property: JsonPropertyName("to")] string? To,                   // YYYY-MM-DD (nzf_Data)
    [property: JsonPropertyName("direction")] string? Direction,     // "in" (BP/19) / "out" (BW/20) / null=oba
    [property: JsonPropertyName("unsettled_only")] bool UnsettledOnly = false,
    [property: JsonPropertyName("limit")] int Limit = 200            // hard cap 1000
);

public sealed record BankOperationDto(
    [property: JsonPropertyName("subiekt_id")] long SubiektId,       // nzf_Id
    [property: JsonPropertyName("direction")] string Direction,      // "in" / "out" / "unknown"
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("amount")] decimal? Amount,          // WartoscPoczatkowa
    [property: JsonPropertyName("remaining")] decimal? Remaining,    // WartoscBiezaca (niewykorzystane w rozliczeniach)
    [property: JsonPropertyName("contractor_id")] long? ContractorId,
    [property: JsonPropertyName("title")] string? Title,             // Tytulem
    [property: JsonPropertyName("number")] string? Number           // NumerPelny
);
