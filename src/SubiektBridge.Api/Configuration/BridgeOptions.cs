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

    public int IdempotencyTtlDays { get; init; } = 30;

    /// <summary>
    /// Sciezka do install dir (default: dir w ktorym uruchomil sie SubiektBridge.Api.exe).
    /// Endpoint POST /admin/update spawnuje update-bridge.ps1 z tej lokacji.
    /// </summary>
    public string? InstallDir { get; init; }

    /// <summary>True (default) - POST /admin/update aktywny. False - wylaczony 404.</summary>
    public bool AllowSelfUpdate { get; init; } = true;

    /// <summary>
    /// Wlacza ksiegowanie home-bankingu (POST /bank-transactions/{hb_id}/book, wariant B: Sfera tworzy
    /// operacje + most domyka link raw UPDATE hb_Transakcja). Domyslnie FALSE -> endpoint zwraca 501-stub.
    /// Kill-switch bez redeployu: gdy raw UPDATE zaczalby szkodzic, ustaw false i restart. Wlaczac dopiero
    /// po zielonym tescie odwracalnym na prodzie (docs/PLAN-home-banking-booking-variant-b.md sekcja 7).
    /// </summary>
    public bool EnableHbBooking { get; init; } = false;
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
