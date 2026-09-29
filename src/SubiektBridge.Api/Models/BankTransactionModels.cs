using System.Text.Json.Serialization;

namespace SubiektBridge.Api.Models;

// ----------------------------- Bank transactions (surowe przelewy z wyciągu, hb_Transakcja) -----------------------------
//
// CZYSTY PASSTHROUGH. Most nie interpretuje, nie dopasowuje, nie rozpoznaje kontrahenta - zwraca surowe
// pola hb_Transakcja, których Laravel potrzebuje do WLASNEGO matchingu (kwota+kontrahent+rachunek, tiery,
// auto vs ręcznie). Most dostaje gotowy rozkaz "zaksięguj X" / "rozlicz Y z Z", sam nie decyduje co z czym.

public sealed record BankTransactionQueryRequestDto(
    [property: JsonPropertyName("direction")] string? Direction,        // "in" (C/wpłata) / "out" (D/wypłata) / null=oba
    [property: JsonPropertyName("unbooked_only")] bool UnbookedOnly = true,  // = bez linku I hb_Status IN (0,4) - pula "do zaksięgowania"
    [property: JsonPropertyName("from")] string? From = null,           // YYYY-MM-DD (hb_DataKsiegowania)
    [property: JsonPropertyName("to")] string? To = null,
    [property: JsonPropertyName("limit")] int Limit = 200
);

public sealed record BankTransactionDto(
    [property: JsonPropertyName("hb_id")] long HbId,                              // hb_IdTransakcji
    [property: JsonPropertyName("date")] string? Date,                           // hb_DataKsiegowania
    [property: JsonPropertyName("amount")] decimal Amount,                       // hb_Kwota
    [property: JsonPropertyName("direction")] string Direction,                  // "in" (C) / "out" (D)
    [property: JsonPropertyName("contractor_name")] string? ContractorName,      // hb_Kontrahent (surowa nazwa+adres z przelewu)
    [property: JsonPropertyName("contractor_account")] string? ContractorAccount, // hb_RachKontrahent (surowy nr rachunku nadawcy)
    [property: JsonPropertyName("title")] string? Title,                         // hb_Tytul
    [property: JsonPropertyName("invoice_number")] string? InvoiceNumber,        // hb_NrFaktury (zwykle puste - klient nie podaje)
    [property: JsonPropertyName("booked")] bool Booked,                          // hb_idOperacjiBankowej != NULL
    // Po zaksięgowaniu = nzf_Id operacji bankowej (gotowy do POST /invoices/{id}/settlements). null = niezaksięgowana.
    [property: JsonPropertyName("bank_operation_subiekt_id")] long? BankOperationSubiektId,
    // Konto wyciągu, na które wpłynął przelew (przez nagłówek wyciągu hb_NaglowekIStopka). rachunek_id = rb_Id
    // (opaque, potrzebny do księgowania na właściwym koncie); rachunek_numer = IBAN wyciągu (czytelny). DANE surowe.
    [property: JsonPropertyName("rachunek_id")] long? RachunekId,
    [property: JsonPropertyName("rachunek_numer")] string? RachunekNumer,
    // hb_Status: 0=NOWA, 1=WYGENEROWANA (zaksięgowana), 2=SKOJARZONA, 3=POMINIĘTA (operator), 4=WSTĘPNIE SKOJARZONA.
    // Księgować da się tylko 0/4 (/book: 422 UNSUPPORTED_HB_STATUS dla innych); `unbooked_only` już to filtruje.
    [property: JsonPropertyName("hb_status")] int HbStatus = 0
);

// ----------------------------- Księgowanie przelewu (hb_Transakcja → operacja bankowa BP/BW) -----------------------------

public sealed record BookRequestDto(
    // Opcjonalny - Laravel decyduje kogo przypisać; brak = operacja bez danych kontrahenta.
    [property: JsonPropertyName("contractor_subiekt_id")] long? ContractorSubiektId = null
);

public sealed record BookResultDto(
    // nzf_Id utworzonej operacji bankowej (gotowy do POST /invoices/{id}/settlements). null gdy nie powstała.
    [property: JsonPropertyName("bank_operation_subiekt_id")] long? BankOperationSubiektId,
    [property: JsonPropertyName("hb_id")] long HbId,
    // Wariant B: most sam ustawia link (raw UPDATE hb_Transakcja). linked=true na każdym sukcesie/already_booked.
    [property: JsonPropertyName("linked")] bool Linked,
    [property: JsonPropertyName("already_booked")] bool AlreadyBooked,
    [property: JsonPropertyName("message")] string? Message = null
);
