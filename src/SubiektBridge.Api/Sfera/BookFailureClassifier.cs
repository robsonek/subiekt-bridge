using System.Runtime.InteropServices;

namespace SubiektBridge.Api.Sfera;

/// <summary>Faza tworzenia operacji bankowej (BP/BW) w /book - decyduje o klasyfikacji wyjątku.</summary>
public enum SavePhase
{
    /// <summary>DodajOperacjeBankowa + ustawianie atrybutów, PRZED Zapisz: nic nie zapisano.</summary>
    Build,
    /// <summary>Samo Zapisz(): skutek zależy od HRESULT (odrzucone przez żywy proces vs transport zerwany).</summary>
    Save,
    /// <summary>Odczyt Identyfikator PO powrocie z Zapisz: BP istnieje, tylko id nieznane.</summary>
    ReadId,
}

/// <summary>
/// Zasada (spec 2026-09-29 „kod błędu = dowód o skutku"): `HB_BOOKING_FAILED` (BookError.Internal, klient
/// ponawia = NOWY BP) wolno zwrócić WYŁĄCZNIE, gdy wiadomo, że nic nie zapisano. Wynik nieznany → Orphan.
/// Czysta funkcja bez COM - tabela faza × HRESULT testowana w BookFailureClassifierTests.
/// </summary>
public static class BookFailureClassifier
{
    public static BookError Classify(SavePhase phase, Exception ex) => phase switch
    {
        SavePhase.Build => BookError.Internal,
        SavePhase.ReadId => BookError.Orphan,
        _ => Unwrap(ex) is COMException com
             && IsRejectedNothingSaved(com.ErrorCode)
             && !LooksLikeTransportFailure(com.Message)
            ? BookError.Internal
            : BookError.Orphan,
    };

    // Dostawca SQL (SQLOLEDB/SQLNCLI) raportuje zerwanie połączenia W TRAKCIE polecenia jako E_FAIL z opisem
    // ("General network error", "Communication link failure", "TCP Provider: ... forcibly closed"), a Sfera przekazuje
    // HRESULT i tekst dalej. Serwer mógł wykonać COMMIT, a klient nie dostał odpowiedzi -> wynik NIEZNANY mimo HRESULT
    // z allowlisty. Dopasowanie po fragmentach, case-insensitive; walidacja ("wartość za długa", RAISERROR) nie pasuje.
    private static readonly string[] TransportMarkers =
    {
        "network", "sieci",                                  // General network error / błąd sieciowy
        "communication link",                                // Communication link failure
        "tcp provider", "dbnetlib", "connectionread", "connectionwrite", "forcibly closed",
        "połączeni", "polaczeni",                            // zerwane/utracono połączenie
        "timeout", "time-out", "limit czasu", "czas oczekiwania",
    };

    public static bool LooksLikeTransportFailure(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        foreach (var marker in TransportMarkers)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// HRESULT-y, którymi Sfera/OLE DB raportują ODRZUCENIE zapisu przez żywy proces Subiekta (transakcja Sfery
    /// cofnięta): walidacja i RAISERROR triggera przychodzą jako E_FAIL (obserwowane w tym repo z zerowym skutkiem
    /// w bazie), błędy SQL jako OLE DB 0x80040Exx, wyjątek z EXCEPINFO jako DISP_E_EXCEPTION. Wszystko inne
    /// (RPC 0x8001xxxx, Win32 RPC_S_* 0x800706xx, E_UNEXPECTED, nieznane) = transport zerwany albo nieznane →
    /// konserwatywnie „wynik nieznany". Subiekt GT to osobny proces (Uruchom dopasowuje się do działającej
    /// aplikacji), więc utrata odpowiedzi po COMMIT jest realna.
    /// </summary>
    public static bool IsRejectedNothingSaved(int hresult)
    {
        uint hr = unchecked((uint)hresult);
        return hr switch
        {
            0x80004005 => true,                        // E_FAIL - walidacja Sfery / RAISERROR triggera
            0x80020009 => true,                        // DISP_E_EXCEPTION - błąd z EXCEPINFO od żywego serwera
            0x80070057 => true,                        // E_INVALIDARG
            0x80040F60 or 0x80040F62 => true,          // CHM SuDokument_Zapisz: brak towaru / dokument usunięty
            0x80040E31 or 0x80040E4E => false,         // DB_E_ABORTLIMITREACHED (timeout) / DB_E_CANCELED - wynik NIEZNANY
            >= 0x80040E00 and <= 0x80040EFF => true,   // OLE DB (DB_E_*): dane za długie, integralność, składnia
            _ => false,
        };
    }

    public static string Describe(Exception? ex) => Unwrap(ex) switch
    {
        null => "brak szczegółów",
        COMException com => $"0x{com.ErrorCode:X8} {com.Message}",
        Exception e => $"{e.GetType().Name}: {e.Message}",
    };

    private static Exception? Unwrap(Exception? ex)
        => ex is System.Reflection.TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;
}

/// <summary>
/// Wykonuje trzy fazy tworzenia operacji bankowej i opakowuje KAŻDY wyjątek w BankBookingException z powodem
/// z klasyfikatora. Real podaje lambdy na COM; testy podają lambdy rzucające COMException(hr).
/// BankBookingException rzucony z lambdy (np. SetLogged w fazie Build) przechodzi bez zmian.
/// </summary>
public static class BankOperationSave
{
    public static long Run(Action build, Action save, Func<long> readId)
    {
        try { build(); }
        catch (BankBookingException) { throw; }
        catch (Exception ex)
        {
            throw new BankBookingException(BookFailureClassifier.Classify(SavePhase.Build, ex),
                $"Budowa operacji bankowej padla (nic nie zapisano): {BookFailureClassifier.Describe(ex)}", ex);
        }

        try { save(); }
        catch (BankBookingException) { throw; }
        catch (Exception ex)
        {
            var reason = BookFailureClassifier.Classify(SavePhase.Save, ex);
            string msg = reason == BookError.Internal
                ? $"Zapisz operacji bankowej odrzucony przez Sfere (nic nie zapisano): {BookFailureClassifier.Describe(ex)}"
                : $"Zapisz operacji bankowej padl z wynikiem NIEZNANYM ({BookFailureClassifier.Describe(ex)}) - BP mogl powstac bez linku. Sprawdz recznie w module Bankowosc.";
            throw new BankBookingException(reason, msg, ex);
        }

        // Zapisz wrocil = BP utrwalony. Odczyt id: 2 proby (transient COM/konwersja), potem Orphan BEZ id.
        Exception? last = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                long id = readId();
                if (id > 0) return id;
                last = new InvalidOperationException($"Identyfikator={id} po Zapisz (RCW nie odswiezyl id)");
            }
            catch (Exception ex) { last = ex; }
        }
        throw new BankBookingException(BookError.Orphan,
            $"Operacja bankowa ZAPISANA, ale odczyt Identyfikator padl ({BookFailureClassifier.Describe(last)}) - ORPHAN bez id. Sprawdz recznie w module Bankowosc.", last);
    }
}
