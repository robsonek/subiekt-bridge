using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubiektBridge.Api.Models;

namespace SubiektBridge.Api.Sfera;

// Edycja istniejącego PZ (v0.21.0): logika pure, testowalna bez COM. Spec: docs/specs/2026-10-11-receipts-edit-pz-0.21-design.md §5.1.
// RealSferaSession i FakeSferaSession wołają TE SAME funkcje (normalizacja, revision, komparator, klasyfikacja, decyzja) -
// Fake koduje maszynę decyzji, nie własną interpretację.

public enum ReceiptEditError { NotFound, NotAReceipt, Cancelled, RevisionMismatch, Locked, Validation, UpdateFailed, UpdateUnverified, ReadFailed }
public enum ReceiptLockReason { LockedBySession, ConcurrentModification, PermissionDenied, PeriodLocked, Booked, NotEditable, NotSaved }
public enum ReceiptSavePhase { BeforeFirstSet, Modify, Save, ReadBack }
public enum ReceiptStateMatch { Intended, Unchanged, Diverged }
public enum ReceiptItemKind { Product, Service, OneOffService, Package, Bundle, Suggestion, Unknown }
public enum ReceiptFailureKind { Lock, Validation, NotFound, Unclassified }

/// <summary>
/// Błąd domenowy edycji PZ. <see cref="Reason"/> determinuje mapowanie HTTP (ReceiptEditErrorMapper w kontrolerze).
/// Kontekst dokumentu (SferaCanEdit, LinkedDocuments, CurrentRevision) dopisuje Real/Fake po decyzji.
/// </summary>
public sealed class ReceiptEditException : Exception
{
    public ReceiptEditError Reason { get; }
    /// <summary>Kod 422 dla Reason == Validation (EMPTY_UPDATE, INVALID_ORIGINAL_NUMBER, ORIGINAL_NUMBER_TOO_LONG).</summary>
    public string? Code { get; }
    public object? Details { get; }
    public ReceiptLockReason? LockReason { get; }
    public int? Hresult { get; }
    public string? SferaMessage { get; }
    public ReceiptSavePhase? Phase { get; }
    /// <summary>502: czy zapis prawdopodobnie wszedł (true = Zapisz wróciło bez wyjątku; null = nie wiadomo).</summary>
    public bool? SavedProbably { get; }
    public bool? SferaCanEdit { get; set; }
    public IReadOnlyList<LinkedDocumentDto>? LinkedDocuments { get; set; }
    public string? CurrentRevision { get; set; }

    public ReceiptEditException(ReceiptEditError reason, string message, string? code = null, object? details = null,
        ReceiptLockReason? lockReason = null, int? hresult = null, string? sferaMessage = null, ReceiptSavePhase? phase = null,
        bool? savedProbably = null, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason; Code = code; Details = details; LockReason = lockReason; Hresult = hresult; SferaMessage = sferaMessage;
        Phase = phase; SavedProbably = savedProbably;
    }

    public static string? HresultText(int? hresult) => hresult is int h ? $"0x{unchecked((uint)h):X8}" : null;
}

/// <summary>Pozycja dokumentu do komparatora/revision. Pola = wszystkie chronione (spec §5.1); kwoty i ilości normalizowane do 4 dp (MSSQL money).</summary>
public sealed record ReceiptItemSnapshot(long PositionId, int Lp, long? ProductId, ReceiptItemKind Kind, decimal Quantity,
    decimal QuantityInStockUnits, string Unit, decimal UnitPriceNet, decimal DiscountPercent, decimal VatRate)
{
    public ReceiptItemSnapshot Normalized() => this with
    {
        Quantity = ReceiptNormalization.R4(Quantity),
        QuantityInStockUnits = ReceiptNormalization.R4(QuantityInStockUnits),
        Unit = (Unit ?? string.Empty).Trim(),
        UnitPriceNet = ReceiptNormalization.R4(UnitPriceNet),
        DiscountPercent = ReceiptNormalization.R4(DiscountPercent),
        VatRate = ReceiptNormalization.R4(VatRate),
    };
}

public sealed record ReceiptStateSnapshot(string OriginalNumber, int Status, int? BookingStatus, IReadOnlyList<ReceiptItemSnapshot> Items)
{
    public ReceiptStateSnapshot Normalized() => this with
    {
        OriginalNumber = OriginalNumber ?? string.Empty,
        Items = Items.Select(i => i.Normalized()).OrderBy(i => i.PositionId).ToList(),
    };
}

public sealed record StockRow(long PozId, decimal Ilosc, decimal Pozostalo, int Count);
public sealed record StockFact(decimal IssuedStockUnits, decimal? IssuedQuantity);
public sealed record ReceiptFailureClass(ReceiptFailureKind Kind, ReceiptLockReason? LockReason, string? ValidationCode, int? Hresult, string? Message);

internal static class ReceiptNormalization
{
    /// <summary>Jedna normalizacja dla walidacji, revision i komparatora: 4 miejsca po przecinku (money), bez pasma tolerancji.</summary>
    public static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
}

public static class ReceiptEditFields
{
    /// <summary>SuDokument.NumerOryginalny: 30 znaków, NULL niedopuszczalny (CHM SuDokument_NumerOryginalny.htm).</summary>
    public const int OriginalNumberMaxLength = 30;

    public static string NormalizeOriginalNumber(string value) => value.Trim();

    /// <summary>Tabela §4.2: Undefined → EMPTY_UPDATE; Null → INVALID(null); nie-string → INVALID(type); pusty → INVALID(empty); &gt;30 → TOO_LONG.</summary>
    public static ReceiptEditException? ValidateOriginalNumber(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
                return new ReceiptEditException(ReceiptEditError.Validation,
                    "Body PATCH nie zawiera zadnego obslugiwanego pola (oczekiwane: original_number).", code: "EMPTY_UPDATE");
            case JsonValueKind.Null:
                return Invalid("null", "original_number nie moze byc null (Sfera: NULL niedopuszczalny dla NumerOryginalny).");
            case JsonValueKind.String:
                break;
            default:
                return Invalid("type", $"original_number musi byc stringiem (otrzymano JSON {value.ValueKind}).");
        }

        string normalized = NormalizeOriginalNumber(value.GetString() ?? string.Empty);
        if (normalized.Length == 0)
        {
            return Invalid("empty", "original_number nie moze byc pusty (czyszczenie pola nie jest obslugiwane).");
        }
        if (normalized.Length > OriginalNumberMaxLength)
        {
            return new ReceiptEditException(ReceiptEditError.Validation,
                $"original_number ma {normalized.Length} znakow - Subiekt przyjmuje maks. {OriginalNumberMaxLength} (SuDokument.NumerOryginalny).",
                code: "ORIGINAL_NUMBER_TOO_LONG", details: new { max_length = OriginalNumberMaxLength, length = normalized.Length });
        }
        return null;

        static ReceiptEditException Invalid(string reason, string message)
            => new(ReceiptEditError.Validation, message, code: "INVALID_ORIGINAL_NUMBER", details: new { reason });
    }

    /// <summary>
    /// Token external_reference z Uwag - TYLKO gdy Uwagi zawierają marker "ref:" (UwagiFields.ReferenceSuffix); pierwszy marker,
    /// token do białego znaku. Bez markera → null (nie zgadujemy z notatek).
    /// </summary>
    public static string? ExtractExternalReference(string? uwagi)
    {
        if (string.IsNullOrEmpty(uwagi)) return null;
        int idx = uwagi.IndexOf("ref:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int i = idx + 4;
        while (i < uwagi.Length && char.IsWhiteSpace(uwagi[i])) i++;
        int start = i;
        while (i < uwagi.Length && !char.IsWhiteSpace(uwagi[i])) i++;
        return i > start ? uwagi[start..i] : null;
    }
}

public static class ReceiptRevision
{
    /// <summary>"r1:" + 16 hex SHA-256 kanonicznej reprezentacji snapshotu (bez BookingStatus - zmienia go księgowość niezależnie).</summary>
    public static string Compute(ReceiptStateSnapshot snapshot)
    {
        var n = snapshot.Normalized();
        var sb = new StringBuilder();
        sb.Append("on=").Append(n.OriginalNumber).Append('\u001f');
        sb.Append("st=").Append(n.Status.ToString(CultureInfo.InvariantCulture)).Append('\u001f');
        foreach (var i in n.Items)
        {
            sb.Append(i.PositionId.ToString(CultureInfo.InvariantCulture)).Append(';')
              .Append(i.ProductId?.ToString(CultureInfo.InvariantCulture) ?? "-").Append(';')
              .Append(i.Kind).Append(';')
              .Append(i.Quantity.ToString("F4", CultureInfo.InvariantCulture)).Append(';')
              .Append(i.QuantityInStockUnits.ToString("F4", CultureInfo.InvariantCulture)).Append(';')
              .Append(i.Unit).Append(';')
              .Append(i.UnitPriceNet.ToString("F4", CultureInfo.InvariantCulture)).Append(';')
              .Append(i.DiscountPercent.ToString("F4", CultureInfo.InvariantCulture)).Append(';')
              .Append(i.VatRate.ToString("F4", CultureInfo.InvariantCulture)).Append('\u001e');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return "r1:" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

public static class ReceiptStockFacts
{
    /// <summary>
    /// Fakty rozchodu z surowych agregatów dok_MagRuch (spec §4.1 „Jednostki rozchodu"): status 1 → wiersze obowiązkowe, Σilosc == Ilosc,
    /// 0 ≤ pozostalo ≤ ilosc; status 0/3 → brak wierszy = 0, wiersze = niespójność; status 2 i inne → null bez niespójności;
    /// kind ≠ Product → pozycja pominięta. issued_quantity (j. dokumentu) = issued_stock × IloscJm / Ilosc (null, gdy Ilosc = 0).
    /// </summary>
    public static (IReadOnlyDictionary<long, StockFact?> Facts, bool AnyInconsistent) FromRows(IEnumerable<StockRow> rows, ReceiptStateSnapshot current)
    {
        var agg = rows.GroupBy(r => r.PozId).ToDictionary(g => g.Key, g => (Ilosc: g.Sum(r => r.Ilosc), Pozostalo: g.Sum(r => r.Pozostalo)));
        var facts = new Dictionary<long, StockFact?>();
        bool inconsistent = false;

        foreach (var item in current.Items)
        {
            if (item.Kind != ReceiptItemKind.Product) continue;
            bool has = agg.TryGetValue(item.PositionId, out var a);
            switch (current.Status)
            {
                case 1:
                    if (!has || ReceiptNormalization.R4(a.Ilosc) != ReceiptNormalization.R4(item.QuantityInStockUnits) || a.Pozostalo < 0 || a.Pozostalo > a.Ilosc)
                    {
                        facts[item.PositionId] = null; inconsistent = true;
                    }
                    else
                    {
                        facts[item.PositionId] = Fact(a.Ilosc - a.Pozostalo, item);
                    }
                    break;
                case 0:
                case 3:
                    if (has) { facts[item.PositionId] = null; inconsistent = true; }
                    else facts[item.PositionId] = Fact(0m, item);
                    break;
                default:
                    facts[item.PositionId] = null; // 2 = unieważniony (dok_MagRuch nieczytany), 5-8 = zamówienia
                    break;
            }
        }
        return (facts, inconsistent);

        static StockFact Fact(decimal issuedStock, ReceiptItemSnapshot item)
        {
            decimal stock = ReceiptNormalization.R4(issuedStock);
            decimal? qty = item.QuantityInStockUnits > 0
                ? ReceiptNormalization.R4(issuedStock * item.Quantity / item.QuantityInStockUnits)
                : null;
            return new StockFact(stock, qty);
        }
    }
}

public static class ReceiptStateComparer
{
    /// <summary>
    /// Tożsamość pozycji = ob_Id; równość DOKŁADNA po normalizacji (bez pasma tolerancji) na wszystkich chronionych polach.
    /// Intended: nagłówek = docelowy, Status = sprzed, pozycje = docelowe (dla PATCH = sprzed). Unchanged: wszystko = sprzed.
    /// BookingStatus nie jest porównywany (zmienia go księgowość). Inaczej Diverged.
    /// </summary>
    public static ReceiptStateMatch Compare(ReceiptStateSnapshot before, ReceiptStateSnapshot intended, ReceiptStateSnapshot after)
    {
        var b = before.Normalized(); var i = intended.Normalized(); var a = after.Normalized();
        if (string.Equals(a.OriginalNumber, i.OriginalNumber, StringComparison.Ordinal) && a.Status == i.Status && ItemsEqual(a.Items, i.Items))
            return ReceiptStateMatch.Intended;
        if (string.Equals(a.OriginalNumber, b.OriginalNumber, StringComparison.Ordinal) && a.Status == b.Status && ItemsEqual(a.Items, b.Items))
            return ReceiptStateMatch.Unchanged;
        return ReceiptStateMatch.Diverged;
    }

    private static bool ItemsEqual(IReadOnlyList<ReceiptItemSnapshot> x, IReadOnlyList<ReceiptItemSnapshot> y)
    {
        if (x.Count != y.Count) return false;
        var byId = y.ToDictionary(i => i.PositionId);
        foreach (var xi in x)
        {
            if (!byId.TryGetValue(xi.PositionId, out var yi)) return false;
            if (xi.ProductId != yi.ProductId || xi.Kind != yi.Kind || xi.Quantity != yi.Quantity || xi.QuantityInStockUnits != yi.QuantityInStockUnits
                || !string.Equals(xi.Unit, yi.Unit, StringComparison.Ordinal) || xi.UnitPriceNet != yi.UnitPriceNet
                || xi.DiscountPercent != yi.DiscountPercent || xi.VatRate != yi.VatRate)
                return false;
        }
        return true;
    }
}

public static class ReceiptEditability
{
    /// <summary>Fakty → lista powodów (informacja dla klienta, NIE decyzja mostu). Kolejność stała.</summary>
    public static IReadOnlyList<string> Reasons(int dokTyp, int status, bool? periodLocked, int? bookingStatus, long? linkedDocumentId, bool? sferaCanEdit)
    {
        var reasons = new List<string>();
        if (status == DuplicateScan.StatusAnulowany) reasons.Add("CANCELLED");
        if (dokTyp != DokTyp.PZ) reasons.Add("NOT_A_RECEIPT");
        if (periodLocked == true) reasons.Add("PERIOD_LOCKED");
        if (bookingStatus is 3 or 7) reasons.Add("BOOKED");
        if (bookingStatus is 1 or 2 or 6) reasons.Add("EXPORTED_TO_ACCOUNTING");
        if (linkedDocumentId is > 0) reasons.Add("LINKED_DOCUMENT");
        if (sferaCanEdit == false) reasons.Add("SFERA_NOT_EDITABLE");
        return reasons;
    }
}

/// <summary>
/// Klasyfikacja wyjątków Sfery przy edycji (lista błędów CHM start_Lista_bledow.htm). HRESULT ma pierwszeństwo nad WSZYSTKIMI frazami;
/// frazy tylko, gdy HRESULT nie jest znany żadnej tabeli (blokad, walidacji 0x80040F8A, braku 0x80040F62).
/// </summary>
public static class ReceiptSaveFailureClassifier
{
    private const uint HrCannotLock = 0x80040F19;          // INS_E_CANNOTLOCKOBJECT (lock przy pierwszym secie atrybutu)
    private const uint HrConcurrency = 0x80040F1A;         // INS_E_CONCURRENCYVIOLATION "Dane zostały zmienione w tle"
    private const uint HrPermission = 0x80040F1D;          // INS_E_PERMISSIONDENIED
    private const uint HrPeriodLocked = 0x80040F64;        // INS_E_SU_OKRES_ZABLOKOWANY
    private const uint HrDateLocked = 0x80040F99;          // INS_E_DATA_W_OKRESIE_ZABLOKOWANYM
    private const uint HrCostCorrLocked = 0x800414E7;      // INS_E_SU_OBJETYKORKOSZT_ZABLOKOWANY
    private const uint HrBooked = 0x80040FC3;              // INS_E_EDYCJA_ZAKSIEGOWANEGO
    private const uint HrMissingOrig = 0x80040F8A;         // INS_E_SU_DOK_BRAK_NR_ORYG
    private const uint HrDeleted = 0x80040F62;             // INS_E_SU_DOK_NIE_ISTNIEJE

    // Granica słowa po lewej: "niezaksięgowany" NIE pasuje (poprzedza litera), "Dokument zaksięgowany" pasuje.
    private static readonly Regex BookedPhrase = new(@"(?<!\p{L})zaksięgowan", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex;
    }

    public static (int? Hresult, string? Message) Describe(Exception ex)
    {
        var u = Unwrap(ex);
        return u is COMException com ? (com.ErrorCode, com.Message) : (null, u.Message);
    }

    private static ReceiptLockReason? LockByHresult(int hresult) => unchecked((uint)hresult) switch
    {
        HrCannotLock => ReceiptLockReason.LockedBySession,
        HrConcurrency => ReceiptLockReason.ConcurrentModification,
        HrPermission => ReceiptLockReason.PermissionDenied,
        HrPeriodLocked or HrDateLocked or HrCostCorrLocked => ReceiptLockReason.PeriodLocked,
        HrBooked => ReceiptLockReason.Booked,
        _ => null,
    };

    private static bool KnownToOtherTable(int hresult) => unchecked((uint)hresult) is HrMissingOrig or HrDeleted;

    private static ReceiptLockReason? LockByPhrase(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        string m = Regex.Replace(message, @"\s+", " ").Trim().ToLowerInvariant();
        if (m.Contains("zablokowany do edycji") || m.Contains("nie można edytować") || m.Contains("edycja jest ograniczona")) return ReceiptLockReason.NotEditable;
        if (m.Contains("dane zostały zmienione w tle")) return ReceiptLockReason.ConcurrentModification;
        if (m.Contains("okres jest zablokowany") || m.Contains("sprzed daty blokady")) return ReceiptLockReason.PeriodLocked;
        if (m.Contains("dostęp zabroniony")) return ReceiptLockReason.PermissionDenied;
        if (BookedPhrase.IsMatch(m)) return ReceiptLockReason.Booked;
        return null;
    }

    /// <summary>Powód blokady: HRESULT z tabeli → powód; HRESULT znany innej tabeli (F8A/F62) → null; nieznany/null → frazy.</summary>
    public static ReceiptLockReason? ClassifyLock(int? hresult, string? message)
    {
        if (hresult is int h)
        {
            if (LockByHresult(h) is { } r) return r;
            if (KnownToOtherTable(h)) return null;
        }
        return LockByPhrase(message);
    }

    public static string? ClassifyValidation(int? hresult, string? message)
        => hresult is int h && unchecked((uint)h) == HrMissingOrig ? "INVALID_ORIGINAL_NUMBER" : null;

    public static bool IsNotFound(int? hresult) => hresult is int h && unchecked((uint)h) == HrDeleted;

    /// <summary>K1 HRESULT blokady → K2 F8A → K3 F62 → K4 frazy (tylko HRESULT nieznany) → K5 nieznany.</summary>
    public static ReceiptFailureClass Classify(Exception ex)
    {
        var (hr, msg) = Describe(ex);
        if (hr is int h)
        {
            if (LockByHresult(h) is { } lockReason) return new(ReceiptFailureKind.Lock, lockReason, null, hr, msg);
            if (ClassifyValidation(h, msg) is { } code) return new(ReceiptFailureKind.Validation, null, code, hr, msg);
            if (IsNotFound(h)) return new(ReceiptFailureKind.NotFound, null, null, hr, msg);
        }
        if (LockByPhrase(msg) is { } byPhrase) return new(ReceiptFailureKind.Lock, byPhrase, null, hr, msg);
        return new(ReceiptFailureKind.Unclassified, null, null, hr, msg);
    }
}

/// <summary>
/// Jedna funkcja decyzji dla Real i Fake (spec §5.1 R1–R8). Zwraca null = 200 (stan docelowy potwierdzony odczytem kontrolnym),
/// inaczej wyjątek z powodem. Pierwszeństwo: R1 (fazy przed Zapisz) → R2 reset sesji → R3 zamknięcie niepotwierdzone →
/// R4 dokument pozytywnie nie istnieje → R5 brak odczytu kontrolnego → R6 Intended → R7 Diverged → R8 Unchanged.
/// </summary>
public static class ReceiptEditOutcome
{
    public static ReceiptEditException? Decide(ReceiptSavePhase phase, Exception? ex, ReceiptStateMatch? match, bool closeConfirmed, bool sessionReset, bool? existsAfter)
    {
        if (existsAfter == false && match is not null)
            throw new ArgumentException("existsAfter=false wyklucza odczyt kontrolny (match musi byc null).", nameof(match));

        if (phase is ReceiptSavePhase.BeforeFirstSet or ReceiptSavePhase.Modify)
        {
            if (ex is null) throw new ArgumentException("Faza przed Zapisz wymaga wyjatku - bez wyjatku nie ma decyzji do podjecia.", nameof(ex));
            return FromClassification(ReceiptSaveFailureClassifier.Classify(ex), phase, existsAfter, nothingSavedCertain: true);
        }

        bool savedProbably = ex is null;
        if (sessionReset)
            return Unverified($"Sesja Sfery zostala zresetowana po Zapisz - stan PZ nieznany, sprawdz GET.", phase, savedProbably, ex);
        if (!closeConfirmed)
            return Unverified("Zamknij() dokumentu nie zostalo potwierdzone po Zapisz - stan PZ nieznany, sprawdz GET.", phase, savedProbably, ex);
        if (existsAfter == false)
            return new ReceiptEditException(ReceiptEditError.NotFound, "PZ nie istnieje juz w Subiekcie (usuniety w trakcie edycji).", phase: phase, inner: ex);
        if (match is null)
            return Unverified("Odczyt kontrolny po Zapisz nie powiodl sie - stan PZ nieznany, sprawdz GET.", phase, savedProbably, ex);

        switch (match)
        {
            case ReceiptStateMatch.Intended:
                return null;
            case ReceiptStateMatch.Diverged:
                return Unverified("Stan PZ po Zapisz nie jest ani docelowy, ani sprzed zadania (zmiana rownolegla?) - sprawdz GET.", phase, null, ex);
            default:
                if (ex is null)
                {
                    return new ReceiptEditException(ReceiptEditError.Locked,
                        "Sfera nie zapisala zmiany (Zapisz bez wyjatku, stan sprzed zadania) - dokument prawdopodobnie zablokowany do edycji.",
                        lockReason: ReceiptLockReason.NotSaved, phase: phase);
                }
                return FromClassification(ReceiptSaveFailureClassifier.Classify(ex), phase, existsAfter, nothingSavedCertain: false);
        }
    }

    private static ReceiptEditException Unverified(string message, ReceiptSavePhase phase, bool? savedProbably, Exception? ex)
    {
        var (hr, msg) = ex is null ? (null, null) : ReceiptSaveFailureClassifier.Describe(ex);
        return new ReceiptEditException(ReceiptEditError.UpdateUnverified, message, hresult: hr, sferaMessage: msg, phase: phase, savedProbably: savedProbably, inner: ex);
    }

    private static ReceiptEditException FromClassification(ReceiptFailureClass c, ReceiptSavePhase phase, bool? existsAfter, bool nothingSavedCertain)
    {
        string hrText = ReceiptEditException.HresultText(c.Hresult) ?? "-";
        switch (c.Kind)
        {
            case ReceiptFailureKind.Lock:
                return new ReceiptEditException(ReceiptEditError.Locked,
                    $"Sfera odmowila edycji PZ ({c.LockReason}): {c.Message} [{hrText}] - nic nie zmieniono.",
                    lockReason: c.LockReason, hresult: c.Hresult, sferaMessage: c.Message, phase: phase);
            case ReceiptFailureKind.Validation:
                return new ReceiptEditException(ReceiptEditError.Validation,
                    $"Sfera odrzucila wartosc ({c.ValidationCode}): {c.Message} [{hrText}] - nic nie zmieniono.",
                    code: c.ValidationCode, details: new { source = "sfera", hresult = ReceiptEditException.HresultText(c.Hresult), sfera_message = c.Message },
                    hresult: c.Hresult, sferaMessage: c.Message, phase: phase);
            case ReceiptFailureKind.NotFound:
                if (existsAfter == false)
                    return new ReceiptEditException(ReceiptEditError.NotFound, "PZ zostal usuniety z danych (0x80040F62) i nie istnieje w Subiekcie.", hresult: c.Hresult, sferaMessage: c.Message, phase: phase);
                return new ReceiptEditException(ReceiptEditError.UpdateUnverified,
                    $"Sfera zglosila usuniecie dokumentu ({hrText}), ale nie udalo sie tego potwierdzic - sprawdz GET.",
                    hresult: c.Hresult, sferaMessage: c.Message, phase: phase, savedProbably: false);
            default:
                return new ReceiptEditException(ReceiptEditError.UpdateFailed,
                    nothingSavedCertain
                        ? $"Modyfikacja PZ padla przed Zapisz ({hrText}: {c.Message}) - nic nie zapisano."
                        : $"Zapisz PZ padl ({hrText}: {c.Message}), stan po odczycie kontrolnym = sprzed zadania - nic nie zmieniono.",
                    hresult: c.Hresult, sferaMessage: c.Message, phase: phase);
        }
    }
}

/// <summary>Chroniony snapshot + pola nagłówka z odczytu COM (adapter na delegatach; <c>BookingStatus</c> dopisuje wołający z TryRead — prezentacyjne).</summary>
public sealed record ReceiptProtectedRead(ReceiptStateSnapshot Snapshot, int DokTyp, string Number, string? Uwagi, long? LinkedId);

/// <summary>
/// Odczyt chronionego snapshotu przez delegaty (Real: <c>InvokeMember</c> na SuDokument/SuPozycja; testy: słowniki). Reguła (Codex r4 #1,
/// klasa N4/N7): w torze decyzji o skutku NIC nie połyka wyjątku i NIC nie podstawia wartości — wyjątek delegatu → <see cref="ReceiptEditError.ReadFailed"/>
/// z <c>InnerException</c>; <c>null</c>/<c>DBNull</c> tam, gdzie kolumna jest NOT NULL (dok_Typ, dok_Status, dok_NrPelnyOryg, ob_Id, ob_Lp,
/// ob_Ilosc, ob_IloscMag, ob_CenaNetto, ob_Rabat, ob_VatProc, ob_TowRodzaj; UslugaJednorazowa to Boolean) → ReadFailed. Legalne nulle
/// (zrzut schematu / CHM): Uwagi, DoDokumentuId (brak linku; ≤ 0 też), TowarId (NULL = usługa jednorazowa, CHM SuPozycja_TowarId), Jm (ob_Jm
/// NULL → ""), NumerPelny (tylko do logów/DTO → ""). Kolejność pozycji = kolejność enumeracji (Element(1..Liczba)) — prezentację Real mapuje po indeksie.
/// Niekompletny snapshot nigdy nie dociera do komparatora: przed zapisem → 502 SUBIEKT_QUERY_FAILED, po zapisie → 502 RECEIPT_UPDATE_UNVERIFIED.
/// </summary>
public static class ReceiptSnapshotReader
{
    /// <param name="header">Atrybut nagłówka po nazwie (wyjątek = awaria odczytu).</param>
    /// <param name="positions">Kolekcja Pozycje: <c>null</c> = nieodczytana (ReadFailed); sekwencja akcesorów pozycji; wyjątek z enumeracji = awaria.</param>
    public static ReceiptProtectedRead Read(long id, Func<string, object?> header, Func<IEnumerable<Func<string, object?>>?> positions)
    {
        const string hdr = "naglowek";
        int typ = RequiredInt32(id, header, "Typ", hdr);
        int status = RequiredInt32(id, header, "StatusDokumentu", hdr);
        string originalNumber = RequiredString(id, header, "NumerOryginalny", hdr);
        string number = OptionalString(id, header, "NumerPelny", hdr) ?? string.Empty;
        string? uwagi = OptionalString(id, header, "Uwagi", hdr);
        long? linkedId = OptionalInt64(id, header, "DoDokumentuId", hdr);
        if (linkedId <= 0) linkedId = null;

        IEnumerable<Func<string, object?>>? seq;
        try { seq = positions(); }
        catch (Exception ex) { throw Failed(id, "Pozycje", "kolekcja", ex); }
        if (seq is null) throw Failed(id, "Pozycje", "kolekcja", null, "kolekcja Pozycje nieodczytana (null)");

        var items = new List<ReceiptItemSnapshot>();
        IEnumerator<Func<string, object?>> e;
        try { e = seq.GetEnumerator(); }
        catch (Exception ex) { throw Failed(id, "Pozycje", "kolekcja", ex); }
        using (e)
        {
            int index = 0;
            while (true)
            {
                bool more;
                try { more = e.MoveNext(); }
                catch (Exception ex) { throw Failed(id, $"Pozycje.Element({index + 1})", "pozycja", ex); }
                if (!more) break;
                index++;
                var poz = e.Current;
                string where = $"pozycja {index}";
                long positionId = RequiredInt64(id, poz, "Id", where);
                int lp = RequiredInt32(id, poz, "Lp", where);
                decimal quantity = RequiredDecimal(id, poz, "IloscJm", where);
                decimal stock = RequiredDecimal(id, poz, "Ilosc", where);
                string unit = OptionalString(id, poz, "Jm", where) ?? string.Empty;
                decimal price = RequiredDecimal(id, poz, "CenaNettoPrzedRabatem", where);
                decimal discount = RequiredDecimal(id, poz, "RabatProcent", where);
                decimal vat = RequiredDecimal(id, poz, "VatProcent", where);
                long? towarId = OptionalInt64(id, poz, "TowarId", where);
                bool oneOff = RequiredBool(id, poz, "UslugaJednorazowa", where);
                int rodzaj = RequiredInt32(id, poz, "TowarRodzaj", where);
                items.Add(new ReceiptItemSnapshot(positionId, lp, towarId, MapKind(towarId, oneOff, rodzaj), quantity, stock, unit, price, discount, vat));
            }
        }
        return new ReceiptProtectedRead(new ReceiptStateSnapshot(originalNumber, status, null, items), typ, number, uwagi, linkedId);
    }

    /// <summary>TowarId NULL albo UslugaJednorazowa → usługa jednorazowa; TowarRodzaj 1/2/4/8/32 (CHM TowarRodzajEnum) → rodzaj; inne → Unknown.</summary>
    public static ReceiptItemKind MapKind(long? towarId, bool oneOff, int rodzaj)
    {
        if (oneOff || towarId is null) return ReceiptItemKind.OneOffService;
        return rodzaj switch
        {
            1 => ReceiptItemKind.Product,
            2 => ReceiptItemKind.Service,
            4 => ReceiptItemKind.Package,
            8 => ReceiptItemKind.Bundle,
            32 => ReceiptItemKind.Suggestion,
            _ => ReceiptItemKind.Unknown,
        };
    }

    private static ReceiptEditException Failed(long id, string name, string where, Exception? inner, string? detail = null)
    {
        string why = detail ?? (inner is null ? "brak wartosci" : ReceiptSaveFailureClassifier.Unwrap(inner).Message);
        return new ReceiptEditException(ReceiptEditError.ReadFailed,
            $"Odczyt atrybutu {name} ({where}) dokumentu {id} padl przy zywej sesji: {why} - snapshot niekompletny, ponow pozniej.", inner: inner);
    }

    private static object? Raw(long id, Func<string, object?> source, string name, string where)
    {
        object? v;
        try { v = source(name); }
        catch (Exception ex) { throw Failed(id, name, where, ex); }
        return v is DBNull ? null : v;
    }

    private static object Required(long id, Func<string, object?> source, string name, string where)
        => Raw(id, source, name, where) ?? throw Failed(id, name, where, null, "wartosc null w polu wymaganym (kolumna NOT NULL)");

    private static T Convert<T>(long id, string name, string where, object raw, Func<object, T> convert)
    {
        try { return convert(raw); }
        catch (Exception ex) { throw Failed(id, name, where, ex, $"wartosc '{raw}' ({raw.GetType().Name}) nie daje sie przeliczyc: {ex.Message}"); }
    }

    private static int RequiredInt32(long id, Func<string, object?> s, string name, string where) => Convert(id, name, where, Required(id, s, name, where), System.Convert.ToInt32);
    private static long RequiredInt64(long id, Func<string, object?> s, string name, string where) => Convert(id, name, where, Required(id, s, name, where), System.Convert.ToInt64);
    private static decimal RequiredDecimal(long id, Func<string, object?> s, string name, string where) => Convert(id, name, where, Required(id, s, name, where), System.Convert.ToDecimal);
    private static bool RequiredBool(long id, Func<string, object?> s, string name, string where) => Convert(id, name, where, Required(id, s, name, where), System.Convert.ToBoolean);
    private static string RequiredString(long id, Func<string, object?> s, string name, string where) => Convert(id, name, where, Required(id, s, name, where), r => r.ToString() ?? string.Empty);
    private static string? OptionalString(long id, Func<string, object?> s, string name, string where) => Raw(id, s, name, where)?.ToString();
    private static long? OptionalInt64(long id, Func<string, object?> s, string name, string where)
        => Raw(id, s, name, where) is { } raw ? Convert(id, name, where, raw, System.Convert.ToInt64) : null;
}

/// <summary>
/// Klasyfikacja awarii <c>WczytajDokument</c> (spec §5.4 krok 1): martwa sesja → <see cref="SferaUnavailableException"/> (503, nic nie zapisano);
/// żywa → <c>Dokumenty.Istnieje</c>: <c>false</c> → NotFound (404 TYLKO przy pozytywnym braku), <c>true</c> albo wyjątek → ReadFailed (502).
/// Sonda sesji ZAWSZE przed Istnieje (po wyjątku RPC odczyt „nie istnieje” nic nie znaczy).
/// </summary>
public static class ReceiptLoadFailure
{
    public static Exception Classify(long id, Exception? loadException, Func<bool> sessionAlive, Func<bool> exists)
    {
        if (!sessionAlive())
        {
            return new SferaUnavailableException($"Sesja Sfery niedostepna przy odczycie dokumentu {id} - ponow pozniej.", loadException);
        }
        bool found;
        try { found = exists(); }
        catch (Exception ex)
        {
            return new ReceiptEditException(ReceiptEditError.ReadFailed,
                $"WczytajDokument({id}) padl przy zywej sesji, a Dokumenty.Istnieje tez padlo - nie da sie orzec, czy dokument istnieje; ponow pozniej.", inner: ex);
        }
        if (!found)
        {
            return new ReceiptEditException(ReceiptEditError.NotFound, $"PZ o subiekt_id={id} nie istnieje w Subiekcie.", inner: loadException);
        }
        return new ReceiptEditException(ReceiptEditError.ReadFailed,
            $"Dokument {id} istnieje (Istnieje=true), ale WczytajDokument {(loadException is null ? "zwrocil null" : "padl")} przy zywej sesji - ponow pozniej.",
            inner: loadException);
    }
}

/// <summary>
/// <c>Zamknij()</c> dokumentu MUTOWANEGO z raportem wyniku (TryClose połyka błąd — do odczytu kontrolnego potrzebny jest fakt). Porażka = lock Sfery
/// po mutacji może wisieć w sesji i sesja jest nieczysta → <paramref name="resetSession"/> (Real: licznik + <c>ResetSessionOnSta</c>; kolejne żądanie
/// otwiera sesję od nowa, bieżące dostaje 502 przez <c>sessionReset</c>). <paramref name="release"/> ZAWSZE, przed resetem; wyjątki cleanupu nie propagują.
/// </summary>
public static class ReceiptCloseGuard
{
    public static bool CloseVerified(Action zamknij, Action release, Action resetSession, Microsoft.Extensions.Logging.ILogger logger)
    {
        bool ok;
        try
        {
            zamknij();
            ok = true;
        }
        catch (Exception ex)
        {
            ok = false;
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, ex,
                "Zamknij() dokumentu po edycji rzucil - zamkniecie niepotwierdzone (odczyt kontrolny nie jest dowodem), sesja Sfery zostanie zresetowana");
        }
        try { release(); }
        catch (Exception ex) { Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(logger, ex, "ReleaseComObject po Zamknij rzucil (RCW juz zwolniony?)"); }
        if (!ok)
        {
            try { resetSession(); }
            catch (Exception ex) { Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, ex, "Reset sesji Sfery po niepotwierdzonym Zamknij rzucil"); }
        }
        return ok;
    }
}
