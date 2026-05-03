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
