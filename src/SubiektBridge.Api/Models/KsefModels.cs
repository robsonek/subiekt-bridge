using System.Text.Json.Serialization;

namespace SubiektBridge.Api.Models;

// ----------------------------- KSeF (wysylka e-Faktur) -----------------------------

/// <summary>
/// Stan KSeF dokumentu - wspolna odpowiedz POST /invoices/{id}/ksef (200/202) i GET .../ksef (200).
/// POST zwraca ja dla wynikow nie-bledowych; bledy ida wyjatkiem KsefException -> ErrorResponseDto.
/// </summary>
public sealed record KsefStatusResponseDto(
    // Bridge id dokumentu ("sub_<id>") - konwencja document_id jak SettlementResponseDto.
    [property: JsonPropertyName("document_id")] string DocumentId,
    // Mapowanie StatusKSeFEnum (0-8) -> string, patrz KsefStatusMap.
    [property: JsonPropertyName("ksef_status")] string KsefStatus,
    // Numer nadany przez KSeF (null dopoki niezarejestrowana).
    [property: JsonPropertyName("ksef_number")] string? KsefNumber,
    // Data nadania numeru KSeF (SuDokument.DataNumeruKSeF), YYYY-MM-DD.
    [property: JsonPropertyName("ksef_number_date")] string? KsefNumberDate,
    // Opis bledu / ostatni OperacjaWTle.Blad (null przy sukcesie).
    [property: JsonPropertyName("message")] string? Message
);

/// <summary>
/// Mapowanie StatusKSeFEnum (Sfera, od GT 1.77) na stringi API. JEDNO zrodlo prawdy
/// dla RealSferaSession i FakeSferaSession - nie duplikowac switchy po plikach.
/// </summary>
public static class KsefStatusMap
{
    public const string None = "none";                              // 0 gtaStatusKSeFBrak
    public const string Validated = "validated";                    // 1 gtaStatusKSeFDoWyslania
    public const string Generated = "generated";                    // 2 gtaStatusKSeFDoWyslaniaWygenerowanaEFaktura
    public const string Sending = "sending";                        // 3 gtaStatusKSeFWTrakcieWysylania
    public const string Processing = "processing";                  // 4 gtaStatusKSeFPrzetwarzanaWKSeF
    public const string Registered = "registered";                  // 5 gtaStatusKSeFZarejestrowanoWKSeF
    public const string Rejected = "rejected";                      // 6 gtaStatusKSeFOdrzuconoWKSeF
    public const string ValidationFailed = "validation_failed";     // 7 gtaStatusKSeFNieSpelniaWymaganKSeF
    public const string CommunicationError = "communication_error"; // 8 gtaStatusKSeFBladKomunikacjiZKSeF

    public static string ToApiString(int statusKsef) => statusKsef switch
    {
        0 => None,
        1 => Validated,
        2 => Generated,
        3 => Sending,
        4 => Processing,
        5 => Registered,
        6 => Rejected,
        7 => ValidationFailed,
        8 => CommunicationError,
        _ => "unknown",
    };
}
