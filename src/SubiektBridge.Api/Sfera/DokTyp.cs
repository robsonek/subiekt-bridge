namespace SubiektBridge.Api.Sfera;

/// <summary>
/// dok_Typ dokumentów obsługiwanych przez most (zrzut schematu dok__Dokument, MS_Description): 2=FS, 6=KFS, 9=MM, 10=PZ.
/// Typ dokumentu rozpoznajemy po kolumnie, NIE po prefiksie numeru (symbol numeracji bywa niestandardowy, np. „FH").
/// Statyczna klasa poza windows-only RealSferaSession, żeby mapowanie było testowalne cross-platform.
/// </summary>
public static class DokTyp
{
    public const int FS = 2;
    public const int KFS = 6;
    public const int MM = 9;
    public const int PZ = 10;

    /// <summary>Nazwa do `details.document_type` kodu DUPLICATE_CHECK_UNAVAILABLE i do logów.</summary>
    public static string Name(int dokTyp) => dokTyp switch
    {
        FS => "FS",
        KFS => "KFS",
        MM => "MM",
        PZ => "PZ",
        _ => throw new ArgumentOutOfRangeException(nameof(dokTyp), dokTyp, "Nieobslugiwany dok_Typ (oczekiwane 2/6/9/10)."),
    };
}
