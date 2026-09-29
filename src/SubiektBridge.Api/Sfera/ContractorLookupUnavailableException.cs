namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Rzucany gdy dopasowanie kontrahenta po NIP (raw SQL po adr__Ewid) padło - baza/SqlClient niedostępne.
/// Wcześniej błąd był połykany i most zakładał kontrahenta po Symbolu: przy kartotece założonej ręcznie
/// z innym symbolem powstawał duplikat. Teraz dokument NIE powstaje (Zapisz() jeszcze nie wołane).
///
/// Controller mapuje na 503 z code='CONTRACTOR_LOOKUP_UNAVAILABLE' - klient ponawia z backoffem
/// (stan bazy zwykle wraca; /health pokazuje wtedy sql_connection="down").
/// </summary>
public sealed class ContractorLookupUnavailableException : Exception
{
    public string Nip { get; }

    public ContractorLookupUnavailableException(string nip, Exception inner)
        : base($"Nie udało się sprawdzić kontrahenta po NIP {nip} (baza niedostępna) - dokument nie został wystawiony, ponów później.", inner)
    {
        Nip = nip;
    }
}
