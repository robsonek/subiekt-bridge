namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Rzucane gdy w Subiekcie istnieje juz dokument (FS/PZ) z tym samym external_reference
/// w Uwagach. Bridge sprawdza to przed DodajFS/DodajPZ zeby wykluczyc duplikat
/// nawet gdy klient wyśle inny Idempotency-Key (np. debug curl).
/// </summary>
public sealed class DuplicateInvoiceException : Exception
{
    public long ExistingSubiektId { get; }
    public string ExistingNumber { get; }
    public string ExternalReference { get; }

    public DuplicateInvoiceException(long existingSubiektId, string existingNumber, string externalReference)
        : base($"Dokument z external_reference '{existingReference(externalReference)}' juz istnieje w Subiekcie: {existingNumber} (subiekt_id={existingSubiektId}).")
    {
        ExistingSubiektId = existingSubiektId;
        ExistingNumber = existingNumber;
        ExternalReference = externalReference;
    }

    private static string existingReference(string s) => s.Length > 80 ? s[..77] + "..." : s;
}
