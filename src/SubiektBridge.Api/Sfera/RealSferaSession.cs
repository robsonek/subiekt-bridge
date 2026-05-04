using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Models;

namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Prawdziwa sesja COM/Sfera Subiekt GT. Wyłącznie Windows.
///
/// Architektura:
/// - <c>dynamic</c> + <c>Type.GetTypeFromProgID("InsERT.GT")</c> - late binding, bez TLB.
/// - Stringi przekazujemy do COM jako standardowe .NET <c>string</c> (UTF-16). COM marshaler
///   konwertuje je do <c>BSTR</c> automatycznie. Subiekt wewnętrznie konwertuje na CP1250
///   przy zapisie do MSSQL - my się tym nie zajmujemy.
/// - Dedykowany STA thread + kolejka <see cref="BlockingCollection{T}"/> dla wszystkich
///   wywołań COM. KLUCZOWE: <c>InsERT.GT</c> jest STA (apartment threaded), a thread pool
///   ASP.NET Core jest MTA. Próba <c>Activator.CreateInstance</c> z MTA threada na STA-only
///   in-proc COM rzuca <c>0x8000FFFF E_UNEXPECTED</c>. Wszystkie operacje muszą wykonać się
///   na tym samym STA threadzie.
/// - Sesja trzymana przez cały czas życia procesu (lazy init), auto-recreate przy crashu.
///
/// Wymagania na Windowsie klienta:
/// - Subiekt GT zainstalowany (32-bit - cała linia GT jest x86)
/// - Sfera aktywna i wykupiona dla operatora
/// - Bridge zbudowany jako win-x86 (in-proc COM = bit-level musi pasować)
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealSferaSession : ISferaSession
{
    private readonly SubiektOptions _options;
    private readonly ILogger<RealSferaSession> _logger;
    private readonly Thread _staThread;
    private readonly BlockingCollection<Action> _workQueue = new();
    private dynamic? _subiekt;
    private DateTimeOffset? _lastInvoiceAt;
    private string? _subiektVersion;
    private string? _lastError;
    private bool _disposed;

    public RealSferaSession(SubiektOptions options, ILogger<RealSferaSession> logger)
    {
        _options = options;
        _logger = logger;

        _staThread = new Thread(WorkerLoop)
        {
            Name = "Sfera-STA-Worker",
            IsBackground = true,
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    /// <summary>Ostatni błąd Sfery (do prezentacji w /health endpoint).</summary>
    public string? LastError => _lastError;

    private void WorkerLoop()
    {
        _logger.LogInformation("Sfera STA worker started, ApartmentState={State}",
            Thread.CurrentThread.GetApartmentState());

        foreach (var work in _workQueue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // Wyjątki delegowane do TaskCompletionSource w wywołaniu.
                _logger.LogDebug(ex, "STA worker action threw (delegated to caller)");
            }
        }

        // Sesję zamykamy na tym samym STA threadzie.
        ResetSessionOnSta();
    }

    /// <summary>
    /// Wykonuje funkcję na STA threadzie i zwraca Task. Kolejka serializuje wywołania
    /// (jeden COM call naraz, bez race conditions w sesji Sfery).
    /// </summary>
    private Task<T> RunOnStaAsync<T>(Func<T> func, CancellationToken ct)
    {
        if (_disposed)
        {
            return Task.FromException<T>(new ObjectDisposedException(nameof(RealSferaSession)));
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Cancellation: jeśli token anulowany przed startem - od razu cancel.
        ct.Register(() => tcs.TrySetCanceled(ct));

        _workQueue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, CancellationToken.None);

        return tcs.Task;
    }

    private dynamic Session
    {
        get
        {
            if (_subiekt is null)
            {
                _subiekt = OpenSession();
            }
            return _subiekt;
        }
    }

    private dynamic OpenSession()
    {
        _logger.LogInformation("Opening Sfera session: server={Server}, db={Db}, operator={Operator}, apartment={Apt}",
            _options.Server, _options.Database, _options.Operator,
            Thread.CurrentThread.GetApartmentState());

        var gtType = Type.GetTypeFromProgID("InsERT.GT")
            ?? throw new InvalidOperationException(
                "ProgID 'InsERT.GT' nie znaleziony. Zainstaluj Subiekt GT i Sferę na tym hoście.");
        var dodatkiType = Type.GetTypeFromProgID("InsERT.Dodatki")
            ?? throw new InvalidOperationException(
                "ProgID 'InsERT.Dodatki' nie znaleziony.");

        dynamic gt = Activator.CreateInstance(gtType)!;
        dynamic dodatki = Activator.CreateInstance(dodatkiType)!;

        gt.Produkt = _options.Product;            // 1 = Subiekt
        gt.Autentykacja = _options.Authentication; // 0 = SQL auth
        gt.Serwer = _options.Server;
        gt.Uzytkownik = _options.DbUser;
        gt.UzytkownikHaslo = dodatki.Szyfruj(_options.DbPassword);
        gt.Baza = _options.Database;
        gt.Operator = _options.Operator;
        gt.OperatorHaslo = dodatki.Szyfruj(_options.OperatorPassword);

        // 0 = bez UI, 4 = profil "Subiekt" (ze sprzedażą).
        dynamic session = gt.Uruchom(0, 4);

        try { _subiektVersion = (string?)session.Aplikacja?.Wersja; } catch { /* opcjonalne */ }

        _lastError = null;
        return session;
    }

    // -------------------------- Health --------------------------

    public Task<SferaHealthDto> HealthAsync(CancellationToken ct)
    {
        return RunOnStaAsync(() =>
        {
            try
            {
                _ = Session; // trigger lazy open
                return new SferaHealthDto(_subiektVersion ?? "unknown", true, _lastInvoiceAt, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sfera health check failed");
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
                ResetSessionOnSta();
                return new SferaHealthDto("unknown", false, _lastInvoiceAt, _lastError);
            }
        }, ct);
    }

    // -------------------------- Query (read-only listing) --------------------------

    public Task<IReadOnlyList<InvoiceQueryItemDto>> QueryInvoicesAsync(InvoiceQueryRequestDto request, CancellationToken ct)
    {
        return RunOnStaAsync<IReadOnlyList<InvoiceQueryItemDto>>(() => QueryInvoicesCore(request), ct);
    }

    private IReadOnlyList<InvoiceQueryItemDto> QueryInvoicesCore(InvoiceQueryRequestDto request)
    {
        var filter = BuildInvoiceQueryFilter(request);
        // Type filter client-side (kolumna SQL nie istnieje dla NumerPelny).
        // Pobieramy wiecej niz limit zeby po filtrze nie zabraklo, hard cap = limit*5.
        var typeFilter = string.IsNullOrWhiteSpace(request.Type)
            ? null
            : request.Type.Trim().ToUpperInvariant();
        var fetchCap = typeFilter != null ? Math.Min(request.Limit * 5, 1000) : Math.Min(request.Limit, 1000);
        // Sfera dokumentacja nie udostepnia listy kolumn dok__Dokument; dok_Id jest
        // bezpieczne (PK), data wystawienia ma rozne nazwy w roznych wersjach Subiekta
        // (dok_DataWyst vs dok_DataWystawienia) - sortujemy po dok_Id DESC zeby dostac
        // najnowsze (Subiekt nadaje rosnace ID).
        var sort = "dok_Id DESC";
        var limit = Math.Clamp(request.Limit <= 0 ? 200 : request.Limit, 1, 1000);

        _logger.LogInformation("QueryInvoices filter={Filter} sort={Sort} limit={Limit}",
            filter, sort, limit);

        dynamic kolekcja = Session.SuDokumentyManager.OtworzKolekcje(filter, sort);

        // Liczba moze byc int albo Variant - cast defensywnie.
        int total;
        try { total = Convert.ToInt32(kolekcja.Liczba); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryInvoices: nie mozna odczytac kolekcja.Liczba");
            throw;
        }
        _logger.LogInformation("QueryInvoices: kolekcja zwrocila {Total} dokumentow (cap={Limit})", total, limit);

        var items = new List<InvoiceQueryItemDto>(Math.Min(total, limit));

        // Sfera eksportuje IEnumVARIANT (VBA For Each dziala) - uzywamy IEnumerable.
        // Fallback: Element(i) w razie gdyby IEnumVARIANT nie byl wystawiony.
        try
        {
            int seen = 0;
            foreach (dynamic dok in (System.Collections.IEnumerable)kolekcja)
            {
                if (seen >= fetchCap || items.Count >= limit) break;
                seen++;
                try
                {
                    var mapped = MapDokumentToQueryItem(dok);
                    if (typeFilter == null || mapped.Type == typeFilter)
                    {
                        items.Add(mapped);
                    }
                }
                catch (Exception mapEx)
                {
                    _logger.LogWarning(mapEx, "QueryInvoices: skip dokument (mapping error)");
                }
                finally
                {
                    try { dok.Zamknij(); } catch { /* best-effort cleanup */ }
                }
            }
        }
        catch (InvalidCastException)
        {
            _logger.LogInformation("QueryInvoices: IEnumerable nie dostepne - fallback na Element(i)");
            int take = Math.Min(total, fetchCap);
            for (int i = 0; i < take && items.Count < limit; i++)
            {
                dynamic dok = kolekcja.Element(i);
                try
                {
                    var mapped = MapDokumentToQueryItem(dok);
                    if (typeFilter == null || mapped.Type == typeFilter)
                    {
                        items.Add(mapped);
                    }
                }
                catch (Exception mapEx)
                {
                    _logger.LogWarning(mapEx, "QueryInvoices: skip dokument (mapping error)");
                }
                finally
                {
                    try { dok.Zamknij(); } catch { /* best-effort */ }
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Buduje SQL WHERE clause z białej listy pól. Klient nie podaje raw SQL —
    /// strony są escapeowane (single quote -&gt; double single quote), daty walidowane
    /// po regex YYYY-MM-DD.
    /// </summary>
    private static string BuildInvoiceQueryFilter(InvoiceQueryRequestDto r)
    {
        var clauses = new List<string>();

        // Type filter NIE w SQL: dok_NumerPelny to atrybut COM (computed), nie kolumna w
        // dok__Dokument - SQL "LIKE 'FS %'" zwraca 0x80040E14. Filtrujemy client-side
        // (po MapDokumentToQueryItem) na bazie response.number.

        // Daty - dok_DataWyst (krotka nazwa kolumny w Subiekt GT).
        if (IsValidIsoDate(r.From)) clauses.Add($"dok_DataWyst >= '{r.From}'");
        if (IsValidIsoDate(r.To))   clauses.Add($"dok_DataWyst <= '{r.To}'");

        if (!string.IsNullOrWhiteSpace(r.NotesContains))
            clauses.Add($"dok_Uwagi LIKE '%{EscapeSqlLiteral(r.NotesContains)}%'");

        if (!string.IsNullOrWhiteSpace(r.Nip))
            clauses.Add($"dok_NabKodSlownik = '{EscapeSqlLiteral(r.Nip)}'");

        return clauses.Count == 0 ? "dok_Id > 0" : string.Join(" AND ", clauses);
    }

    private static bool IsValidIsoDate(string? s) =>
        !string.IsNullOrWhiteSpace(s) && System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$");

    private static string EscapeSqlLiteral(string s) => s.Replace("'", "''");

    private InvoiceQueryItemDto MapDokumentToQueryItem(dynamic dok)
    {
        long subiektId = (long)dok.Identyfikator;
        string number = (string)dok.NumerPelny ?? "";
        string type = number.Split(' ', 2)[0]; // "FS 1/2026" -> "FS"

        // Pola czasem nieobecne dla niektórych typów - czytamy defensywnie.
        DateTimeOffset? issueDate = TryGetDate(dok, "DataWystawienia");
        long? contractorId = TryGetLong(dok, "KontrahentId");
        string? notes = TryGetString(dok, "Uwagi");
        decimal? gross = TryGetDecimal(dok, "WartoscBrutto")
            ?? TryGetDecimal(dok, "KwotaBrutto")
            ?? TryGetDecimal(dok, "KwotaDoZaplaty");
        decimal? net = TryGetDecimal(dok, "WartoscNetto");
        decimal? vat = TryGetDecimal(dok, "WartoscVat");

        // NIP/Nazwa kontrahenta - NabywcaInfo na SuDokument bywa pusty.
        // Lookup do Kontrahenci.Wczytaj(KontrahentId) jest niezbedny do uzyskania
        // pelnych metadanych. N+1 (200 lookupow per listing) ale Sfera dziala lokalnie.
        string? nip = null, nazwa = null;
        if (contractorId.HasValue)
        {
            try
            {
                dynamic kontr = Session.Kontrahenci.Wczytaj(contractorId.Value);
                try
                {
                    nip = TryGetString(kontr, "NIP");
                    nazwa = TryGetString(kontr, "Nazwa");
                }
                finally
                {
                    try { kontr.Zamknij(); } catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MapDokumentToQueryItem: Kontrahenci.Wczytaj({Id}) failed", contractorId.Value);
            }
        }

        return new InvoiceQueryItemDto(
            SubiektId: subiektId,
            Number: number,
            Type: type,
            IssueDate: issueDate?.ToString("yyyy-MM-dd"),
            ContractorId: contractorId,
            ContractorNip: nip,
            ContractorName: nazwa,
            NetAmount: net,
            VatAmount: vat,
            GrossAmount: gross,
            Notes: notes);
    }

    /// <summary>
    /// Mapuje dokument przez statyczny pomocnik (bez session lookup) - dla foreach gdzie
    /// nie chcemy ryzykowac N+1 obciaznia Sfery na duzych kolekcjach. Aktualnie nie uzywamy,
    /// ale zostawiamy dla przyszlosci.
    /// </summary>
    private static string? TryGetString(dynamic obj, string prop)
    {
        try { var v = obj.GetType().InvokeMember(prop, BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public, null, obj, null); return v?.ToString(); }
        catch { return null; }
    }
    private static long? TryGetLong(dynamic obj, string prop)
    {
        try { var v = obj.GetType().InvokeMember(prop, BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public, null, obj, null); return v == null ? null : Convert.ToInt64(v); }
        catch { return null; }
    }
    private static decimal? TryGetDecimal(dynamic obj, string prop)
    {
        try { var v = obj.GetType().InvokeMember(prop, BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public, null, obj, null); return v == null ? null : Convert.ToDecimal(v); }
        catch { return null; }
    }
    private static DateTimeOffset? TryGetDate(dynamic obj, string prop)
    {
        try { var v = obj.GetType().InvokeMember(prop, BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public, null, obj, null); return v == null ? null : new DateTimeOffset(Convert.ToDateTime(v)); }
        catch { return null; }
    }

    // -------------------------- Single FV lookup + retro PDF --------------------------

    public Task<InvoiceQueryItemDto?> FindInvoiceByIdAsync(long subiektId, CancellationToken ct)
    {
        return RunOnStaAsync<InvoiceQueryItemDto?>(() =>
        {
            try
            {
                dynamic dok = Session.SuDokumentyManager.WczytajDokument(subiektId);
                try
                {
                    return MapDokumentToQueryItem(dok);
                }
                finally
                {
                    try { dok.Zamknij(); } catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                // Sfera rzuca COMException dla nieistniejącego ID. Logujemy info bo
                // użytkownik może świadomie zapytać o zły ID; nie traktujemy jako błąd serwera.
                _logger.LogInformation(ex, "FindInvoiceById: dokument {Id} nie znaleziony", subiektId);
                return null;
            }
        }, ct);
    }

    public Task<byte[]?> GetInvoicePdfAsync(long subiektId, CancellationToken ct)
    {
        return RunOnStaAsync<byte[]?>(() =>
        {
            try
            {
                dynamic dok = Session.SuDokumentyManager.WczytajDokument(subiektId);
                try
                {
                    string? base64 = TryGeneratePdf(dok, subiektId);
                    return base64 == null ? null : Convert.FromBase64String(base64);
                }
                finally
                {
                    try { dok.Zamknij(); } catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "GetInvoicePdf: dokument {Id} nie znaleziony albo PDF padł", subiektId);
                return null;
            }
        }, ct);
    }

    // -------------------------- Invoices --------------------------

    public Task<InvoiceResponseDto> CreateInvoiceAsync(InvoiceRequestDto request, CancellationToken ct)
    {
        return RunOnStaAsync(() => CreateInvoiceCore(request), ct);
    }

    private InvoiceResponseDto CreateInvoiceCore(InvoiceRequestDto request)
    {
        // Anti-duplicate: jesli w Subiekcie juz jest FS z tym external_reference w Uwagach,
        // odmow wystawienia. Idempotency-Key cache w Bridge zalapie powtorzony request z tym
        // samym kluczem, ale jak klient wyśle ten sam payload pod innym kluczem (np. retry
        // z innym job ID, debug curl) - bez tej kontroli powstanie duplikat w ksiegowosci.
        var existingId = FindExistingInvoiceByReference(request.ExternalReference, "FS");
        if (existingId.HasValue)
        {
            dynamic existing = Session.SuDokumentyManager.WczytajDokument(existingId.Value);
            try
            {
                throw new DuplicateInvoiceException(
                    existingId.Value,
                    (string)existing.NumerPelny ?? "",
                    request.ExternalReference);
            }
            finally
            {
                try { existing.Zamknij(); } catch { /* cleanup */ }
            }
        }

        dynamic fs = Session.SuDokumentyManager.DodajFS();
        try
        {
            fs.LiczonyOdCenBrutto = true;

            long contractorId = ResolveOrCreateContractor(request.Contractor);
            fs.KontrahentId = contractorId;

            if (!request.Contractor.IsPerson)
            {
                fs.FormaDokumentu = 1; // FV firmowa
            }

            // Magazyn na FS ustawiamy PER POZYCJA (SuPozycja.MagazynId), NIE na dokumencie.
            // SuDokument.MagazynNadawczyId jest dla MM (przesuniec miedzymagazynowych),
            // dla FS rzuca COM NotImplementedException 0x80004001.
            int? perLineWarehouseId = request.WarehouseSubiektId;
            foreach (var line in request.Lines)
            {
                AddLineToDocument(fs, line.Ean, line.NameFallback, line.Quantity, line.Unit, line.UnitPriceGross, perLineWarehouseId);
            }

            if (request.Shipping.Include)
            {
                AddShippingLineToDocument(fs, request.Shipping);
            }

            ApplyPayment(fs, request.Payment);

            // Rozliczony = true SUCK ujemny efekt dla PlatnoscKredyt: Sfera widzi
            // PlatnoscPrzelewKwota=0 + Rozliczony=true i AUTOMATYCZNIE konwertuje
            // PlatnoscKredyt na PlatnoscPrzelew (z dokumentacji Sfery
            // SuDokument_PlatnoscPrzelewKwota.htm). FV ma wtedy "Zaplacono przelewem"
            // zamiast wybranej formy ("Allegro Pay").
            //
            // Dla kredytu kupieckiego (Allegro Pay) Rozliczony=false - Allegro splaci
            // kredyt po terminie. Dla Przelew/Gotowka/Karta - Rozliczony=true gdy
            // is_settled (klient zaplacil bezposrednio).
            bool isCredit = string.Equals(request.Payment.Attribute, "PlatnoscKredyt", StringComparison.Ordinal)
                || string.Equals(request.Payment.Attribute, "PlatnoscRaty", StringComparison.Ordinal);
            fs.Rozliczony = isCredit ? false : request.Payment.IsSettled;
            fs.Uwagi = request.Notes ?? string.Empty;

            fs.Zapisz();

            long subiektId = ToInt64(fs.Identyfikator);
            string number = (string)fs.NumerPelny;
            DateTimeOffset issuedAt = DateTimeOffset.UtcNow;
            _lastInvoiceAt = issuedAt;

            string? pdfBase64 = TryGeneratePdf(fs, subiektId);

            // Net/VAT po zapisaniu - Sfera wylicza bo LiczonyOdCenBrutto=true.
            // Brak WartoscBrutto po Zapisz() to zaskoczenie - FV jest w bazie ale czytanie pól
            // failuje (zmiana nazw w wersji Subiekta? błąd COM?). Lepszy hard fail niż zwracać
            // Laravel-owi totals z requestu, bo Subiekt mógł wyliczyć inaczej (mieszane VAT,
            // zaokrąglenia per pozycja).
            var totals = ReadDocumentTotalsOrThrow(fs, subiektId, number);

            return new InvoiceResponseDto(
                Id: $"sub_{subiektId}",
                SubiektId: subiektId,
                Number: number,
                IssuedAt: issuedAt,
                ContractorSubiektId: contractorId,
                Totals: totals,
                PdfUrl: null,
                PdfBase64: pdfBase64);
        }
        finally
        {
            TryClose(fs);
        }
    }

    public Task<InvoiceResponseDto> CreateCorrectionAsync(
        long sourceSubiektId,
        InvoiceCorrectionRequestDto request,
        CancellationToken ct)
    {
        return RunOnStaAsync(() => CreateCorrectionCore(sourceSubiektId, request), ct);
    }

    public Task<InvoiceResponseDto> CreateReceiptAsync(ReceiptIssueRequestDto request, CancellationToken ct)
    {
        return RunOnStaAsync(() => CreateReceiptCore(request), ct);
    }

    private InvoiceResponseDto CreateReceiptCore(ReceiptIssueRequestDto request)
    {
        // PZ - Przyjęcie Zewnętrzne. Dokument magazynowy (zwiększa stan).
        // Dostawca (request.Supplier) jest kontrahentem; magazyn = MagazynOdbiorczyId
        // (do którego trafia towar). Pozycje używają ceny zakupu z OrderItem.purchase_price.
        dynamic pz = Session.SuDokumentyManager.DodajPZ();
        try
        {
            pz.LiczonyOdCenBrutto = true;

            // MagazynOdbiorczyId opcjonalne - gdy null, Sfera użyje magazynu z sesji
            // operatora (Subiekt.MagazynId), co dla typowego setup'u z 1 magazynem
            // jest tym czego chcemy.
            if (request.WarehouseSubiektId is int receiptWarehouseId)
            {
                pz.MagazynOdbiorczyId = receiptWarehouseId;
            }

            // Find-or-create kontrahenta (dostawcy). Symbol = "SUPPLIER-{id}" deterministyczny.
            long contractorId = ResolveOrCreateContractor(request.Supplier);
            pz.KontrahentId = contractorId;

            // Powiązanie z FS jeśli istnieje (workflow: PZ przed FS = sourceSubiektId null;
            // PZ po FS = sourceSubiektId ustawione, Subiekt linkuje dokumenty).
            if (request.SourceInvoiceSubiektId.HasValue)
            {
                TrySet(pz, "DoDokumentuId", (int) request.SourceInvoiceSubiektId.Value);
            }

            foreach (var line in request.Lines)
            {
                AddLineToDocument(pz, line.Ean, line.NameFallback, line.Quantity, line.Unit, line.UnitPriceGross);
            }

            pz.Uwagi = request.Notes ?? string.Empty;

            pz.Zapisz();

            long subiektId = ToInt64(pz.Identyfikator);
            string number = (string)pz.NumerPelny;
            DateTimeOffset issuedAt = DateTimeOffset.UtcNow;
            _lastInvoiceAt = issuedAt;

            string? pdfBase64 = TryGeneratePdf(pz, subiektId);

            // PZ to dokument magazynowy - WartoscBrutto opcjonalnie (nie wszystkie wersje
            // Subiekta wystawiają to pole na PZ). Jeśli brak - liczymy z lines payload.
            decimal? grossOpt = TryReadDecimal(pz, "WartoscBrutto");
            decimal gross = grossOpt ?? request.Lines.Sum(l => l.UnitPriceGross * l.Quantity);

            return new InvoiceResponseDto(
                Id: $"sub_{subiektId}",
                SubiektId: subiektId,
                Number: number,
                IssuedAt: issuedAt,
                ContractorSubiektId: contractorId,
                Totals: new InvoiceTotalsDto(Net: null, Vat: null, Gross: gross),
                PdfUrl: null,
                PdfBase64: pdfBase64);
        }
        finally
        {
            TryClose(pz);
        }
    }

    private InvoiceResponseDto CreateCorrectionCore(long sourceSubiektId, InvoiceCorrectionRequestDto request)
    {
        dynamic kfs = request.SourceIsExternal
            ? Session.SuDokumentyManager.DodajKFSn()
            : Session.SuDokumentyManager.DodajKFS();
        try
        {
            kfs.LiczonyOdCenBrutto = true;

            if (!request.SourceIsExternal)
            {
                kfs.DoDokumentuId = (int)sourceSubiektId;
            }
            else
            {
                if (!string.IsNullOrEmpty(request.SourceInvoiceNumber))
                {
                    kfs.DoDokumentuNumerPelny = request.SourceInvoiceNumber;
                }
                if (!string.IsNullOrEmpty(request.SourceInvoiceDate))
                {
                    kfs.DoDokumentuDataWystawienia = DateTime.Parse(request.SourceInvoiceDate);
                }
            }

            foreach (var line in request.Lines)
            {
                AddLineToDocument(
                    document: kfs,
                    ean: line.Ean,
                    name: line.NameFallback,
                    quantity: line.QuantityChange, // ujemna dla zwrotów
                    unit: line.Unit,
                    unitPriceGross: line.UnitPriceGross);
            }

            kfs.Uwagi = $"Korekta: {request.Reason} | ref: {request.ExternalReference}";

            kfs.Zapisz();

            long subiektId = ToInt64(kfs.Identyfikator);
            string number = (string)kfs.NumerPelny;
            var issuedAt = DateTimeOffset.UtcNow;
            _lastInvoiceAt = issuedAt;

            string? pdfBase64 = TryGeneratePdf(kfs, subiektId);

            // Patrz komentarz w CreateInvoiceCore - hard fail przy braku totals.
            var totals = ReadDocumentTotalsOrThrow(kfs, subiektId, number);

            return new InvoiceResponseDto(
                Id: $"sub_{subiektId}",
                SubiektId: subiektId,
                Number: number,
                IssuedAt: issuedAt,
                ContractorSubiektId: 0,
                Totals: totals,
                PdfUrl: null,
                PdfBase64: pdfBase64);
        }
        finally
        {
            TryClose(kfs);
        }
    }

    /// <summary>
    /// Odczytuje totals (net/vat/gross) z zapisanego dokumentu. Hard fail jeśli WartoscBrutto
    /// nieczytelne - FV jest w bazie Subiekta ale Bridge nie może wiarygodnie raportować kwot.
    /// Lepszy 5xx niż zwracać Laravelowi nieprawdziwe sumy z request payloadu (Subiekt mógł
    /// wyliczyć inaczej dla mieszanych stawek VAT lub zaokrągleń per pozycja).
    /// </summary>
    private InvoiceTotalsDto ReadDocumentTotalsOrThrow(dynamic document, long subiektId, string number)
    {
        decimal? grossOpt = TryReadDecimal(document, "WartoscBrutto");
        if (grossOpt is not decimal gross)
        {
            _logger.LogError(
                "Sfera nie zwróciła WartoscBrutto po Zapisz() dla {Number} (subiekt_id={SubiektId}). " +
                "FV jest w bazie Subiekta - sprawdź ręcznie.", number, subiektId);
            throw new InvalidOperationException(
                $"Nie można odczytać WartoscBrutto dla zapisanego dokumentu {number}. " +
                "Możliwa zmiana nazwy pola w wersji Subiekta. FV jest w bazie - wymaga ręcznego sprawdzenia.");
        }

        decimal? net = TryReadDecimal(document, "WartoscNetto");
        decimal? vat = TryReadDecimal(document, "WartoscPodatku");

        // Net/Vat są pomocnicze (Subiekt sam wylicza, my je tylko persystujemy w Laravel
        // dla audytu). Brak nie zatrzymuje flow - zostawiamy null.
        return new InvoiceTotalsDto(net, vat, gross);
    }

    // -------------------------- Lookup --------------------------

    public Task<ProductDto?> FindProductByEanAsync(string ean, CancellationToken ct)
    {
        return RunOnStaAsync<ProductDto?>(() =>
        {
            string symbol = ean;
            if (!(bool)Session.Towary.Istnieje(symbol))
            {
                return null;
            }

            dynamic towar = Session.Towary.Wczytaj(symbol);
            try
            {
                return new ProductDto(
                    SubiektId: ToInt64(towar.Identyfikator),
                    Symbol: (string)towar.Symbol,
                    Ean: ean,
                    Name: (string)towar.Nazwa,
                    VatRate: TryReadDecimal(towar, "VatStawka") ?? 23m,
                    Unit: TryReadString(towar, "JmZakupu") ?? TryReadString(towar, "JmSprzedazy") ?? "szt.",
                    IsActive: true);
            }
            finally
            {
                TryClose(towar);
            }
        }, ct);
    }

    public Task<ContractorDto?> FindContractorByNipAsync(string nip, CancellationToken ct)
    {
        return RunOnStaAsync<ContractorDto?>(() =>
        {
            string symbol = nip;
            if (!(bool)Session.Kontrahenci.Istnieje(symbol))
            {
                return null;
            }

            dynamic kh = Session.Kontrahenci.Wczytaj(symbol);
            try
            {
                return new ContractorDto(
                    IsPerson: TryReadBool(kh, "Osoba") ?? false,
                    Symbol: (string)kh.Symbol,
                    Nip: TryReadString(kh, "NIP"),
                    Name: (string)kh.Nazwa,
                    FullName: TryReadString(kh, "NazwaPelna"),
                    FirstName: null,
                    LastName: null,
                    Email: TryReadString(kh, "AdresEMail"),
                    Address: new AddressDto(
                        Street: TryReadString(kh, "Ulica") ?? "",
                        PostCode: TryReadString(kh, "KodPocztowy") ?? "",
                        City: TryReadString(kh, "Miejscowosc") ?? "",
                        CountryCode: TryReadString(kh, "Kraj") ?? "PL"));
            }
            finally
            {
                TryClose(kh);
            }
        }, ct);
    }

    public Task<object?> InvokeRawAsync(string method, IReadOnlyList<object?> args, CancellationToken ct)
    {
        return RunOnStaAsync<object?>(() =>
        {
            // Dot-navigated method call: "Magazyny.Liczba" -> Session.Magazyny.Liczba(args)
            var parts = method.Split('.');
            object current = Session;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current = current.GetType().InvokeMember(parts[i],
                    BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public,
                    null, current, Array.Empty<object>())!;
            }

            string lastPart = parts[^1];
            return current.GetType().InvokeMember(lastPart,
                BindingFlags.InvokeMethod | BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public,
                null, current, args.ToArray());
        }, ct);
    }

    // -------------------------- Building blocks --------------------------

    /// <summary>
    /// Find-or-create kontrahenta po Symbolu (NIP dla firm, custom dla osób).
    /// Zwraca Identyfikator kontrahenta w Subiekcie.
    /// </summary>
    private long ResolveOrCreateContractor(ContractorDto c)
    {
        string symbol = c.Symbol;

        // Istnieje: UPDATE danych z biezacego payload (klient mogl sie przeprowadzic
        // / zmienic nazwisko - reuse symbol/Identyfikator, ale ksiegowosc oczekuje
        // aktualnych danych na FV). Update tylko gdy widoczna roznica - mniej write'ow.
        if ((bool)Session.Kontrahenci.Istnieje(symbol))
        {
            dynamic existing = Session.Kontrahenci.Wczytaj(symbol);
            try
            {
                long id = ToInt64(existing.Identyfikator);
                UpdateContractorIfChanged(existing, c);
                return id;
            }
            finally
            {
                TryClose(existing);
            }
        }

        dynamic kh = Session.Kontrahenci.Dodaj();
        try
        {
            kh.Osoba = c.IsPerson;
            kh.Symbol = symbol;
            ApplyContractorFields(kh, c);
            kh.Zapisz();
            return ToInt64(kh.Identyfikator);
        }
        finally
        {
            TryClose(kh);
        }
    }

    /// <summary>
    /// Update istniejacego kontrahenta wartosciami z DTO. Sprawdza czy cos sie
    /// zmienilo zeby uniknac niepotrzebnego Zapisz() (audit trail w Subiekcie
    /// + konflikty miedzy rownoleglymi sesjami).
    /// </summary>
    private static void UpdateContractorIfChanged(dynamic existing, ContractorDto c)
    {
        bool changed = false;
        changed |= TryUpdate(existing, "Nazwa", c.Name);
        changed |= TryUpdate(existing, "NazwaPelna", c.FullName ?? c.Name);
        changed |= TryUpdate(existing, "Miejscowosc", c.Address.City);
        changed |= TryUpdate(existing, "KodPocztowy", c.Address.PostCode);
        changed |= TryUpdate(existing, "Ulica", c.Address.Street);
        if (!string.IsNullOrEmpty(c.Email))
        {
            changed |= TryUpdate(existing, "AdresEMail", c.Email);
        }
        if (!string.IsNullOrEmpty(c.Nip))
        {
            changed |= TryUpdate(existing, "NIP", c.Nip);
        }

        if (changed)
        {
            try { existing.Zapisz(); } catch { /* read-only contractor (rzadkie) - skip */ }
        }
    }

    /// <summary>Ustawia pola na nowo tworzonym kontrahencie (Symbol musi byc juz set).</summary>
    private static void ApplyContractorFields(dynamic kh, ContractorDto c)
    {
        kh.Nazwa = c.Name;
        kh.NazwaPelna = c.FullName ?? c.Name;

        if (!string.IsNullOrEmpty(c.Nip))
        {
            kh.NIP = c.Nip;
        }

        kh.Miejscowosc = c.Address.City;
        kh.KodPocztowy = c.Address.PostCode;
        kh.Ulica = c.Address.Street;

        if (!string.IsNullOrEmpty(c.Email))
        {
            TrySet(kh, "AdresEMail", c.Email);
        }
    }

    /// <summary>
    /// Set property na ComObject jezeli nowa wartosc rozni sie od starej.
    /// Zwraca true gdy faktycznie ustawiono. Pomija set gdy nowa wartosc null/empty.
    /// </summary>
    private static bool TryUpdate(dynamic obj, string prop, string? newValue)
    {
        if (string.IsNullOrEmpty(newValue))
        {
            return false;
        }

        try
        {
            var current = obj.GetType().InvokeMember(prop,
                BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public,
                null, obj, null);
            string currentStr = current?.ToString() ?? string.Empty;
            if (string.Equals(currentStr, newValue, StringComparison.Ordinal))
            {
                return false;
            }

            obj.GetType().InvokeMember(prop,
                BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.Public,
                null, obj, new object[] { newValue });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Szuka istniejacego dokumentu w Subiekcie po external_reference w polu Uwagi.
    /// Zwraca subiekt_id najnowszego pasujacego dokumentu typu zgodnego z typePrefix
    /// (np. "FS"), lub null gdy brak. SQL LIKE skanuje dok_Uwagi - dla typowej bazy
    /// (kilkadziesiat tysiecy FS) szybkie. Sfera dodatkowo filtruje po magazynie
    /// operatora.
    /// </summary>
    private long? FindExistingInvoiceByReference(string externalReference, string typePrefix)
    {
        if (string.IsNullOrWhiteSpace(externalReference))
        {
            return null;
        }

        var escaped = externalReference.Replace("'", "''");
        var filter = $"dok_Uwagi LIKE '%{escaped}%'";

        try
        {
            dynamic kolekcja = Session.SuDokumentyManager.OtworzKolekcje(filter, "dok_Id DESC");
            int total = Convert.ToInt32(kolekcja.Liczba);
            if (total == 0)
            {
                return null;
            }

            foreach (dynamic dok in (System.Collections.IEnumerable)kolekcja)
            {
                try
                {
                    string number = (string)dok.NumerPelny ?? "";
                    if (number.StartsWith(typePrefix + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        return ToInt64(dok.Identyfikator);
                    }
                }
                finally
                {
                    try { dok.Zamknij(); } catch { /* cleanup */ }
                }
            }
        }
        catch (Exception ex)
        {
            // Anti-duplicate check zawiodl (np. Sfera SQL error). Logujemy i kontynuujemy
            // jakby duplikatu nie bylo - lepiej miec ewentualny duplikat niz zablokowac
            // legitymowane wystawianie FV.
            _logger.LogWarning(ex, "FindExistingInvoiceByReference failed for ref='{Ref}'; assuming no duplicate", externalReference);
        }

        return null;
    }

    private void AddLineToDocument(dynamic document, string? ean, string name, int quantity, string unit, decimal unitPriceGross, int? warehouseId = null)
    {
        dynamic position;
        bool isTowar = false;

        if (!string.IsNullOrEmpty(ean))
        {
            // EAN podany - MUSI istnieć w Subiekcie. Cichy fallback do "usługi jednorazowej"
            // maskowałby brak synchronizacji towaru: FS/PZ by się wystawił, ale magazyn
            // by się nie ruszył dla tej pozycji - rozjazd księgowy.
            if (!(bool)Session.Towary.Istnieje(ean))
            {
                throw new MissingProductException(ean);
            }

            dynamic towar = Session.Towary.Wczytaj(ean);
            try
            {
                position = document.Pozycje.Dodaj(towar);
                isTowar = true;
            }
            finally
            {
                TryClose(towar);
            }
        }
        else
        {
            // EAN pusty/null - świadomie "usługa jednorazowa" (np. wysyłka, korekta
            // kwotowa zwrotu pieniężnego z InvoiceCorrectionIssuer).
            position = document.Pozycje.DodajUslugeJednorazowa();
            position.UslJednNazwa = name;
        }

        // SuPozycja.MagazynId - magazyn z ktorego towar wychodzi (dla FS) lub do ktorego
        // wchodzi (dla PZ). Ustawiamy tylko dla towarow (nie dla uslug jednorazowych
        // ktore nie maja stanu magazynowego).
        if (isTowar && warehouseId.HasValue)
        {
            TrySet(position, "MagazynId", warehouseId.Value);
        }

        position.IloscJm = quantity;
        position.Jm = string.IsNullOrEmpty(unit) ? "szt." : unit;
        position.CenaBruttoPrzedRabatem = (double)unitPriceGross;
    }

    private void AddShippingLineToDocument(dynamic document, ShippingDto shipping)
    {
        dynamic position = document.Pozycje.DodajUslugeJednorazowa();
        position.UslJednNazwa = shipping.Name;
        position.IloscJm = 1;
        position.Jm = "szt.";
        position.CenaBruttoPrzedRabatem = (double)shipping.UnitPriceGross;
    }

    private void ApplyPayment(dynamic document, PaymentDto payment)
    {
        // Sfera nie ma *Id dla wszystkich form: PlatnoscGotowka i PlatnoscPrzelew mają tylko Kwota.
        // PlatnoscKredyt/PlatnoscKarta/PlatnoscRaty/PlatnoscKredyt mają *Id (słowniki sl_FormaPlatnosci itd.).
        // SetCom (NIE TrySet) gwarantuje że literówka w atrybucie da 500 zamiast cicho zapisać FV bez formy.
        if (payment.MethodSubiektId.HasValue)
        {
            SetCom(document, payment.Attribute + "Id", payment.MethodSubiektId.Value);
        }
        SetCom(document, payment.Attribute + "Kwota", (double)payment.Amount);
    }

    /// <summary>
    /// Ustawia property na obiekcie COM przez reflection. Wyjątek propaguje
    /// (w przeciwieństwie do <see cref="TrySet"/> które połyka błąd).
    /// Używaj dla pól krytycznych biznesowo (np. forma płatności).
    /// </summary>
    private static void SetCom(object target, string propName, object value)
    {
        target.GetType().InvokeMember(propName,
            BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.Public,
            null, target, new[] { value });
    }

    /// <summary>
    /// Drukuje dokument do PDF i zwraca base64. Null gdy generowanie się nie powiedzie -
    /// FV jest już wystawiona, PDF dorabiamy później przez retry/manual.
    /// </summary>
    private string? TryGeneratePdf(dynamic document, long subiektId)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"sub_{subiektId}_{Guid.NewGuid():N}.pdf");
        try
        {
            // 0 = gtaTypPlikuPDF (z SubiektDokumentEnum: pierwszy enum value to PDF)
            if (_options.PdfTemplateId.HasValue)
            {
                document.DrukujDoPlikuWgWzorca(_options.PdfTemplateId.Value, tempPath, 0);
            }
            else
            {
                document.DrukujDoPliku(tempPath, 0);
            }

            byte[] bytes = File.ReadAllBytes(tempPath);
            return Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF generation failed for subiekt_id={SubiektId}, FV jest wystawiona ale bez PDF",
                subiektId);
            return null;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
        }
    }

    // -------------------------- Helpers (reflection na COM IDispatch) --------------------------

    private static long ToInt64(dynamic value)
    {
        // Sfera Identyfikator: czasem int, czasem long w zależności od wersji Subiekta.
        return value switch
        {
            int i => i,
            long l => l,
            _ => Convert.ToInt64(value),
        };
    }

    private static void TrySet(object target, string propName, object value)
    {
        try
        {
            target.GetType().InvokeMember(propName,
                BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.Public,
                null, target, new[] { value });
        }
        catch (Exception)
        {
            // Property może nie istnieć w tej wersji Subiekta - silently skip.
        }
    }

    private static decimal? TryReadDecimal(object target, string propName)
    {
        try
        {
            object? raw = target.GetType().InvokeMember(propName,
                BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public,
                null, target, Array.Empty<object>());
            return raw is null ? null : Convert.ToDecimal(raw);
        }
        catch { return null; }
    }

    private static bool? TryReadBool(object target, string propName)
    {
        try
        {
            object? raw = target.GetType().InvokeMember(propName,
                BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public,
                null, target, Array.Empty<object>());
            return raw is null ? null : Convert.ToBoolean(raw);
        }
        catch { return null; }
    }

    private static string? TryReadString(object target, string propName)
    {
        try
        {
            object? raw = target.GetType().InvokeMember(propName,
                BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public,
                null, target, Array.Empty<object>());
            return raw?.ToString();
        }
        catch { return null; }
    }

    private int _consecutiveCloseFailures = 0;
    private const int CloseFailureThreshold = 3;

    private void TryClose(dynamic obj)
    {
        bool zamknijFailed = false;

        try
        {
            obj.Zamknij();
            _consecutiveCloseFailures = 0;
        }
        catch (Exception ex)
        {
            zamknijFailed = true;
            _consecutiveCloseFailures++;
            _logger.LogWarning(ex,
                "Sfera Zamknij() rzucił ({Count}/{Threshold}). Sesja może być w nieczystym stanie.",
                _consecutiveCloseFailures, CloseFailureThreshold);
        }

        try { Marshal.ReleaseComObject(obj); } catch { /* COM RCW already released - bezpieczne */ }

        // Po N pod rząd nieudanych Zamknij() resetujemy sesję - kolejny dokument w skażonej
        // sesji mógłby dziedziczyć stan poprzedniego (udokumentowane jako nieprzewidywalne
        // zachowanie Sfery).
        if (zamknijFailed && _consecutiveCloseFailures >= CloseFailureThreshold)
        {
            _logger.LogError(
                "{Threshold} consecutive Zamknij() failures - resetuję sesję Sfery.",
                CloseFailureThreshold);
            ResetSessionOnSta();
            _consecutiveCloseFailures = 0;
        }
    }

    /// <summary>
    /// Resetuje sesję. WAŻNE: musi być wywołane na STA threadzie (z STA worker loop'a).
    /// Bezpośrednie wywołanie z innego threada zrobiłoby Marshal.ReleaseComObject z MTA
    /// = potencjalny race / wyjątek.
    /// </summary>
    private void ResetSessionOnSta()
    {
        try { _subiekt?.Zakoncz(); } catch { }
        try { if (_subiekt is not null) Marshal.ReleaseComObject(_subiekt); } catch { }
        _subiekt = null;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        // Sygnalizuj STA workerowi że ma się zamknąć.
        // ResetSessionOnSta() wykona się jako ostatnia akcja w workerze.
        _workQueue.CompleteAdding();

        // Czekamy max 5 sekund na zamknięcie sesji Sfery.
        if (!_staThread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Sfera STA worker did not exit cleanly within 5s");
        }

        _workQueue.Dispose();
        return ValueTask.CompletedTask;
    }
}
