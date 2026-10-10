using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SubiektBridge.Api.Models;

/// <summary>
/// Mirror Laravel-side `App\Modules\Invoicing\Bridge\DTOs\InvoiceRequest`.
/// Snake_case JSON do zgodności z konwencjami Laravelowego klienta.
/// </summary>
public sealed record InvoiceRequestDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("issue_date")] string IssueDate,
    [property: JsonPropertyName("sale_date")] string SaleDate,
    [property: JsonPropertyName("payment")] PaymentDto Payment,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("contractor")] ContractorDto Contractor,
    [property: JsonPropertyName("lines")] IReadOnlyList<LineDto> Lines,
    [property: JsonPropertyName("shipping")] ShippingDto Shipping,
    [property: JsonPropertyName("totals")] InvoiceTotalsDto Totals,
    [property: JsonPropertyName("external_reference")] string ExternalReference,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("warehouse_subiekt_id")] int? WarehouseSubiektId = null
);

public sealed record InvoiceCorrectionRequestDto(
    [property: JsonPropertyName("issue_date")] string IssueDate,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("source_is_external")] bool SourceIsExternal,
    [property: JsonPropertyName("source_invoice_number")] string? SourceInvoiceNumber,
    [property: JsonPropertyName("source_invoice_date")] string? SourceInvoiceDate,
    [property: JsonPropertyName("lines")] IReadOnlyList<CorrectionLineDto> Lines,
    [property: JsonPropertyName("external_reference")] string ExternalReference,
    // Opcjonalna forma platnosci dla KFS. Bez tego Sfera ustawia domyslnie
    // PlatnoscGotowkaKwota = total korekty, co dla zwrotow Allegro (przelew/Allegro Pay)
    // jest myslace. Bridge zeruje PlatnoscGotowkaKwota i ustawia wlasciwy *Kwota
    // (PlatnoscPrzelewKwota / PlatnoscKartaKwota itp.) gdy payment != null.
    [property: JsonPropertyName("payment")] PaymentDto? Payment = null
);

public sealed record PaymentDto(
    [property: JsonPropertyName("attribute")] string Attribute,
    // Null gdy atrybut nie ma odpowiednika *Id w Sferze (PlatnoscGotowka, PlatnoscPrzelew).
    // Wtedy Bridge ustawia tylko {Attribute}Kwota.
    [property: JsonPropertyName("method_subiekt_id")] int? MethodSubiektId,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("is_settled")] bool IsSettled
);

public sealed record ContractorDto(
    [property: JsonPropertyName("is_person")] bool IsPerson,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("nip")] string? Nip,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("full_name")] string? FullName,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")] string? LastName,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("address")] AddressDto Address
);

/// <summary>
/// Walidacja Symbolu kontrahenta - czysta logika (testowalna cross-platform), wołana przez Real i Fake.
/// </summary>
public static class ContractorFields
{
    /// <summary>kh_Symbol = typ TSymbol = varchar(20) (zrzut schematu 1.88, Types/dbo.TSymbol.sql).</summary>
    public const int SymbolMaxLength = 20;

    /// <summary>
    /// NIP bez '-' i spacji. Subiekt trzyma adr_NIP tak, jak wpisał operator (starsze/ręczne kartoteki bywają
    /// z kreskami), więc KAŻDE porównanie po NIP (dopasowanie kontrahenta przy FS/PZ, filtr GET /invoices?nip=)
    /// normalizuje obie strony: tę wartość vs REPLACE(REPLACE(adr_NIP,'-',''),' ','') w SQL. Bez tego kartoteka
    /// z "111-111-11-11" nie pasuje do "1111111111" i most zakłada duplikat po Symbolu.
    /// </summary>
    public static string NormalizeNip(string nip) => nip.Replace("-", "").Replace(" ", "");

    /// <summary>Null gdy Symbol jest poprawny, inaczej komunikat dla klienta.</summary>
    public static string? ValidateSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return "contractor.symbol jest wymagany, gdy kontrahenta nie da się dopasować po NIP.";
        }
        if (symbol.Length > SymbolMaxLength)
        {
            return $"contractor.symbol '{symbol}' ma {symbol.Length} znaków - Subiekt przyjmuje maks. " +
                   $"{SymbolMaxLength} (kh_Symbol varchar({SymbolMaxLength})).";
        }
        return null;
    }
}

/// <summary>
/// Uwagi dokumentu (dok_Uwagi = TUwagi = varchar(500)) i doklejany do nich external_reference.
/// Czysta logika string - testowalna cross-platform, dzielona przez RealSferaSession i kontrolery.
/// </summary>
public static class UwagiFields
{
    /// <summary>dok_Uwagi = TUwagi = varchar(500) (zrzut schematu 1.88).</summary>
    public const int MaxLength = 500;

    /// <summary>Sufiks doklejany do notatek klienta (anti-duplicate szuka go w dok_Uwagi).</summary>
    public static string ReferenceSuffix(string externalReference) => $"ref: {externalReference}";

    /// <summary>
    /// Czy <paramref name="uwagi"/> zawiera <paramref name="reference"/> jako CAŁY token (nie podciąg):
    /// znak przed i po nie może być znakiem identyfikatora. Bez tego ref "order:12" pasował do
    /// "order:123" → fałszywe 409 z CUDZYM existing_subiekt_id. Case-insensitive - tak jak SQL LIKE
    /// pod polskim collation, którym Sfera wstępnie filtruje kolekcję.
    /// </summary>
    public static bool ContainsReferenceToken(string? uwagi, string? reference)
    {
        if (string.IsNullOrEmpty(uwagi) || string.IsNullOrEmpty(reference)) return false;
        int idx = 0;
        while ((idx = uwagi.IndexOf(reference, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = idx + reference.Length;
            bool startOk = idx == 0 || !IsReferenceChar(uwagi[idx - 1]);
            bool endOk = end == uwagi.Length || !IsReferenceChar(uwagi[end]);
            if (startOk && endOk) return true;
            idx++;
        }
        return false;

        static bool IsReferenceChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or ':' or '.' or '/';
    }

    /// <summary>
    /// Notatki + " | ref: X". Ref jest ZAWSZE zachowany w całości (to na nim stoi anti-duplicate);
    /// gdyby całość przekroczyła 500 znaków, obcinane są notatki (kontrolery i tak odrzucają to 422
    /// przez <see cref="ValidateNotes"/> - obcięcie to zabezpieczenie ścieżek bez walidacji).
    /// </summary>
    public static string Build(string? notes, string externalReference)
    {
        string baseNotes = notes ?? string.Empty;
        if (string.IsNullOrWhiteSpace(externalReference) || ContainsReferenceToken(baseNotes, externalReference))
        {
            return baseNotes.Length > MaxLength ? baseNotes[..MaxLength] : baseNotes;
        }

        string suffix = ReferenceSuffix(externalReference);
        if (baseNotes.Length == 0) return suffix.Length > MaxLength ? suffix[..MaxLength] : suffix;

        int room = MaxLength - suffix.Length - 3; // " | "
        if (room < 0) return suffix.Length > MaxLength ? suffix[..MaxLength] : suffix;
        if (baseNotes.Length > room) baseNotes = baseNotes[..room];
        return $"{baseNotes} | {suffix}";
    }

    /// <summary>Ile znaków notatek zmieści się obok doklejanego " | ref: X" (do details.max_length w 422).</summary>
    public static int MaxNotesLength(string externalReference)
        => MaxLength - (string.IsNullOrWhiteSpace(externalReference) ? 0 : ReferenceSuffix(externalReference).Length + 3);

    /// <summary>Null gdy notatki zmieszczą się razem z ref w 500 znakach, inaczej komunikat dla klienta (422).</summary>
    public static string? ValidateNotes(string? notes, string externalReference, string fieldName = "notes")
    {
        if (string.IsNullOrEmpty(notes)) return null;
        int suffixLen = MaxLength - MaxNotesLength(externalReference);
        int max = MaxLength - suffixLen;
        return notes.Length > max
            ? $"{fieldName} ma {notes.Length} znaków - Subiekt mieści {UwagiFields.MaxLength} w Uwagach, z czego {suffixLen} zajmuje 'ref: {externalReference}'. Maks. {max}."
            : null;
    }

    /// <summary>
    /// Maks. długość external_reference: musi zmieścić się W CAŁOŚCI w Uwagach razem z prefiksem "ref: " (500 - 5 = 495).
    /// Dłuższy ref był do v0.19.x obcinany po cichu przez <see cref="Build"/> (przy pustych notatkach ValidateNotes nie sprawdzał
    /// ref), a skan pełnym ref nigdy go nie znajdował = duplikat przy ponowieniu. Limit jest globalny; limit notatek OBOK ref
    /// liczy ValidateNotes (przy ref 495 żadne notatki/reason się nie mieszczą - KFS z "Korekta: " zawsze NOTES_TOO_LONG).
    /// </summary>
    public const int MaxExternalReferenceLength = MaxLength - 5; // "ref: "

    /// <summary>
    /// Walidacja external_reference PRZED ValidateNotes (kontrolery FS/KFS/PZ/MM → 422 INVALID_EXTERNAL_REFERENCE):
    /// pusty/biały ref = skan duplikatów bez sensu (anty-duplikat warstwy 2 stoi na nim), za długi = obcięty w Uwagach.
    /// </summary>
    public static string? ValidateExternalReference(string? externalReference)
    {
        if (string.IsNullOrWhiteSpace(externalReference))
        {
            return "external_reference jest wymagane (anty-duplikat w Subiekcie szuka dokumentu po tej wartosci).";
        }
        if (externalReference.Length > MaxExternalReferenceLength)
        {
            return $"external_reference ma {externalReference.Length} znakow - maks. {MaxExternalReferenceLength} " +
                   $"(musi zmiescic sie w {MaxLength} znakach Uwag Subiekta razem z 'ref: ').";
        }
        return null;
    }
}

public sealed record AddressDto(
    [property: JsonPropertyName("street")] string Street,
    [property: JsonPropertyName("post_code")] string PostCode,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("country_code")] string CountryCode
);

public sealed record LineDto(
    [property: JsonPropertyName("ean")] string? Ean,
    [property: JsonPropertyName("name_fallback")] string NameFallback,
    // decimal (od v0.17.0): towary na kg/m z ułamkami. Całkowite wartości JSON (np. 2) działają jak dotąd.
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("unit_price_gross")] decimal UnitPriceGross,
    [property: JsonPropertyName("vat_rate")] decimal VatRate,
    // Opcjonalna cena NETTO jednostkowa - znaczaca TYLKO dla PZ. Gdy podana, Bridge wpisuje
    // ja wprost jako CenaNettoPrzedRabatem (bez przeliczania brutto->netto), co eliminuje
    // groszowe rozjazdy zaokraglen przy cenach zakupu (PZ trzyma ob_CenaNetto, brutto wylicza).
    // Null = zachowanie domyslne: netto = unit_price_gross / (1 + vat_rate/100).
    // Dla FS/KFS pole jest ignorowane (te dokumenty licza od brutto).
    [property: JsonPropertyName("unit_price_net")] decimal? UnitPriceNet = null
);

/// <summary>
/// Pozycja korygująca - ten sam shape JSON co <see cref="LineDto"/>, ujemna `quantity`
/// dla zwrotu (pomniejszenie). Laravel używa wspólnego LineDto z tym samym kluczem
/// `quantity` dla FS/KFS/PZ - C# musi zachować tę zgodność, inaczej KFS ląduje
/// z pustą ilością i Subiekt księguje 0 sztuk korekty.
/// </summary>
public sealed record CorrectionLineDto(
    [property: JsonPropertyName("ean")] string? Ean,
    [property: JsonPropertyName("name_fallback")] string NameFallback,
    [property: JsonPropertyName("quantity")] decimal QuantityChange,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("unit_price_gross")] decimal UnitPriceGross,
    [property: JsonPropertyName("vat_rate")] decimal VatRate
);

public sealed record ShippingDto(
    [property: JsonPropertyName("include")] bool Include,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("unit_price_gross")] decimal UnitPriceGross,
    [property: JsonPropertyName("vat_rate")] decimal VatRate
);

public sealed record InvoiceTotalsDto(
    [property: JsonPropertyName("net")] decimal? Net = null,
    [property: JsonPropertyName("vat")] decimal? Vat = null,
    [property: JsonPropertyName("gross")] decimal Gross = 0m
);

// ----------------------------- Response -----------------------------

public sealed record InvoiceResponseDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("subiekt_id")] long SubiektId,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("contractor_subiekt_id")] long ContractorSubiektId,
    [property: JsonPropertyName("totals")] InvoiceTotalsDto Totals,
    [property: JsonPropertyName("pdf_url")] string? PdfUrl,
    [property: JsonPropertyName("pdf_base64")] string? PdfBase64
);

// ----------------------------- Receipt (PZ) -----------------------------

/// <summary>
/// PZ - Przyjęcie Zewnętrzne. Dropshipping flow: dodaje towar na magazyn z ceną zakupu
/// po zakupie u dostawcy. Jedno PZ per dostawca (unique supplier per zamówienie).
/// </summary>
public sealed record ReceiptIssueRequestDto(
    [property: JsonPropertyName("issue_date")] string IssueDate,
    [property: JsonPropertyName("warehouse_subiekt_id")] int? WarehouseSubiektId,
    [property: JsonPropertyName("supplier")] ContractorDto Supplier,
    [property: JsonPropertyName("lines")] IReadOnlyList<LineDto> Lines,
    // source_invoice_subiekt_id usunięte (audyt 2026-06-10 pkt 7) - dead code: FS wymaga
    // stanu, więc PZ zawsze idzie pierwsze i pole było zawsze null. Klient może je jeszcze
    // wysyłać - System.Text.Json ignoruje nieznane pola.
    [property: JsonPropertyName("external_reference")] string ExternalReference,
    [property: JsonPropertyName("notes")] string Notes,
    // Numer oryginalny - mapuje na SuDokument.NumerOryginalny (max 30 znakow w Sferze,
    // Bridge tnie wejscie). Dla PZ z Allegro wpisujemy tutaj login kupujacego, dla
    // ktorego wystawiamy dropshipping.
    [property: JsonPropertyName("original_number")] string? OriginalNumber = null
);

// ----------------------------- Transfer (MM) -----------------------------

/// <summary>
/// MM - Przesunięcie Międzymagazynowe. Przenosi stan towaru między magazynami
/// (MagazynNadawczyId -> MagazynOdbiorczyId). Dokument WEWNĘTRZNY magazynowy - NIE idzie
/// do KSeF (magazyn nie jest polem schematu e-faktury). Pozycje tylko towarowe (po EAN).
/// </summary>
public sealed record TransferRequestDto(
    [property: JsonPropertyName("source_warehouse_id")] int SourceWarehouseId,
    [property: JsonPropertyName("dest_warehouse_id")] int DestWarehouseId,
    [property: JsonPropertyName("lines")] IReadOnlyList<TransferLineDto> Lines,
    [property: JsonPropertyName("external_reference")] string ExternalReference,
    [property: JsonPropertyName("notes")] string? Notes = null
);

public sealed record TransferLineDto(
    [property: JsonPropertyName("ean")] string Ean,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("unit")] string Unit = "szt."
);

public sealed record TransferResponseDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("subiekt_id")] long SubiektId,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("source_warehouse_id")] int SourceWarehouseId,
    [property: JsonPropertyName("dest_warehouse_id")] int DestWarehouseId
);

// ----------------------------- Query (GET /invoices) -----------------------------

/// <summary>
/// Filtr zapytania o istniejące FV w Subiekcie. Bridge wykonuje przez Sferę
/// SuDokumentyManager.OtworzKolekcje(filtr, sort) - filtr to SQL WHERE clause
/// budowany z białej listy pól (klient nie podaje raw SQL).
/// </summary>
public sealed record InvoiceQueryRequestDto(
    [property: JsonPropertyName("from")] string? From,                    // YYYY-MM-DD
    [property: JsonPropertyName("to")] string? To,                        // YYYY-MM-DD
    [property: JsonPropertyName("type")] string? Type,                    // FS / KFS / null=oba
    [property: JsonPropertyName("notes_contains")] string? NotesContains, // LIKE %X% w dok_Uwagi
    [property: JsonPropertyName("nip")] string? Nip,                      // NIP kontrahenta
    [property: JsonPropertyName("limit")] int Limit = 200                 // hard cap 1000
);

/// <summary>
/// Czysta logika filtra GET /invoices?nip= - wydzielona z windows-only RealSferaSession, by była
/// testowalna cross-platform. NIP nie jest kolumną dok__Dokument: RealSferaSession zamienia go na kh_Id
/// (SQL po adr__Ewid) i zawęża OtworzKolekcje klauzulą z <see cref="ContractorClause"/>.
/// </summary>
public static class InvoiceQueryFields
{
    /// <summary>
    /// Klauzula WHERE dla kontrahenta (nabywcy) dokumentu sprzedaży: dok_PlatnikId - tę kolumnę bierze
    /// InsERT we własnym widoku sprzedaży (vwZestDef_DokSprzedazy). NIE dok_OdbiorcaId: na MM to id
    /// magazynu, więc kh_Id mógłby liczbowo trafić w magazyn. Id to long - bez escapowania.
    /// Pusta kolekcja -> null (wołający nie powinien wtedy pytać Sfery).
    /// </summary>
    public static string? ContractorClause(IReadOnlyCollection<long> contractorIds)
    {
        if (contractorIds.Count == 0) return null;
        return $"dok_PlatnikId IN ({string.Join(",", contractorIds)})";
    }
}

/// <summary>Pojedynczy wpis listy FV - wystarczające metadata do dopasowania do Order.</summary>
public sealed record InvoiceQueryItemDto(
    [property: JsonPropertyName("subiekt_id")] long SubiektId,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("issue_date")] string? IssueDate,
    [property: JsonPropertyName("contractor_id")] long? ContractorId,
    [property: JsonPropertyName("contractor_nip")] string? ContractorNip,
    [property: JsonPropertyName("contractor_name")] string? ContractorName,
    [property: JsonPropertyName("net_amount")] decimal? NetAmount,
    [property: JsonPropertyName("vat_amount")] decimal? VatAmount,
    [property: JsonPropertyName("gross_amount")] decimal? GrossAmount,
    [property: JsonPropertyName("notes")] string? Notes
);

// ----------------------------- Reference -----------------------------

public sealed record ProductDto(
    [property: JsonPropertyName("subiekt_id")] long SubiektId,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("ean")] string Ean,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("vat_rate")] decimal VatRate,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("is_active")] bool IsActive
);

public sealed record WarehouseDto(
    [property: JsonPropertyName("subiekt_id")] int SubiektId,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_main")] bool IsMain
);

// ----------------------------- Error -----------------------------

public sealed record ErrorResponseDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] object? Details = null,
    [property: JsonPropertyName("retry_after_seconds")] int? RetryAfterSeconds = null
);

// ----------------------------- Health -----------------------------

public sealed record HealthResponseDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("bridge_version")] string BridgeVersion,
    [property: JsonPropertyName("subiekt_version")] string SubiektVersion,
    [property: JsonPropertyName("sfera_session")] string SferaSession,
    [property: JsonPropertyName("last_invoice_at")] DateTimeOffset? LastInvoiceAt,
    [property: JsonPropertyName("queue_depth")] int QueueDepth,
    [property: JsonPropertyName("last_error")] string? LastError = null,
    // "ok" / "down" / "unknown" - własne połączenie SqlClient (raw SQL), niezależne od sesji Sfery.
    [property: JsonPropertyName("sql_connection")] string SqlConnection = "unknown",
    [property: JsonPropertyName("sql_error")] string? SqlError = null
);

// ----------------------------- Raw -----------------------------

public sealed record SferaRawRequestDto(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("args")] IReadOnlyList<object?> Args
);
