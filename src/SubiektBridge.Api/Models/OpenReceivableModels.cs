using System.Text.Json.Serialization;

namespace SubiektBridge.Api.Models;

// ----------------------------- Otwarte naleznosci (kandydaci do dopasowania z przelewem) -----------------------------
//
// READ-ONLY. Most NIE matchuje, NIE klasyfikuje - zwraca okno otwartych naleznosci (rozrachunki sprzedazy
// z otwartym saldem), a "ktora wplata do ktorej naleznosci" rozstrzyga klient (Laravel), tak jak dopasowuje
// GET /invoices do zamowien. Odczyt przez COM Sfery (FinManager.OtworzKolekcje nzf_Typ=39 + atrybuty
// FinDokument), NIE raw SQL na nz__Finanse - kolumny tej tabeli sa kruche (nzf_NumerPelny to atrybut COM,
// nie kolumna, jak dok_NumerPelny), a COM ma zweryfikowana semantyke (WartoscBiezaca = pozostalo).

public sealed record OpenReceivablesQueryRequestDto(
    // Okno kwoty POZOSTALEJ do zaplaty (WartoscBiezaca, PLN). null = brak granicy z danej strony.
    // Klient liczy je sam: [kwota_przelewu, kwota_przelewu/0.97] (procesory typu PayNow potracaja
    // prowizje <=3%, wiec FV >= przelew). Most jest "glupi" - tylko zakres.
    [property: JsonPropertyName("min_amount")] decimal? MinAmount,
    [property: JsonPropertyName("max_amount")] decimal? MaxAmount,
    // TYLKO PLN (default). Inna waluta -> 422 UNSUPPORTED_CURRENCY: WartoscBiezaca jest zawsze w PLN,
    // wiec wiersz walutowy bylby liczba PLN pod etykieta obcej waluty (mylace) i tak nierozliczalny przez most.
    [property: JsonPropertyName("currency")] string? Currency = "PLN",
    // Opcjonalne zawezenie do jednego kontrahenta (ObiektPowiazanyId rozrachunku).
    [property: JsonPropertyName("contractor_id")] long? ContractorId = null,
    // Default 50, hard cap 200 (okno kwoty i tak zaweza; przy duzym zbiorze COM grozil 502/30s).
    [property: JsonPropertyName("limit")] int Limit = 50
);

public sealed record OpenReceivableDto(
    // Bridge id dokumentu zrodlowego -> POST /invoices/{id}/settlements. sub_<DokumentZrodlowyId>.
    [property: JsonPropertyName("document_id")] string DocumentId,
    // DokumentZrodlowyId = nzf_IdDokumentAuto (id dokumentu handlowego, ktory utworzyl rozrachunek).
    [property: JsonPropertyName("document_subiekt_id")] long DocumentSubiektId,
    // Etykieta typu z prefiksu NumerPelny ("FS"/"FZ"/...). COM Typ != nzf_Typ (od GT 1.17), wiec NIE z Typ.
    [property: JsonPropertyName("doc_type")] string DocType,
    // Waluta rozrachunku (symbol z sl_Waluta). Most zwraca tylko pasujace do filtra (default PLN).
    [property: JsonPropertyName("currency")] string Currency,
    // Pozostalo do zaplaty (WartoscBiezaca, PLN) - po tym matchuje klient.
    [property: JsonPropertyName("remaining")] decimal Remaining,
    // ObiektPowiazanyId rozrachunku (kontrahent). null gdy rozrachunek nie jest wystawiony dla kontrahenta.
    [property: JsonPropertyName("contractor_id")] long? ContractorId,
    // Nazwa kontrahenta (Kontrahenci.Wczytaj(ObiektPowiazanyId).Nazwa). null gdy nie udalo sie odczytac.
    [property: JsonPropertyName("contractor_name")] string? ContractorName,
    // Pelny numer dokumentu zrodlowego (NumerPelny rozrachunku), np. "FS 573/05/2026".
    [property: JsonPropertyName("number")] string Number
);
