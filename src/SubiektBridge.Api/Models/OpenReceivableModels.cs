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
    // Okno daty dokumentu (nzf_Data), YYYY-MM-DD. Klient podaje zakres wokol przelewu (FV jest przed wplata) -
    // zaweza skan po stronie bazy i jest semantycznie poprawne (swiezy przelew -> swieza FV).
    [property: JsonPropertyName("from")] string? From = null,
    [property: JsonPropertyName("to")] string? To = null,
    // Default 50, hard cap 200. Predykaty ida do filtra OtworzKolekcje (server-side), wiec to cap WYNIKU
    // (najnowsze wg nzf_Data), nie skanu - brak enumeracji calej tabeli (fix perf v0.11.0).
    [property: JsonPropertyName("limit")] int Limit = 50,
    // Wyszukiwarka FV (v0.13.0): fragment dopasowywany case-insensitive do NumerPelny / nazwy kontrahenta / NIP.
    // Filtr robiony w petli (te pola sa juz czytane per wiersz - NumerPelny to atrybut COM, nie kolumna, wiec
    // NIE da sie go dac do SQL OtworzKolekcje). Klient zwykle podaje search BEZ okna kwoty (szuka po nazwie/numerze),
    // ale z 'from' (zaweza skan). null/pusty = brak filtra (zachowanie sprzed v0.13).
    [property: JsonPropertyName("search")] string? Search = null
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
    [property: JsonPropertyName("number")] string Number,
    // Data dokumentu (nzf_Data, COM atrybut "Data"), YYYY-MM-DD. null gdy nie udalo sie odczytac.
    // Operator dopasowuje przelew do FV po dacie - swiezy przelew zwykle do swiezej FV.
    [property: JsonPropertyName("date")] string? Date = null,
    // Wartosc pierwotna rozrachunku (WartoscPoczatkowa = nzf_WartoscPierwotna, PLN brutto) - calkowita
    // kwota FV; "remaining" to ile jeszcze zostalo. Operator widzi czy to wplata pelna czy czesciowa.
    // 0 gdy nie udalo sie odczytac.
    [property: JsonPropertyName("original")] decimal Original = 0m,
    // NIP kontrahenta (z tego samego Kontrahenci.Wczytaj co ContractorName). null dla osob prywatnych
    // (B2C) lub gdy nie udalo sie odczytac. Pomaga rozroznic firmy o podobnych nazwach.
    [property: JsonPropertyName("nip")] string? Nip = null
);

/// <summary>
/// Normalizacja pol kontrahenta z COM dla open-receivables. Wydzielone z RealSferaSession (ktory jest
/// [SupportedOSPlatform("windows")] + COM, wiec nietestowalny na CI) - czysta logika string, testowalna
/// cross-platform. Dzieki temu kontrakt "null dla B2C" jest pokryty testem jednostkowym (nie tylko Fake'iem).
/// </summary>
public static class OpenReceivableFields
{
    /// <summary>
    /// NIP z COM -> null dla B2C: Real Subiekt zwraca dla osoby prywatnej (brak NIP) pusty/whitespace string,
    /// nie null. Mapujemy na null, by Real == Fake == kontrakt DTO ("null dla B2C"); klient wykrywa B2C po nip == null.
    /// </summary>
    public static string? NormalizeNip(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw;

    /// <summary>
    /// Wyszukiwarka FV (v0.13.0): czy wiersz pasuje do frazy (case-insensitive substring na NumerPelny / nazwie
    /// kontrahenta / NIP). Pusta/null fraza = pasuje wszystko (brak filtra). Wydzielone jako static - testowalne
    /// cross-platform (RealSferaSession jest windows-only/COM). Logika dzielona przez Real i Fake (parytet).
    /// </summary>
    public static bool MatchesSearch(string? search, string? number, string? contractorName, string? nip)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        string needle = search.Trim().ToLowerInvariant();

        return Contains(number, needle) || Contains(contractorName, needle) || Contains(nip, needle);

        static bool Contains(string? haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && haystack.ToLowerInvariant().Contains(needle);
    }

    /// <summary>
    /// Neutralizuje wildcardy LIKE (<c>%</c>, <c>_</c>, <c>[</c>) dla frazy idącej jako PARAMETR SQL (SqlParameter)
    /// w wyszukiwarce kontrahentów (v0.14.0). Klasa znaków <c>[x]</c>, BEZ ESCAPE. NIE dubluje apostrofu (parametr
    /// chroni przed injection; <c>''</c> w parametrze wstawiłoby dosłownie dwa apostrofy). Bez tego operator
    /// wpisujący <c>%</c> dopasowałby WSZYSTKICH kontrahentów (samo <c>%</c> = wildcard „dowolny ciąg"). Wydzielone
    /// tu (nie w windows-only RealSferaSession) by było testowalne cross-platform.
    /// </summary>
    public static string EscapeLikeWildcards(string s) => s
        .Replace("[", "[[]")
        .Replace("%", "[%]")
        .Replace("_", "[_]");
}
