using Microsoft.Extensions.Logging.Abstractions;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Spec 2026-10-10 (backlog 138, W5/W6): rdzeń skanu duplikatów po external_reference jest fail-closed.
/// Kandydaci przychodzą z read-only SQL (wszystkie magazyny), token-check w C#, duch WYŁĄCZNIE po dok_Status = 2
/// (CHM SubiektDokumentStatusEnum: 0 = wycofany skutek magazynowy = dokument istnieje), potwierdzenie COM
/// fail-closed, deadline sprawdzany na KAŻDYM wyjściu. Rdzeń na delegatach - bez COM/SQL.
/// </summary>
public class DuplicateScanTests
{
    private const string Ref = "sys:order:12";

    private static DuplicateCandidate Cand(long id, string uwagi, int status = 1, string number = "FS 1/2026")
        => new(id, number, uwagi, status);

    private static ExistingDocument? Find(
        Func<IEnumerable<DuplicateCandidate>> fetch,
        Func<long, bool>? verify = null,
        Func<bool>? alive = null,
        Func<bool>? deadline = null,
        string reference = Ref)
        => DuplicateScan.Find(reference, "FS", fetch,
            verify ?? (_ => true), alive ?? (() => true), deadline ?? (() => false), NullLogger.Instance);

    private static IEnumerable<DuplicateCandidate> Throwing(int afterCount)
    {
        for (int i = 0; i < afterCount; i++)
        {
            yield return Cand(100 - i, "ref: sys:order:999");
        }
        throw new InvalidOperationException("symulowany blad SQL");
    }

    // ----------------------------- 33: awaria pobrania/enumeracji -----------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FetchThrows_DuplicateCheckUnavailable_SessionNotProbed(int afterCount)
    {
        // Mutant: catch-all -> return null (fail-open); Mutant: sonda sesji wolana dla bledu SQL (SqlClient != sesja).
        int probes = 0;
        var ex = Assert.Throws<DuplicateCheckUnavailableException>(() =>
            Find(() => Throwing(afterCount), alive: () => { probes++; return true; }));

        Assert.Equal(Ref, ex.ExternalReference);
        Assert.Equal("FS", ex.DocumentType);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal(0, probes);
    }

    [Fact]
    public void DeadlineExceeded_BeforeSecondCandidate_DuplicateCheckUnavailable()
    {
        // Mutant: po deadline return null; Mutant: deadline sprawdzany tylko na starcie.
        int calls = 0;
        var candidates = new[] { Cand(2, "ref: sys:order:999"), Cand(1, "ref: " + Ref) };

        Assert.Throws<DuplicateCheckUnavailableException>(() =>
            Find(() => candidates, deadline: () => ++calls >= 2));
    }

    [Fact]
    public void FetchDelegateThrowsDirectly_DuplicateCheckUnavailable()
    {
        // Wyjatek z samego delegata (nie z iteratora): np. SqlConnection.Open poza iteratorem. Mutant: catch tylko wokol MoveNext.
        var ex = Assert.Throws<DuplicateCheckUnavailableException>(() =>
            Find(() => throw new InvalidOperationException("Open padl")));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void DeadlineExceeded_AfterEmptyEnumeration_DuplicateCheckUnavailable()
    {
        // 33d: 0 kandydatow, budzet wyczerpany DOPIERO w trakcie (pustego) odczytu -> NIE "brak duplikatu".
        // Flaga uplywu ustawiana wewnatrz iteratora: przed enumeracja deadline mowi "jest czas".
        bool expired = false;
        IEnumerable<DuplicateCandidate> Empty() { expired = true; yield break; }

        Assert.Throws<DuplicateCheckUnavailableException>(() => Find(Empty, deadline: () => expired));
    }

    [Fact]
    public void DeadlineExceeded_AfterLastReadNoMatch_DuplicateCheckUnavailable()
    {
        // 33e: dwa nietrafione; budzet wyczerpany w ostatnim MoveNext (tym, ktory zwraca false) -> kontrola po enumeracji,
        // przed return null. Mutant: brak koncowej kontroli -> null.
        bool expired = false;
        IEnumerable<DuplicateCandidate> Seq()
        {
            yield return Cand(2, "ref: sys:order:999");
            yield return Cand(1, "ref: sys:order:998");
            expired = true;
        }

        Assert.Throws<DuplicateCheckUnavailableException>(() => Find(Seq, deadline: () => expired));
    }

    [Fact]
    public void DeadlineExceeded_DuringReadOfMatchingCandidate_DuplicateCheckUnavailable_VerifyNotCalled()
    {
        // 33f (review kodu P2): sam Read trafienia przekracza budzet -> 503 BEZ wywolania COM i bez 409.
        // Mutant: deadline sprawdzany tylko przed MoveNext -> verify wolane, zwrot ExistingDocument.
        bool expired = false;
        int verifies = 0;
        IEnumerable<DuplicateCandidate> Seq()
        {
            yield return Cand(2, "ref: sys:order:999");
            expired = true;
            yield return Cand(1, "ref: " + Ref);
        }

        Assert.Throws<DuplicateCheckUnavailableException>(() =>
            Find(Seq, verify: _ => { verifies++; return true; }, deadline: () => expired));
        Assert.Equal(0, verifies);
    }

    [Fact]
    public void FirstConfirmedMatch_StopsEnumeration()
    {
        // 33c: enumerator rzuca przy 3. elemencie - rdzen musi przerwac po 2. (trafienie potwierdzone).
        static IEnumerable<DuplicateCandidate> Seq()
        {
            yield return Cand(30, "ref: sys:order:999");
            yield return Cand(20, "ref: " + Ref, number: "FS 20/2026");
            throw new InvalidOperationException("nie powinno byc czytane");
        }

        var found = Find(Seq);

        Assert.NotNull(found);
        Assert.Equal(20, found!.Id);
        Assert.Equal("FS 20/2026", found.Number);
    }

    // ----------------------------- 34-36: token, duch, kolejnosc -----------------------------

    [Fact]
    public void NoTokenMatch_Null_VerifyNotCalled()
    {
        // LIKE '%order:12%' trafia order:123 - token-check odrzuca; COM nie jest wolany.
        int verifies = 0;
        var found = Find(() => new[] { Cand(5, "ref: sys:order:123") }, verify: _ => { verifies++; return true; });

        Assert.Null(found);
        Assert.Equal(0, verifies);
    }

    [Fact]
    public void CancelledStatus2_Skipped_Null_VerifyNotCalled()
    {
        // Duch = dok_Status 2 (unieważniony). Mutant: duch -> 409; Mutant: COM wolane dla ducha.
        int verifies = 0;
        var found = Find(() => new[] { Cand(5, "ref: " + Ref, status: DuplicateScan.StatusAnulowany) },
            verify: _ => { verifies++; return true; });

        Assert.Null(found);
        Assert.Equal(0, verifies);
    }

    [Fact]
    public void ActiveStatus_Verified_ReturnsNewestFirst()
    {
        // Kandydaci DESC po dok_Id: pierwszy potwierdzony wygrywa; COM wolane raz.
        var verified = new List<long>();
        var found = Find(
            () => new[] { Cand(20, "ref: " + Ref, number: "FS 20/2026"), Cand(10, "ref: " + Ref, number: "FS 10/2026") },
            verify: id => { verified.Add(id); return true; });

        Assert.Equal(20, found!.Id);
        Assert.Equal("FS 20/2026", found.Number);
        Assert.Equal(new[] { 20L }, verified);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void OtherStatuses_TreatedAsExisting(int status)
    {
        // Mutant: status 0 traktowany jak duch (wycofany SKUTEK MAGAZYNOWY != uniewazniony); Mutant: pominiecie odlozonych.
        int verifies = 0;
        var found = Find(() => new[] { Cand(7, "ref: " + Ref, status: status) }, verify: _ => { verifies++; return true; });

        Assert.NotNull(found);
        Assert.Equal(7, found!.Id);
        Assert.Equal(1, verifies);
    }

    // ----------------------------- 37-39: weryfikacja COM fail-closed -----------------------------

    [Fact]
    public void VerifyThrows_SessionAlive_DuplicateCheckUnavailable()
    {
        // Mutant: continue (duch) - dokument o stanie "istnieje" nie daje sie wczytac = nie zgadujemy.
        var ex = Assert.Throws<DuplicateCheckUnavailableException>(() =>
            Find(() => new[] { Cand(7, "ref: " + Ref) },
                verify: _ => throw new InvalidOperationException("COM padl"),
                alive: () => true));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void VerifyThrows_SessionDead_SferaUnavailable()
    {
        // Mutant: DuplicateCheckUnavailable bez sondy - martwa sesja to 503 SUBIEKT_UNAVAILABLE (juz na liscie retry).
        Assert.Throws<SferaUnavailableException>(() =>
            Find(() => new[] { Cand(7, "ref: " + Ref) },
                verify: _ => throw new InvalidOperationException("RPC zerwane"),
                alive: () => false));
    }

    [Fact]
    public void VerifyFalse_DuplicateCheckUnavailable_NoProbe()
    {
        // Wywolanie wrocilo (sesja odpowiada) - 503 bez sondy. Mutant: continue; Mutant: sonda wolana.
        int probes = 0;
        Assert.Throws<DuplicateCheckUnavailableException>(() =>
            Find(() => new[] { Cand(7, "ref: " + Ref) }, verify: _ => false, alive: () => { probes++; return true; }));

        Assert.Equal(0, probes);
    }

    // ----------------------------- 41-42: ref pusty, DokTyp -----------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyReference_Throws(string? reference)
    {
        // Mutant: return null (skan pominiety = fail-open).
        Assert.Throws<ArgumentException>(() => Find(() => Array.Empty<DuplicateCandidate>(), reference: reference!));
    }

    [Theory]
    [InlineData(2, "FS")]
    [InlineData(6, "KFS")]
    [InlineData(9, "MM")]
    [InlineData(10, "PZ")]
    public void DokTyp_Name_MapsKnownTypes(int dokTyp, string expected)
    {
        // Mutant: PZ/MM zamienione.
        Assert.Equal(expected, DokTyp.Name(dokTyp));
    }

    [Fact]
    public void DokTyp_Name_UnknownThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DokTyp.Name(99));
        Assert.Equal(2, DokTyp.FS);
        Assert.Equal(6, DokTyp.KFS);
        Assert.Equal(9, DokTyp.MM);
        Assert.Equal(10, DokTyp.PZ);
    }

    // ----------------------------- 55: walidacja external_reference -----------------------------

    [Fact]
    public void ValidateExternalReference_MaxLength_Is495()
    {
        // 500 (dok_Uwagi) - "ref: " = 495.
        Assert.Equal(495, UwagiFields.MaxExternalReferenceLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateExternalReference_Empty_Error(string? reference)
    {
        Assert.NotNull(UwagiFields.ValidateExternalReference(reference));
    }

    [Fact]
    public void ValidateExternalReference_Boundary_495Ok_496Error()
    {
        // Mutant: granica > 500 / >= 495.
        Assert.Null(UwagiFields.ValidateExternalReference(new string('a', 495)));
        Assert.NotNull(UwagiFields.ValidateExternalReference(new string('a', 496)));
    }

    [Fact]
    public void DuplicateCheckUnavailableException_CarriesReferenceAndType()
    {
        var inner = new InvalidOperationException("x");
        var ex = new DuplicateCheckUnavailableException("sys:order:1", "PZ", inner);

        Assert.Equal("sys:order:1", ex.ExternalReference);
        Assert.Equal("PZ", ex.DocumentType);
        Assert.Same(inner, ex.InnerException);
        Assert.Contains("NIE zostal wystawiony", ex.Message);
    }
}
