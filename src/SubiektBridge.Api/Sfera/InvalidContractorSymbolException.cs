namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Rzucany gdy kontrahenta trzeba założyć/znaleźć po Symbolu (brak dopasowania po NIP), a Symbol
/// nie mieści się w kolumnie <c>kh_Symbol</c>. Bez tej walidacji MSSQL rzucał <c>0x80040E21</c>
/// (multi-step OLE DB) i klient dostawał 500 - wg kontraktu 5xx jest retry'owane, w nieskończoność,
/// bo symbol sam się nie skróci.
///
/// Controller mapuje na 422 z code='INVALID_CONTRACTOR_SYMBOL'.
/// </summary>
public sealed class InvalidContractorSymbolException : Exception
{
    public string Symbol { get; }

    public InvalidContractorSymbolException(string symbol, string reason)
        : base(reason)
    {
        Symbol = symbol;
    }
}
