using SubiektBridge.Api.Models;

namespace SubiektBridge.Api.Sfera;

/// <summary>Kwoty nagłówka dokumentu odczytane z atrybutów SuDokument; null = atrybut nieczytelny (nigdy podstawione 0).</summary>
public sealed record DocumentAmounts(decimal? Net, decimal? Vat, decimal? Gross);

/// <summary>
/// Odczyt kwot nagłówka PZ — wspólna ścieżka GET /receipts/{id} i POST /receipts, bez COM (delegat), testowalna cross-platform.
/// Real podaje <c>name => TryReadDecimal(dok, name)</c>, Fake — model kwot Sfery.
/// </summary>
public static class DocumentTotals
{
    /// <summary>
    /// WartoscNetto / WartoscVat / WartoscBrutto (CHM SuDokument_*, tylko do odczytu, od 1.05); brutto awaryjnie z KwotaDoZaplaty
    /// (suma brutto tabelki VAT). "WartoscVat", NIE "WartoscPodatku" — tego atrybutu nie ma w Sferze.
    /// </summary>
    public static DocumentAmounts Read(Func<string, decimal?> tryRead)
        => new(tryRead("WartoscNetto"), tryRead("WartoscVat"), tryRead("WartoscBrutto") ?? tryRead("KwotaDoZaplaty"));

    /// <summary>
    /// Totals odpowiedzi POST /receipts: netto i VAT z zapisanego PZ (od v0.21.1; wcześniej zawsze null), nieczytelne = null.
    /// Brutto z linii tylko gdy dokument go nie podał: linia z unit_price_net → netto * (1 + VAT), inaczej unit_price_gross
    /// (bez tego klient wysyłający net + gross=0 dostałby/zacache'ował totals.gross = 0).
    /// </summary>
    public static InvoiceTotalsDto ForIssuedReceipt(DocumentAmounts read, IEnumerable<LineDto> lines)
        => new(Net: read.Net, Vat: read.Vat, Gross: read.Gross ?? LinesGross(lines));

    private static decimal LinesGross(IEnumerable<LineDto> lines) => lines.Sum(l =>
        (l.UnitPriceNet.HasValue ? l.UnitPriceNet.Value * (1m + l.VatRate / 100m) : l.UnitPriceGross) * l.Quantity);
}
