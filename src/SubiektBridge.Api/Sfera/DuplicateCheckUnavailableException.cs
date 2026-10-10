namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Rzucany, gdy skan duplikatów po external_reference (warstwa 2 idempotencji) nie mógł orzec, czy dokument już istnieje:
/// read-only SQL po dok__Dokument padł, przekroczono czas skanu albo potwierdzenie COM (WczytajDokument) dokumentu o stanie
/// „istnieje” padło przy żywej sesji. Do v0.19.x skan był fail-open (błąd = „brak duplikatu” = dokument wystawiany) i przy
/// ponowieniu po timeoucie klienta mógł powstać duplikat. Teraz dokument NIE powstaje (skan jest przed pierwszym zapisem).
///
/// Kontroler mapuje na 503 DUPLICATE_CHECK_UNAVAILABLE + details {external_reference, document_type} — kod z listy retry
/// klienta („nic nie zapisano”), ponowienie tym samym Idempotency-Key (503 nie trafia do cache). Martwa sesja Sfery
/// (sonda po wyjątku COM) idzie osobno jako SferaUnavailableException → 503 SUBIEKT_UNAVAILABLE.
/// </summary>
public sealed class DuplicateCheckUnavailableException : Exception
{
    public string ExternalReference { get; }

    /// <summary>"FS" | "KFS" | "PZ" | "MM" (DokTyp.Name).</summary>
    public string DocumentType { get; }

    public DuplicateCheckUnavailableException(string externalReference, string documentType, Exception? inner, string? reason = null)
        : base(BuildMessage(externalReference, documentType, reason), inner)
    {
        ExternalReference = externalReference;
        DocumentType = documentType;
    }

    private static string BuildMessage(string externalReference, string documentType, string? reason)
    {
        string shortRef = externalReference.Length > 80 ? externalReference[..77] + "..." : externalReference;
        string why = string.IsNullOrEmpty(reason) ? "" : $" ({reason})";
        return $"Nie udalo sie sprawdzic w Subiekcie, czy dokument {documentType} z external_reference '{shortRef}' juz istnieje{why}" +
               " - w tym zadaniu dokument NIE zostal wystawiony, ponow pozniej tym samym Idempotency-Key.";
    }
}
