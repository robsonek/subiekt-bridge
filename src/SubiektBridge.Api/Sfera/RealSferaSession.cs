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

    // -------------------------- SQL Query (read-only) --------------------------

    public Task<QueryResultDto> QueryAsync(string sql, int maxRows, CancellationToken ct)
    {
        // SQL przez bezposredni SqlConnection (nie Sfera) - Sfera.Baza.PolaczenieAdoNet
        // zwraca SqlConnection wrapped jako ComObject i dynamic binder nie widzi metod
        // (CreateCommand etc nie sa bindable). Connection string budujemy z opcji
        // Subiekta - te same dane co Sfera uzywa do logowania.
        return Task.Run(() =>
        {
            var connStr = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = _options.Server,
                InitialCatalog = _options.Database,
                UserID = _options.DbUser,
                Password = _options.DbPassword,
                TrustServerCertificate = true,
                ConnectTimeout = 10,
            }.ToString();

            using var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;

            using var reader = cmd.ExecuteReader();
            var columns = new List<string>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            var rows = new List<IReadOnlyList<object?>>();
            int count = 0;
            while (reader.Read())
            {
                if (count >= maxRows)
                {
                    return new QueryResultDto(columns, rows, true);
                }
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
                count++;
            }

            return new QueryResultDto(columns, rows, false);
        }, ct);
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
        // Dostawca (request.Supplier) jest kontrahentem.
        // Magazyn ustawiamy PER POZYCJA (SuPozycja.MagazynId), NIE na dokumencie -
        // MagazynOdbiorczyId/NadawczyId są atrybutami dla MM (przesunięć miedzymagazynowych)
        // wg pomocy Sfery. Dla PZ ustawienie pz.MagazynOdbiorczyId rzuca NotImplemented
        // z ComObject (sprawdzone empirycznie v0.7.29). Subiekt sam wpisze dok_MagId
        // z magazynu pierwszej pozycji.
        dynamic pz = Session.SuDokumentyManager.DodajPZ();
        try
        {
            // PZ liczy od cen netto (default Sfery). NIE ustawiamy LiczonyOdCenBrutto=true -
            // dla PZ rzuca 0x80004005 (E_FAIL) na Zapisz(). Sprawdzone empirycznie + spojne
            // z istniejacymi PZ klienta (ob_CenaNetto wpisane wprost, ob_CenaBrutto wyliczone).

            // Find-or-create kontrahenta (dostawcy).
            long contractorId = ResolveOrCreateContractor(request.Supplier);

            // KLUCZOWE dla dokumentow MAGAZYNOWYCH (PZ, WZ, MM): uzywamy OdbiorcaId, NIE
            // KontrahentId. Pomoc Sfery wprost: "Dla dokumentow magazynowych pole to zawiera
            // identyfikator kontrahenta, dla ktorego ma byc dokument magazynowy wystawiony.
            // Z tego wzgledu dla dokumentow magazynowych nalezy poslugiwac sie atrybutem
            // OdbiorcaId a nie KontrahentId." (SuDokument_OdbiorcaId.htm). Setowanie
            // KontrahentId na PZ powodowalo 0x80004005 na Zapisz().
            pz.OdbiorcaId = contractorId;

            // NumerOryginalny - login kupujacego z Allegro (dla dropshippingu identyfikuje
            // dla kogo PZ wystawione). Sfera limituje do 30 znakow + nie dopuszcza NULL.
            if (!string.IsNullOrEmpty(request.OriginalNumber))
            {
                string numerOryg = request.OriginalNumber.Length > 30
                    ? request.OriginalNumber.Substring(0, 30)
                    : request.OriginalNumber;
                TrySet(pz, "NumerOryginalny", numerOryg);
            }

            // Powiązanie z FS jeśli istnieje (workflow: PZ przed FS = sourceSubiektId null;
            // PZ po FS = sourceSubiektId ustawione, Subiekt linkuje dokumenty).
            if (request.SourceInvoiceSubiektId.HasValue)
            {
                TrySet(pz, "DoDokumentuId", (int) request.SourceInvoiceSubiektId.Value);
            }

            int? perLineWarehouseId = request.WarehouseSubiektId;
            foreach (var line in request.Lines)
            {
                AddLineToDocument(pz, line.Ean, line.NameFallback, line.Quantity, line.Unit, line.UnitPriceGross, perLineWarehouseId, useNetPrice: true, vatRate: line.VatRate);
            }

            pz.Uwagi = request.Notes ?? string.Empty;

            // Diagnostyka: log stanu PZ tuz przed Zapisz(). 0x80004005 z Sfery to ogolny
            // E_FAIL bez szczegolow (excepInfo gubione przez RuntimeBinder), wiec logujemy
            // co wgralismy zeby porownac z istniejacymi PZ klienta.
            try
            {
                int liczbaPozycji = 0;
                try { liczbaPozycji = (int)pz.Pozycje.Liczba; } catch { /* best effort */ }
                _logger.LogInformation(
                    "PZ przed Zapisz: KontrahentId={KontrahentId}, Pozycji={Liczba}, Uwagi.len={UwagiLen}, MagPerLine={Mag}, NetMode=true",
                    contractorId,
                    liczbaPozycji,
                    (request.Notes ?? "").Length,
                    perLineWarehouseId);
            }
            catch { /* logging best-effort */ }

            try
            {
                pz.Zapisz();
            }
            catch (Exception ex)
            {
                // Sfera zwraca opisy bledow przez IErrorInfo - dynamic binder czesto je traci.
                // Marshal.GetExceptionForHR z aktualnym error info pozwala odzyskac tekst.
                string detail = ex.Message;
                if (ex is COMException com)
                {
                    var resolved = Marshal.GetExceptionForHR(com.ErrorCode);
                    if (resolved != null && !string.IsNullOrEmpty(resolved.Message)
                        && resolved.Message != ex.Message)
                    {
                        detail = $"{ex.Message} | IErrorInfo: {resolved.Message}";
                    }
                    _logger.LogError(ex, "PZ.Zapisz() COM HRESULT=0x{HR:X8}: {Detail}", com.ErrorCode, detail);
                }
                else
                {
                    _logger.LogError(ex, "PZ.Zapisz() failed: {Detail}", detail);
                }
                throw;
            }

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
            if (!request.SourceIsExternal)
            {
                // Powiązanie ze źródłowym FS: NaPodstawie(int) - DispId 166. DoDokumentuId
                // jest read-only w Sferze (TargetParameterCountException przy SetProperty).
                // NaPodstawie automatycznie kopiuje pozycje z FS do KFS - modyfikujemy je
                // niżej przez IloscJmPoKorekcie zamiast dodawać własne ujemne pozycje.
                kfs.NaPodstawie((int)sourceSubiektId);
            }
            else
            {
                // KFSn = korekta do dokumentu nieistniejącego w bazie (zewnętrzny FS).
                // Tu nie ma pozycji do skopiowania, ręcznie podajemy referencję.
                if (!string.IsNullOrEmpty(request.SourceInvoiceNumber))
                {
                    SetComProperty(kfs, "DoDokumentuNumerPelny", request.SourceInvoiceNumber);
                }
                if (!string.IsNullOrEmpty(request.SourceInvoiceDate))
                {
                    SetComProperty(kfs, "DoDokumentuDataWystawienia", DateTime.Parse(request.SourceInvoiceDate));
                }
            }

            kfs.LiczonyOdCenBrutto = true;

            // Mapowanie request.Lines: dla kazdej linii z EAN szukamy pozycji w skopiowanych
            // pozycjach KFS i ustawiamy IloscJmPoKorekcie. KFS po NaPodstawie() NIE POZWALA
            // dodawac nowych pozycji (Pozycje.Dodaj/DodajUslugeJednorazowa rzucaja COMException).
            //
            // Linia z `Ean==null` to korekta uslugi jednorazowej (np. zwrot wysylki) - szukamy
            // pozycji z UslugaJednorazowa==true, opcjonalnie matchujac po nazwie (UslJednNazwa).
            // Pozycje bez matchu = blad walidacji - lepiej fail-fast niz wystawic KFS bez
            // prawidlowych korekt.
            var unmatched = new List<string>();
            foreach (var line in request.Lines)
            {
                if (request.SourceIsExternal)
                {
                    unmatched.Add($"line ean={line.Ean ?? "<null>"} name='{line.NameFallback}' qty={line.QuantityChange}");
                    continue;
                }

                bool matched;
                if (string.IsNullOrEmpty(line.Ean))
                {
                    matched = TryAdjustServicePosition(kfs, line.NameFallback, line.QuantityChange);
                }
                else
                {
                    matched = TryAdjustExistingPosition(kfs, line.Ean, line.QuantityChange);
                }

                if (!matched)
                {
                    unmatched.Add($"line ean={line.Ean ?? "<null>"} name='{line.NameFallback}' qty={line.QuantityChange}");
                }
            }

            if (unmatched.Count > 0)
            {
                throw new InvalidOperationException(
                    "KFS: nie udalo sie zmapowac " + unmatched.Count + " linii na pozycje skopiowane z FS. " +
                    "KFS po NaPodstawie() nie pozwala dodawac nowych pozycji - kazda linia musi pasowac " +
                    "do istniejacej pozycji FS po Towar.Identyfikator. Niezmapowane: " +
                    string.Join("; ", unmatched));
            }

            kfs.Uwagi = $"Korekta: {request.Reason} | ref: {request.ExternalReference}";

            // KFS payment: po NaPodstawie() Sfera defaultowo wpisuje
            // PlatnoscGotowkaKwota = total korekty (z SuDokument_PlatnoscGotowkaKwota.htm:
            // "Domyslnie wartosc maksymalna jest wstawiona w pole platnosc gotowka").
            // Dla zwrotow Allegro to mylace - klient otrzymal pieniadze przelewem,
            // a Subiekt pokazuje "Zaplacono gotowka". Zerujemy gotowke i ustawiamy
            // wlasciwy atrybut (PlatnoscPrzelewKwota / PlatnoscKartaKwota / ...).
            //
            // Pomijamy gdy Payment == null - zachowanie kompatybilne z legacy klientami
            // ktorzy nie wysylaja payment field.
            if (request.Payment is not null)
            {
                ApplyCorrectionPayment(kfs, request.Payment);
            }

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
        // 1. Lookup po NIP (firmy) - kontrahent moze juz byc w Subiekcie z innym Symbol-em
        // (klient utworzyl recznie, lub byl zaimportowany z innego systemu). NIE
        // nadpisujemy danych - zostawiamy adres wpisany w Subiekcie.
        if (!string.IsNullOrEmpty(c.Nip))
        {
            var byNip = FindContractorIdByNip(c.Nip);
            if (byNip.HasValue)
            {
                _logger.LogInformation("Contractor matched by NIP {Nip} -> kh_Id={Id}", c.Nip, byNip.Value);
                return byNip.Value;
            }
        }

        string symbol = c.Symbol;

        // 2. Istnieje po Symbolu: UPDATE danych z biezacego payload (klient mogl sie przeprowadzic
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
        var truncatedName = c.Name.Length > SubiektNazwaMaxLen ? c.Name[..SubiektNazwaMaxLen] : c.Name;
        changed |= TryUpdate(existing, "Nazwa", truncatedName);
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

    /// <summary>
    /// Lookup kh_Id po NIP. Subiekt trzyma NIP w adr__Ewid (TypAdresu=1, glowny adres)
    /// JOIN z kh__Kontrahent. Nie ma kolumny NIP w samym kh__Kontrahent.
    /// Zwraca pierwszy match (mogą być duplikaty - klient widzi sam i konsoliduje recznie).
    /// </summary>
    private long? FindContractorIdByNip(string nip)
    {
        try
        {
            var connStr = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = _options.Server,
                InitialCatalog = _options.Database,
                UserID = _options.DbUser,
                Password = _options.DbPassword,
                TrustServerCertificate = true,
                ConnectTimeout = 10,
            }.ToString();

            using var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT TOP 1 k.kh_Id
                FROM kh__Kontrahent k
                JOIN adr__Ewid a ON a.adr_IdObiektu = k.kh_Id AND a.adr_TypAdresu = 1
                WHERE a.adr_NIP = @nip
                ORDER BY k.kh_Id ASC";
            cmd.Parameters.AddWithValue("@nip", nip);
            cmd.CommandTimeout = 10;
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? null : Convert.ToInt64(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FindContractorIdByNip({Nip}) failed", nip);
            return null;
        }
    }

    /// <summary>Ustawia pola na nowo tworzonym kontrahencie (Symbol musi byc juz set).</summary>
    private const int SubiektNazwaMaxLen = 50;

    private static void ApplyContractorFields(dynamic kh, ContractorDto c)
    {
        kh.Nazwa = c.Name.Length > SubiektNazwaMaxLen ? c.Name[..SubiektNazwaMaxLen] : c.Name;
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
                    if (!number.StartsWith(typePrefix + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long candidateId = ToInt64(dok.Identyfikator);

                    // OtworzKolekcje czasem zwraca rekordy z dok__Dokument ktore zostaly
                    // soft-deleted/anulowane (Sfera nie filtruje per default). Weryfikujemy
                    // ze dokument NAPRAWDE istnieje przez WczytajDokument - jesli rzuca,
                    // skip (kolekcja zawiera "ducha", nie traktujemy jako duplikat).
                    try
                    {
                        dynamic verify = Session.SuDokumentyManager.WczytajDokument(candidateId);
                        try
                        {
                            return candidateId;
                        }
                        finally
                        {
                            try { verify.Zamknij(); } catch { /* cleanup */ }
                        }
                    }
                    catch (Exception verifyEx)
                    {
                        _logger.LogInformation(verifyEx, "Anti-duplicate: kolekcja zwrocila {Id} ale WczytajDokument padl - traktuje jako duch", candidateId);
                        continue;
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

    private void AddLineToDocument(dynamic document, string? ean, string name, int quantity, string unit, decimal unitPriceGross, int? warehouseId = null, bool useNetPrice = false, decimal vatRate = 23m)
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

        if (useNetPrice)
        {
            // PZ liczy od cen NETTO (LiczonyOdCenBrutto=true rzuca 0x80004005 na Zapisz()
            // dla PZ - sprawdzone empirycznie + istniejace PZ klienta maja ob_CenaNetto
            // ustawione, ob_CenaBrutto wyliczone z VatProc). Przeliczamy brutto -> netto
            // po VAT-cie z LineDto.
            decimal unitPriceNet = unitPriceGross / (1m + (vatRate / 100m));
            position.CenaNettoPrzedRabatem = (double)unitPriceNet;
        }
        else
        {
            position.CenaBruttoPrzedRabatem = (double)unitPriceGross;
        }
    }

    private void AddShippingLineToDocument(dynamic document, ShippingDto shipping)
    {
        dynamic position = document.Pozycje.DodajUslugeJednorazowa();
        position.UslJednNazwa = shipping.Name;
        position.IloscJm = 1;
        position.Jm = "szt.";
        position.CenaBruttoPrzedRabatem = (double)shipping.UnitPriceGross;
    }

    /// <summary>
    /// Po NaPodstawie() KFS ma skopiowane pozycje z FS - znajdujemy pozycję po Towar.Identyfikator
    /// (numeric tw_Id, stabilny match) i ustawiamy IloscJmPoKorekcie. Pozycje nieobjęte korektą
    /// zostają z defaultem. Zwraca true gdy match znaleziony.
    ///
    /// Strategie matchowania (kolejność):
    /// 1. EAN → tw_Id przez Towary.Wczytaj(ean), porównanie z poz.TowarId
    /// 2. Sequential: jeśli pozCount == 1 i to jedyny call dla tego KFS - bierz pozycję 1
    ///    (1:1 mapping w przypadkach single-item refund)
    /// </summary>
    private bool TryAdjustExistingPosition(dynamic document, string ean, int deltaQuantity)
    {
        int pozCount;
        try { pozCount = (int)document.Pozycje.Liczba; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryAdjustExistingPosition: nie moge odczytac Pozycje.Liczba");
            return false;
        }

        // Pre-resolve EAN -> tw_Id przez Session.Towary.Wczytaj.
        long? targetTowarId = null;
        try
        {
            if ((bool)Session.Towary.Istnieje(ean))
            {
                dynamic towar = Session.Towary.Wczytaj(ean);
                try { targetTowarId = ToInt64(towar.Identyfikator); }
                finally { TryClose(towar); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryAdjustExistingPosition: nie udalo sie zresolvowac EAN={Ean} -> tw_Id", ean);
        }

        _logger.LogInformation(
            "TryAdjustExistingPosition: szukam ean={Ean} (tw_Id={TwId}), delta={Delta}, pozCount={Count}",
            ean, targetTowarId?.ToString() ?? "<unresolved>", deltaQuantity, pozCount);

        // Sfera Pozycje.Element jest 1-indexed (COM Automation standard).
        for (int i = 1; i <= pozCount; i++)
        {
            dynamic? poz = null;
            try
            {
                try { poz = document.Pozycje.Element(i); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TryAdjustExistingPosition: Element({I}) rzucil", i);
                    continue;
                }
                if (poz == null) continue;

                // Diagnostyka: jakie property faktycznie sa dostepne na pozycji KFS po NaPodstawie.
                long? posTowarId = TryReadInt64(poz, "TowarId");
                long? posIdentyfikator = TryReadInt64(poz, "Identyfikator");
                int posIloscJm = -1;
                try { posIloscJm = Convert.ToInt32(poz.IloscJm); } catch { /* ignore */ }

                long? posTowarIdFromTowar = null;
                string? posSymbol = null, posEan = null;
                try
                {
                    dynamic towar = poz.Towar;
                    if (towar != null)
                    {
                        posTowarIdFromTowar = TryReadInt64(towar, "Identyfikator");
                        posSymbol = TryReadString(towar, "Symbol");
                        posEan = TryReadString(towar, "EAN");
                    }
                }
                catch { /* ignore - poz moze nie miec Towar (uslugowa) */ }

                _logger.LogInformation(
                    "  poz[{I}]: TowarId={TowarId}, Identyfikator={Ident}, Towar.Identyfikator={TI}, " +
                    "Towar.Symbol={Symbol}, Towar.EAN={Ean}, IloscJm={Qty}",
                    i,
                    posTowarId?.ToString() ?? "<null>",
                    posIdentyfikator?.ToString() ?? "<null>",
                    posTowarIdFromTowar?.ToString() ?? "<null>",
                    posSymbol ?? "<null>",
                    posEan ?? "<null>",
                    posIloscJm);

                // Match priority: TowarId via dispatch -> Towar.Identyfikator -> Symbol/EAN string
                bool match = false;
                if (targetTowarId.HasValue)
                {
                    match = posTowarId == targetTowarId
                         || posTowarIdFromTowar == targetTowarId;
                }
                if (!match)
                {
                    match = string.Equals(posSymbol, ean, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(posEan, ean, StringComparison.OrdinalIgnoreCase);
                }

                if (!match) continue;

                int newQty = posIloscJm >= 0 ? posIloscJm + deltaQuantity : 0;
                if (newQty < 0) newQty = 0;

                try
                {
                    SetComProperty(poz, "IloscJmPoKorekcie", newQty);
                    _logger.LogInformation("  poz[{I}]: IloscJmPoKorekcie ustawione na {NewQty}", i, newQty);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TryAdjustExistingPosition: SetProperty IloscJmPoKorekcie={NewQty} padlo", newQty);
                    return false;
                }
            }
            finally
            {
                // Pozycja KFS NIE jest dokumentem - nie ma Zamknij(), tylko zwalniamy COM RCW.
                if (poz != null)
                {
                    try { Marshal.ReleaseComObject((object)poz); } catch { /* RCW already released */ }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Korekta pozycji uslugi jednorazowej (np. zwrot wysylki w pelnym zwrocie zamowienia).
    /// FS dodaje wysylke przez Pozycje.DodajUslugeJednorazowa(), w KFS po NaPodstawie() ta
    /// pozycja jest skopiowana z UslugaJednorazowa==true. Nie ma Towar.Identyfikator/EAN,
    /// wiec match po nameHint (case-insensitive). Bez hinta - bierzemy jedyna pozycje
    /// uslugowa; jesli jest ich >1, zwracamy false (operator musi wystawic KFS recznie).
    ///
    /// QuantityChange jest ujemne (np. -1 zwraca 1 szt). Wynikowe IloscJmPoKorekcie =
    /// max(currentQty + change, 0).
    /// </summary>
    private bool TryAdjustServicePosition(dynamic document, string? nameHint, int quantityChange)
    {
        int pozCount;
        try { pozCount = (int)document.Pozycje.Liczba; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryAdjustServicePosition: nie moge odczytac Pozycje.Liczba");
            return false;
        }

        _logger.LogInformation(
            "TryAdjustServicePosition: szukam uslugi jednorazowej, nameHint='{NameHint}', delta={Delta}, pozCount={Count}",
            nameHint ?? "<null>", quantityChange, pozCount);

        // Faza 1: zbierz wszystkie pozycje uslugowe + ich nazwy. RCW pozostawiamy otwarte do
        // konca metody bo bedziemy potencjalnie ustawiac IloscJmPoKorekcie na trafionej pozycji.
        var serviceMatches = new List<(int Index, dynamic Position, string? UslJednNazwa, int IloscJm)>();
        for (int i = 1; i <= pozCount; i++)
        {
            dynamic? poz = null;
            try { poz = document.Pozycje.Element(i); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TryAdjustServicePosition: Element({I}) rzucil", i);
                continue;
            }
            if (poz == null) continue;

            bool isUsluga = false;
            try { isUsluga = (bool)poz.UslugaJednorazowa; } catch { /* atrybut moze brakowac */ }
            if (!isUsluga)
            {
                try { Marshal.ReleaseComObject((object)poz); } catch { /* ignore */ }
                continue;
            }

            string? uslName = TryReadString(poz, "UslJednNazwa");
            int iloscJm = -1;
            try { iloscJm = Convert.ToInt32(poz.IloscJm); } catch { /* ignore */ }

            _logger.LogInformation(
                "  poz[{I}] (usluga): UslJednNazwa='{Name}', IloscJm={Qty}", i, uslName ?? "<null>", iloscJm);

            serviceMatches.Add((i, poz, uslName, iloscJm));
        }

        try
        {
            if (serviceMatches.Count == 0)
            {
                _logger.LogWarning("TryAdjustServicePosition: brak pozycji uslugowych w KFS");
                return false;
            }

            // Match priority: dokladne (case-insensitive, trim) > prefix > pojedyncza pozycja.
            (int Index, dynamic Position, string? UslJednNazwa, int IloscJm)? chosen = null;

            if (!string.IsNullOrWhiteSpace(nameHint))
            {
                string hint = nameHint.Trim();
                chosen = serviceMatches.FirstOrDefault(m =>
                    !string.IsNullOrEmpty(m.UslJednNazwa) &&
                    string.Equals(m.UslJednNazwa.Trim(), hint, StringComparison.OrdinalIgnoreCase));

                if (chosen.HasValue && chosen.Value.Position == null)
                {
                    chosen = null;
                }

                if (!chosen.HasValue)
                {
                    chosen = serviceMatches.FirstOrDefault(m =>
                        !string.IsNullOrEmpty(m.UslJednNazwa) &&
                        m.UslJednNazwa.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (chosen.HasValue && chosen.Value.Position == null)
                    {
                        chosen = null;
                    }
                }
            }

            if (!chosen.HasValue && serviceMatches.Count == 1)
            {
                chosen = serviceMatches[0];
            }

            if (!chosen.HasValue)
            {
                _logger.LogWarning(
                    "TryAdjustServicePosition: nameHint='{Hint}' nie pasuje do zadnej z {Count} pozycji uslugowych",
                    nameHint ?? "<null>", serviceMatches.Count);
                return false;
            }

            var target = chosen.Value;
            int newQty = target.IloscJm >= 0 ? target.IloscJm + quantityChange : 0;
            if (newQty < 0) newQty = 0;

            try
            {
                SetComProperty(target.Position, "IloscJmPoKorekcie", newQty);
                _logger.LogInformation(
                    "  poz[{I}] (usluga '{Name}'): IloscJmPoKorekcie ustawione na {NewQty}",
                    target.Index, target.UslJednNazwa ?? "<null>", newQty);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "TryAdjustServicePosition: SetProperty IloscJmPoKorekcie={NewQty} padlo", newQty);
                return false;
            }
        }
        finally
        {
            foreach (var match in serviceMatches)
            {
                if (match.Position != null)
                {
                    try { Marshal.ReleaseComObject((object)match.Position); } catch { /* RCW already released */ }
                }
            }
        }
    }

    private static long? TryReadInt64(object target, string propName)
    {
        try
        {
            object? raw = target.GetType().InvokeMember(propName,
                BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public,
                null, target, Array.Empty<object>());
            return raw is null ? null : Convert.ToInt64(raw);
        }
        catch { return null; }
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
    /// Wariant ApplyPayment dla KFS. Po NaPodstawie() Sfera defaultowo wpisuje
    /// PlatnoscGotowkaKwota = abs(wartosc KFS). Jesli klient zaplacil przelewem/karta,
    /// trzeba najpierw wyzerowac gotowke (zeby nie sumowac dwoch form), a potem ustawic
    /// docelowa forme.
    ///
    /// Kwota: amount jest pozytywna (np. 48.48) i reprezentuje wartosc zwrotu do klienta.
    /// Subiekt traktuje to jako "do zwrotu danym sposobem".
    /// </summary>
    private void ApplyCorrectionPayment(dynamic document, PaymentDto payment)
    {
        // 1. Wyzeruj domyslna gotowke (chyba ze targetem jest gotowka).
        if (!string.Equals(payment.Attribute, "PlatnoscGotowka", StringComparison.Ordinal))
        {
            try
            {
                SetCom(document, "PlatnoscGotowkaKwota", 0.0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ApplyCorrectionPayment: nie udalo sie wyzerowac PlatnoscGotowkaKwota");
            }
        }

        // 2. Ustaw forme docelowa - identycznie jak FS (Id + Kwota).
        ApplyPayment(document, payment);
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

    private static void SetComProperty(object target, string propName, object value)
    {
        target.GetType().InvokeMember(propName,
            BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.Public,
            null, target, new[] { value });
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
