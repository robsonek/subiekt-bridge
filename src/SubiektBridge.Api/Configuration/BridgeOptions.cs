namespace SubiektBridge.Api.Configuration;

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    /// <summary>Statyczny API key oczekiwany w nagłówku X-Bridge-Token.</summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>True na dev (macOS/Linux) - używa FakeSferaSession bez COM-u.</summary>
    public bool UseFakeSfera { get; init; } = false;

    /// <summary>Whitelist metod dostępnych przez /api/v1/sfera/raw (escape hatch).</summary>
    public IReadOnlyList<string> AllowedRawSferaMethods { get; init; } = Array.Empty<string>();

    public string IdempotencyStorePath { get; init; } = "idempotency.db";

    /// <summary>
    /// Retencja cache idempotency (dni). Nie krótsza niż okno automatycznych ponowień klienta tym samym kluczem
    /// (u klienta Laravel: 14 dni) - replay z cache przy padniętym Subiekcie daje 503, a anti-duplicate po
    /// external_reference jest fail-open. Później ponowienie trafia w anti-duplicate (409 DUPLICATE_* tylko przy
    /// udanym wykryciu; błąd skanu przepuszcza zapis).
    /// Celowo NIE ma tego klucza w szablonie appsettings.Production.json (self-update go zachowuje - przypięta
    /// wartość blokowałaby zmianę domyślnej).
    /// </summary>
    public int IdempotencyTtlDays { get; init; } = 14;

    /// <summary>
    /// Sciezka do install dir (default: dir w ktorym uruchomil sie SubiektBridge.Api.exe).
    /// Endpoint POST /admin/update spawnuje update-bridge.ps1 z tej lokacji.
    /// </summary>
    public string? InstallDir { get; init; }

    /// <summary>True (default) - POST /admin/update aktywny. False - wylaczony 404.</summary>
    public bool AllowSelfUpdate { get; init; } = true;

    /// <summary>
    /// Ksiegowanie home-bankingu (POST /bank-transactions/{hb_id}/book, wariant B: Sfera tworzy operacje +
    /// most domyka link raw UPDATE hb_Transakcja). **Domyslnie TRUE** - endpoint aktywny od razu po deployu
    /// (self-update zachowuje appsettings, wiec brak klucza => domyslny ON; klient nie ma dostepu do serwera,
    /// nie ma jak ustawic flagi recznie). Pozostaje jako wylacznik: ustaw false + restart, by wrocic do 501-stub.
    /// Integralnosc danych zapewniaja mechanizmy w kodzie (guard IS NULL + @@ROWCOUNT, rollback/orphan->500,
    /// guardy PLN/status/kierunek, fail-closed idempotency) - NIEZALEZNE od tej flagi. R2/R3 niezweryfikowane
    /// empirycznie (brak dostepu do serwera do testu sekcja 7) - swiadome ryzyko wlasciciela.
    /// </summary>
    public bool EnableHbBooking { get; init; } = true;

    /// <summary>
    /// Cap (sekundy) na czekanie POST /invoices/{id}/ksef na wynik operacji KSeF (poll OperacjaWTle).
    /// Po przekroczeniu endpoint zwraca 202 - wysylka dokancza sie w tle Sfery, klient ponawia POST
    /// (idempotentny advance). Min egzekwowane w kodzie: 5 s.
    /// </summary>
    public int KsefSendTimeoutSeconds { get; init; } = 90;

    /// <summary>
    /// Listing GET /invoices i GET /receipts z read-only SQL poza wątkiem STA (od v0.21.6, InvoiceListingSql). **Domyślnie TRUE.**
    /// FALSE = dawna ścieżka COM (OtworzKolekcje + wczytanie każdego dokumentu, 0,5–1,5 s/dokument, blokuje kolejkę STA) -
    /// wyłącznie jawny przełącznik awaryjny, nigdy cichy fallback po błędzie SQL.
    /// </summary>
    public bool InvoiceListingViaSql { get; init; } = true;

    /// <summary>
    /// NIP i nazwa kontrahenta w odczytach pojedynczego dokumentu (GET /invoices/{id}, GET/PATCH /receipts/{id}) i open-receivables/payables
    /// z read-only SQL (adr__Ewid, od v0.21.7, ContractorNipName) zamiast COM Kontrahenci.Wczytaj (~2,4 s przy złym planie procedury InsERT).
    /// **Domyślnie TRUE.** Awaria SQL i tak wraca do COM (WARNING); FALSE = wyłącznie dawna ścieżka COM (przełącznik awaryjny, czytany przy starcie).
    /// </summary>
    public bool ContractorReadViaSql { get; init; } = true;
}

public sealed class SubiektOptions
{
    public const string SectionName = "Subiekt";

    public int Product { get; init; } = 1;
    public int Authentication { get; init; } = 0;
    public string Server { get; init; } = string.Empty;
    public string Database { get; init; } = string.Empty;
    public string DbUser { get; init; } = string.Empty;
    public string DbPassword { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;
    public string OperatorPassword { get; init; } = string.Empty;
    public int? PdfTemplateId { get; init; }
    public string Encoding { get; init; } = "windows-1250";
}
