namespace SubiektBridge.Api.Sfera;

/// <summary>
/// dok_Typ dokumentów obsługiwanych przez most (zrzut schematu dok__Dokument, MS_Description): 2=FS, 6=KFS, 9=MM, 10=PZ.
/// Typ dokumentu rozpoznajemy po kolumnie, NIE po prefiksie numeru (symbol numeracji bywa niestandardowy, np. „FH").
/// Statyczna klasa poza windows-only RealSferaSession, żeby mapowanie było testowalne cross-platform.
/// </summary>
public static class DokTyp
{
    public const int FZ = 1;
    public const int FS = 2;
    public const int KFS = 6;
    public const int MM = 9;
    public const int PZ = 10;

    /// <summary>
    /// Pełna lista symbol → dok_Typ z MS_Description kolumny (zrzut schematu 1.88 HF3, dbo.dok__Dokument.sql):
    /// {1-FZ,2-FS,3-RZ,4-RS,5-KFZ,6-KFS,9-MM,10-PZ,11-WZ,12-PW,13-RW,14-ZW,15-ZD,16-ZK,21-PA,29-IW,35-ZPZ,36-ZWZ,51-TS,62-FM,67-KFM,72-ZM}.
    /// Używana przez filtr `type` listingu (InvoiceQueryFields.PlanType) — typ idzie do SQL po kolumnie, nie po prefiksie numeru.
    /// Case-insensitive (klient podaje "fs"/"FS").
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> BySymbol = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["FZ"] = FZ, ["FS"] = FS, ["RZ"] = 3, ["RS"] = 4, ["KFZ"] = 5, ["KFS"] = KFS, ["MM"] = MM, ["PZ"] = PZ,
        ["WZ"] = 11, ["PW"] = 12, ["RW"] = 13, ["ZW"] = 14, ["ZD"] = 15, ["ZK"] = 16, ["PA"] = 21, ["IW"] = 29,
        ["ZPZ"] = 35, ["ZWZ"] = 36, ["TS"] = 51, ["FM"] = 62, ["KFM"] = 67, ["ZM"] = 72,
    };

    /// <summary>dok_Typ dla symbolu typu dokumentu (po trim, case-insensitive) albo null, gdy symbol nie jest typem Subiekta (np. niestandardowy „FH").</summary>
    public static int? FromSymbol(string? symbol)
        => !string.IsNullOrWhiteSpace(symbol) && BySymbol.TryGetValue(symbol.Trim(), out var typ) ? typ : null;

    /// <summary>Nazwa do `details.document_type` kodu DUPLICATE_CHECK_UNAVAILABLE i do logów.</summary>
    public static string Name(int dokTyp) => dokTyp switch
    {
        FS => "FS",
        KFS => "KFS",
        MM => "MM",
        PZ => "PZ",
        _ => throw new ArgumentOutOfRangeException(nameof(dokTyp), dokTyp, "Nieobslugiwany dok_Typ (oczekiwane 2/6/9/10)."),
    };

    /// <summary>Nazwa typu dokumentu powiązanego (linked_documents[].type): FZ/FS/KFS/MM/PZ, inaczej "TYP_&lt;n&gt;" - nigdy nie rzuca.</summary>
    public static string NameOrUnknown(int dokTyp) => dokTyp switch
    {
        FZ => "FZ",
        FS => "FS",
        KFS => "KFS",
        MM => "MM",
        PZ => "PZ",
        _ => $"TYP_{dokTyp}",
    };
}
