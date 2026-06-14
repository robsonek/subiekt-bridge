using System.Text.Json.Serialization;

namespace SubiektBridge.Api.Models;

// ----------------------------- Bank transactions (surowe przelewy z wyciągu, hb_Transakcja) -----------------------------
//
// Read-only. hb_Transakcja to zaimportowane linie wyciągu PRZED zaksięgowaniem. Sfera nie wystawia
// API do ich księgowania - most je tylko CZYTA (SELECT) i wystawia konsumentowi (Laravel).
//
// ZAKRES MOSTU = DANE, nie polityka. Most NIE dopasowuje przelewów do należności (tiery, auto vs ręcznie)
// - to robi Laravel (jak dziś dopasowuje GET /invoices do zamówień). Most wystawia obie strony danych:
// surowe przelewy (poniżej) + otwarte rozrachunki (OpenReceivableDto). Księgowanie/rozliczenie to osobne
// endpointy (operacje Sfery) wołane przez Laravel po decyzji.

public sealed record BankTransactionQueryRequestDto(
    [property: JsonPropertyName("direction")] string? Direction,        // "in" (C/wpłata) / "out" (D/wypłata) / null=oba
    [property: JsonPropertyName("unbooked_only")] bool UnbookedOnly = true,
    [property: JsonPropertyName("from")] string? From = null,           // YYYY-MM-DD (hb_DataKsiegowania)
    [property: JsonPropertyName("to")] string? To = null,
    [property: JsonPropertyName("limit")] int Limit = 200
);

public sealed record BankTransactionDto(
    [property: JsonPropertyName("hb_id")] long HbId,                    // hb_IdTransakcji
    [property: JsonPropertyName("date")] string? Date,                  // hb_DataKsiegowania
    [property: JsonPropertyName("amount")] decimal Amount,             // hb_Kwota
    [property: JsonPropertyName("direction")] string Direction,        // "in" (C) / "out" (D)
    [property: JsonPropertyName("contractor_name")] string? ContractorName,   // hb_Kontrahent (nazwa+adres z przelewu)
    [property: JsonPropertyName("contractor_account")] string? ContractorAccount, // hb_RachKontrahent
    // contractor_id: rozpoznany kontrahent po rachunku nadawcy (rb__RachBankowy) - DANA pomocnicza dla
    // matchingu po stronie Laravela, NIE decyzja. null = rachunku nie ma na żadnej karcie.
    [property: JsonPropertyName("contractor_id")] long? ContractorId,
    [property: JsonPropertyName("title")] string? Title,               // hb_Tytul
    [property: JsonPropertyName("booked")] bool Booked                 // hb_idOperacjiBankowej != NULL
);

// ----------------------------- Otwarte rozrachunki (druga strona dla matchingu w Laravelu) -----------------------------

public sealed record OpenReceivableQueryRequestDto(
    [property: JsonPropertyName("direction")] string? Direction,        // "in"=należności (39) / "out"=zobowiązania (40) / null=oba
    [property: JsonPropertyName("contractor_id")] long? ContractorId,   // filtr nzf_IdObiektu (opcjonalny)
    [property: JsonPropertyName("amount")] decimal? Amount,             // filtr exact nzf_Wartosc (opcjonalny)
    [property: JsonPropertyName("limit")] int Limit = 500
);

public sealed record OpenReceivableDto(
    [property: JsonPropertyName("rozrachunek_subiekt_id")] long RozrachunekSubiektId,  // nzf_Id
    [property: JsonPropertyName("kind")] string Kind,                                 // "receivable" (39) / "liability" (40)
    [property: JsonPropertyName("invoice_number")] string? InvoiceNumber,             // nzf_NumerPelny
    [property: JsonPropertyName("document_subiekt_id")] long? DocumentSubiektId,      // nzf_IdDokumentAuto (dok handlowy; null dla BO)
    [property: JsonPropertyName("contractor_id")] long? ContractorId,                 // nzf_IdObiektu
    [property: JsonPropertyName("original_amount")] decimal OriginalAmount,           // nzf_WartoscPierwotna
    [property: JsonPropertyName("remaining")] decimal Remaining,                      // nzf_Wartosc (otwarta kwota)
    [property: JsonPropertyName("date")] string? Date                                 // nzf_Data
);
