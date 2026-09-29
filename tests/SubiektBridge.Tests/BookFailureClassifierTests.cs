using System.Runtime.InteropServices;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// W1 (spec 2026-09-29 „kod błędu = dowód o skutku"): wyjątek po `bp.Zapisz()` NIGDY nie może wyglądać na
/// „czysty" błąd (HB_BOOKING_FAILED / INTERNAL_ERROR), bo klient ponawia i powstaje drugi BP. Klasyfikacja
/// faza × wyjątek × HRESULT jest czystą funkcją - testowalna bez COM. `BankOperationSave.Run` wykonuje fazy
/// i opakowuje wyjątki w BankBookingException z właściwym powodem.
/// </summary>
public class BookFailureClassifierTests
{
    private static COMException Com(uint hr) => new("com error", unchecked((int)hr));

    // --- faza Build: nic nie zapisano -> Internal (retry bezpieczny), niezaleznie od typu wyjatku ---

    [Theory]
    [InlineData(0x80004005u)] // E_FAIL
    [InlineData(0x80010108u)] // RPC_E_DISCONNECTED - przed Zapisz nadal nic nie zapisano
    public void Build_ComException_IsInternal(uint hr)
        => Assert.Equal(BookError.Internal, BookFailureClassifier.Classify(SavePhase.Build, Com(hr)));

    [Fact]
    public void Build_NonComException_IsInternal()
        => Assert.Equal(BookError.Internal, BookFailureClassifier.Classify(SavePhase.Build, new InvalidCastException()));

    // --- faza ReadId: Zapisz wrocil, BP istnieje -> Orphan zawsze ---

    [Theory]
    [InlineData(0x80004005u)]
    [InlineData(0x80010108u)]
    public void ReadId_ComException_IsOrphan(uint hr)
        => Assert.Equal(BookError.Orphan, BookFailureClassifier.Classify(SavePhase.ReadId, Com(hr)));

    [Fact]
    public void ReadId_NonComException_IsOrphan()
        => Assert.Equal(BookError.Orphan, BookFailureClassifier.Classify(SavePhase.ReadId, new InvalidCastException()));

    // --- faza Save: Internal TYLKO dla HRESULT-ow „odrzucone przez zywy proces" (allowlista, D2) ---

    [Theory]
    [InlineData(0x80004005u)] // E_FAIL - walidacja Sfery / RAISERROR triggera
    [InlineData(0x80040E21u)] // OLE DB: multi-step (dane za dlugie)
    [InlineData(0x80040E14u)] // OLE DB: blad w poleceniu
    [InlineData(0x80040E2Fu)] // OLE DB: naruszenie integralnosci
    [InlineData(0x80020009u)] // DISP_E_EXCEPTION - EXCEPINFO od zywego serwera
    [InlineData(0x80070057u)] // E_INVALIDARG
    [InlineData(0x80040F60u)] // CHM SuDokument_Zapisz: brak towaru
    [InlineData(0x80040F62u)] // CHM SuDokument_Zapisz: dokument usuniety
    public void Save_RejectedHresult_IsInternal(uint hr)
        => Assert.Equal(BookError.Internal, BookFailureClassifier.Classify(SavePhase.Save, Com(hr)));

    [Theory]
    [InlineData(0x80010108u)] // RPC_E_DISCONNECTED
    [InlineData(0x80010105u)] // RPC_E_SERVERFAULT
    [InlineData(0x80010001u)] // RPC_E_CALL_REJECTED
    [InlineData(0x800706BAu)] // RPC_S_SERVER_UNAVAILABLE
    [InlineData(0x800706BEu)] // RPC_S_CALL_FAILED
    [InlineData(0x800706BFu)] // RPC_S_CALL_FAILED_DNE
    [InlineData(0x800401FDu)] // CO_E_OBJNOTCONNECTED
    [InlineData(0x8000FFFFu)] // E_UNEXPECTED
    [InlineData(0x80004004u)] // E_ABORT
    [InlineData(0x80040F00u)] // nieznany kod ITF - domyslnie konserwatywnie
    [InlineData(0x80070005u)] // E_ACCESSDENIED (Win32, nie-RPC) - domyslnie konserwatywnie
    public void Save_TransportOrUnknownHresult_IsOrphan(uint hr)
        => Assert.Equal(BookError.Orphan, BookFailureClassifier.Classify(SavePhase.Save, Com(hr)));

    [Fact]
    public void Save_NonComException_IsOrphan()
        => Assert.Equal(BookError.Orphan, BookFailureClassifier.Classify(SavePhase.Save, new InvalidOperationException("binder")));

    // --- F1 z przegladu: E_FAIL/OLE DB z KOMUNIKATEM transportowym = zerwanie polaczenia W TRAKCIE polecenia ---
    // (dostawca SQL raportuje "General network error"/"Communication link failure" jako E_FAIL; serwer mogl
    // wykonac COMMIT, a klient nie dostal odpowiedzi) -> wynik NIEZNANY -> Orphan mimo HRESULT z allowlisty.

    [Theory]
    [InlineData(0x80004005u, "General network error. Check your network documentation.")]
    [InlineData(0x80004005u, "Communication link failure")]
    [InlineData(0x80004005u, "TCP Provider: An existing connection was forcibly closed by the remote host.")]
    [InlineData(0x80004005u, "[DBNETLIB][ConnectionRead (recv()).]General network error.")]
    [InlineData(0x80004005u, "Zerwane połączenie z serwerem SQL")]
    [InlineData(0x80004005u, "Przekroczono limit czasu oczekiwania (Timeout expired)")]
    [InlineData(0x80004005u, "Błąd sieciowy podczas zapisu")]
    [InlineData(0x80040E21u, "Multiple-step OLE DB operation generated errors: Communication link failure")]
    public void Save_RejectedHresult_ButTransportMessage_IsOrphan(uint hr, string message)
        => Assert.Equal(BookError.Orphan, BookFailureClassifier.Classify(SavePhase.Save, new COMException(message, unchecked((int)hr))));

    [Theory]
    [InlineData(0x80040E31u)] // DB_E_ABORTLIMITREACHED - "Timeout expired" po command timeout
    [InlineData(0x80040E4Eu)] // DB_E_CANCELED - polecenie anulowane w trakcie
    public void Save_OleDbTimeoutOrCanceled_IsOrphan(uint hr)
        => Assert.Equal(BookError.Orphan, BookFailureClassifier.Classify(SavePhase.Save, Com(hr)));

    [Theory]
    [InlineData(0x80004005u, "Wartość za długa dla pola Tytulem")]
    [InlineData(0x80004005u, "Rachunek bankowy jest nieaktywny")]
    [InlineData(0x80004005u, "Operacja w walucie obcej wymaga kursu")] // RAISERROR triggera - zywy serwer odrzucil
    [InlineData(0x80040E21u, "Multiple-step OLE DB operation generated errors. Check each OLE DB status value.")]
    public void Save_RejectedHresult_ValidationMessage_StaysInternal(uint hr, string message)
        => Assert.Equal(BookError.Internal, BookFailureClassifier.Classify(SavePhase.Save, new COMException(message, unchecked((int)hr))));

    // --- BankOperationSave.Run: fazy + opakowanie w BankBookingException ---

    [Fact]
    public void Run_HappyPath_ReturnsId()
    {
        long id = BankOperationSave.Run(build: () => { }, save: () => { }, readId: () => 4242);
        Assert.Equal(4242, id);
    }

    [Fact]
    public void Run_BuildThrows_InternalWithoutId_SaveNotCalled()
    {
        bool saved = false;
        var ex = Assert.Throws<BankBookingException>(() =>
            BankOperationSave.Run(build: () => throw Com(0x80004005), save: () => saved = true, readId: () => 1));
        Assert.Equal(BookError.Internal, ex.Reason);
        Assert.Null(ex.BankOperationSubiektId);
        Assert.False(saved);
        Assert.IsType<COMException>(ex.InnerException);
    }

    [Fact]
    public void Run_SaveThrowsRejected_InternalWithoutId_ReadIdNotCalled()
    {
        bool read = false;
        var ex = Assert.Throws<BankBookingException>(() =>
            BankOperationSave.Run(build: () => { }, save: () => throw Com(0x80004005), readId: () => { read = true; return 1; }));
        Assert.Equal(BookError.Internal, ex.Reason);
        Assert.Null(ex.BankOperationSubiektId);
        Assert.False(read);
    }

    [Fact]
    public void Run_SaveThrowsTransport_OrphanWithoutId()
    {
        var ex = Assert.Throws<BankBookingException>(() =>
            BankOperationSave.Run(build: () => { }, save: () => throw Com(0x80010108), readId: () => 1));
        Assert.Equal(BookError.Orphan, ex.Reason);
        Assert.Null(ex.BankOperationSubiektId);
    }

    [Fact]
    public void Run_SaveThrowsNonCom_OrphanWithoutId()
    {
        var ex = Assert.Throws<BankBookingException>(() =>
            BankOperationSave.Run(build: () => { }, save: () => throw new InvalidOperationException("binder"), readId: () => 1));
        Assert.Equal(BookError.Orphan, ex.Reason);
        Assert.Null(ex.BankOperationSubiektId);
    }

    [Fact]
    public void Run_ReadIdFailsOnce_RetriesAndReturnsId()
    {
        int calls = 0;
        long id = BankOperationSave.Run(build: () => { }, save: () => { }, readId: () =>
        {
            calls++;
            if (calls == 1) throw new InvalidCastException("VT_EMPTY");
            return 777;
        });
        Assert.Equal(777, id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Run_ReadIdFailsTwice_OrphanWithoutId()
    {
        int calls = 0;
        var ex = Assert.Throws<BankBookingException>(() =>
            BankOperationSave.Run(build: () => { }, save: () => { }, readId: () => { calls++; throw new InvalidCastException(); }));
        Assert.Equal(BookError.Orphan, ex.Reason);
        Assert.Null(ex.BankOperationSubiektId);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Run_ReadIdReturnsNonPositive_OrphanWithoutId()
    {
        // Zapisz przeszlo, ale Identyfikator to -1/0 (RCW nie odswiezyl) - BP moze istniec, id nieznane.
        var ex = Assert.Throws<BankBookingException>(() =>
            BankOperationSave.Run(build: () => { }, save: () => { }, readId: () => -1));
        Assert.Equal(BookError.Orphan, ex.Reason);
        Assert.Null(ex.BankOperationSubiektId);
    }

    [Fact]
    public void BankBookingException_CarriesBankOperationId()
    {
        var ex = new BankBookingException(BookError.Orphan, "orphan", bankOperationSubiektId: 91_000);
        Assert.Equal(91_000, ex.BankOperationSubiektId);
    }
}
