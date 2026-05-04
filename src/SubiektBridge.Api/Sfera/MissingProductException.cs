namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Rzucany gdy <see cref="RealSferaSession"/> dostaje pozycję z EAN-em, którego nie ma
/// w bazie towarów Subiekta. Cisze fallowanie do "usługi jednorazowej" maskowałoby
/// prawdziwy problem (towar nie został zsynchronizowany z hurtowni do Subiekta), w efekcie
/// FS/PZ wystawiałby się jako usługa - magazyn by się NIE ruszył dla tej pozycji.
///
/// Controller mapuje na 422 z code='MISSING_PRODUCT', co Laravel-side łapie jako
/// <c>SubiektProductMissingException</c> i pokazuje operatorowi listę brakujących EAN-ów.
/// </summary>
public sealed class MissingProductException : Exception
{
    public string MissingEan { get; }

    public MissingProductException(string ean)
        : base($"Towar o EAN '{ean}' nie istnieje w Subiekcie. Dodaj towar w Subiekcie zanim wystawisz dokument.")
    {
        MissingEan = ean;
    }
}
