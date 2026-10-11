namespace SubiektBridge.Api.Controllers;

/// <summary>
/// Parser `sub_&lt;id&gt;` wspólny dla kontrolerów (invoices/receipts/settlements/ksef). Zakres <c>1 ≤ id ≤ int.MaxValue</c>: identyfikatory Sfery
/// to COM Long (32-bit) — bez kontroli `(int)4294967297 == 1` i `Dokumenty.Istnieje` sprawdzałoby INNY dokument (Codex r4 #6). Poza zakresem →
/// 422 INVALID_BRIDGE_ID bez dotykania COM.
/// </summary>
public static class BridgeIds
{
    public const string Prefix = "sub_";

    public static bool TryParseSubiektId(string? bridgeId, out long subiektId)
    {
        subiektId = 0;
        if (string.IsNullOrEmpty(bridgeId)) return false;
        if (bridgeId.StartsWith(Prefix, StringComparison.Ordinal)
            && long.TryParse(bridgeId.AsSpan(Prefix.Length), out long parsed)
            && parsed >= 1 && parsed <= int.MaxValue)
        {
            subiektId = parsed;
            return true;
        }
        return false;
    }
}
