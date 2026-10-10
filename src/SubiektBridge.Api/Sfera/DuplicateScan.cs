using Microsoft.Extensions.Logging;
using SubiektBridge.Api.Models;

namespace SubiektBridge.Api.Sfera;

/// <summary>Wiersz dok__Dokument z prefiltra SQL (dok_Uwagi LIKE '%ref%'): id, pełny numer, uwagi, dok_Status.</summary>
public sealed record DuplicateCandidate(long Id, string Number, string Uwagi, int Status);

/// <summary>Potwierdzony duplikat: istniejący dokument z tym samym external_reference (do 409 DUPLICATE_*).</summary>
public sealed record ExistingDocument(long Id, string Number);

/// <summary>
/// Rdzeń skanu duplikatów po external_reference (warstwa 2 idempotencji) — FAIL-CLOSED, bez COM/SQL (delegaty), testowalny
/// cross-platform. Spec 2026-10-10 (backlog 138, W5):
/// - kandydaci z read-only SQL po dok__Dokument (widzi WSZYSTKIE magazyny — COM OtworzKolekcje widział tylko magazyn sesji),
///   czytani strumieniowo (bez materializacji), przerwanie po pierwszym potwierdzonym trafieniu;
/// - LIKE to pre-filtr podciągu — duplikat = ref jako CAŁY token w Uwagach (UwagiFields.ContainsReferenceToken);
/// - duch (dokument do pominięcia) = WYŁĄCZNIE dok_Status = 2 (gtaSubiektDokumentStatusAnulowany, CHM SubiektDokumentStatusEnum);
///   0 = wycofany SKUTEK MAGAZYNOWY, 1 = wywołany, 3 = odłożony, 4 = MM na źródłowym — dokument ISTNIEJE;
/// - potwierdzenie COM (WczytajDokument): wyjątek przy martwej sesji → SferaUnavailableException (503 SUBIEKT_UNAVAILABLE),
///   wyjątek przy żywej sesji albo „nie wczytał” → DuplicateCheckUnavailableException (503) — nie zgadujemy;
/// - budżet czasu sprawdzany na KAŻDYM wyjściu (przed każdym kandydatem i po zakończeniu enumeracji, przed wynikiem
///   negatywnym): przekroczenie = 503, nigdy częściowy „brak duplikatu”. Deadline nie przerywa trwającego wywołania SQL/COM.
/// Każda inna awaria (pobranie/odczyt kandydatów) → DuplicateCheckUnavailableException; żadnego `catch → null`.
/// </summary>
public static class DuplicateScan
{
    /// <summary>dok_Status = 2: dokument unieważniony w programie — jedyny stan „nie istnieje” (CHM SubiektDokumentStatusEnum).</summary>
    public const int StatusAnulowany = 2;

    /// <summary>Budżet całego skanu (Real: Stopwatch startowany przed otwarciem połączenia SQL).</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <param name="fetchCandidates">Leniwa enumeracja kandydatów (DESC po dok_Id). Wyjątek przy otwarciu/odczycie = 503.</param>
    /// <param name="verifyLoads">COM WczytajDokument(id): true = obiekt wczytany. Wyjątek → klasyfikacja sondą sesji.</param>
    /// <param name="sessionAlive">Sonda sesji (IsSessionAlive) — wołana TYLKO po wyjątku weryfikacji COM.</param>
    /// <param name="deadlineExceeded">Budżet czasu (Stopwatch &gt; Deadline).</param>
    public static ExistingDocument? Find(
        string externalReference,
        string documentType,
        Func<IEnumerable<DuplicateCandidate>> fetchCandidates,
        Func<long, bool> verifyLoads,
        Func<bool> sessionAlive,
        Func<bool> deadlineExceeded,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(externalReference))
        {
            // Kontrolery odrzucają pusty ref 422 (INVALID_EXTERNAL_REFERENCE); tu nigdy "null = brak duplikatu".
            throw new ArgumentException("external_reference jest wymagane do skanu duplikatow.", nameof(externalReference));
        }

        IEnumerator<DuplicateCandidate> candidates;
        try
        {
            candidates = fetchCandidates().GetEnumerator();
        }
        catch (Exception ex)
        {
            throw Unavailable(externalReference, documentType, "pobranie kandydatow z bazy padlo", ex, logger);
        }

        using (candidates)
        {
            while (true)
            {
                if (deadlineExceeded())
                {
                    throw Unavailable(externalReference, documentType, $"przekroczony czas skanu {Deadline.TotalSeconds:0}s", null, logger);
                }

                bool more;
                try
                {
                    more = candidates.MoveNext();
                }
                catch (Exception ex)
                {
                    throw Unavailable(externalReference, documentType, "odczyt kandydatow z bazy padl", ex, logger);
                }
                if (!more)
                {
                    break;
                }

                // Sam odczyt (Read) mogl przekroczyc budzet: kontrola takze PO udanym MoveNext, PRZED token-checkiem i COM
                // (review kodu P2: trafienie odczytane po terminie nie moze uruchomic COM ani dac 409 zamiast 503).
                if (deadlineExceeded())
                {
                    throw Unavailable(externalReference, documentType, $"przekroczony czas skanu {Deadline.TotalSeconds:0}s (po odczycie kandydata)", null, logger);
                }

                var c = candidates.Current;

                // LIKE '%ref%' to pre-filtr podciągu: "order:12" pasuje do "order:123" → token-check (jak od v0.18.0).
                if (!UwagiFields.ContainsReferenceToken(c.Uwagi, externalReference))
                {
                    continue;
                }

                if (c.Status == StatusAnulowany)
                {
                    logger.LogInformation("Anti-duplicate: dok_Id={Id} ({Number}) z ref='{Ref}' ma dok_Status=2 (uniewazniony) - pomijam jako duch",
                        c.Id, c.Number, externalReference);
                    continue;
                }

                bool loaded;
                try
                {
                    loaded = verifyLoads(c.Id);
                }
                catch (Exception ex)
                {
                    if (!sessionAlive())
                    {
                        throw new SferaUnavailableException(
                            $"Sesja Sfery niedostepna przy weryfikacji duplikatu ref='{externalReference}' ({documentType}, dok_Id={c.Id}) - nic nie zapisano, ponow pozniej.", ex);
                    }
                    throw Unavailable(externalReference, documentType,
                        $"weryfikacja dokumentu dok_Id={c.Id} (dok_Status={c.Status}) padla przy zywej sesji", ex, logger);
                }
                if (!loaded)
                {
                    throw Unavailable(externalReference, documentType,
                        $"dokument dok_Id={c.Id} (dok_Status={c.Status}) nie dal sie wczytac", null, logger);
                }

                return new ExistingDocument(c.Id, c.Number);
            }
        }

        // Pusty wynik albo ostatni odczyt po terminie NIE moze dac "brak duplikatu" (r3 F6).
        if (deadlineExceeded())
        {
            throw Unavailable(externalReference, documentType, $"przekroczony czas skanu {Deadline.TotalSeconds:0}s (po enumeracji)", null, logger);
        }
        return null;
    }

    private static DuplicateCheckUnavailableException Unavailable(string externalReference, string documentType, string reason, Exception? inner, ILogger logger)
    {
        logger.LogError(inner, "Anti-duplicate ({Typ}, ref='{Ref}') FAIL-CLOSED: {Reason} - dokument nie zostanie wystawiony", documentType, externalReference, reason);
        return new DuplicateCheckUnavailableException(externalReference, documentType, inner, reason);
    }
}
