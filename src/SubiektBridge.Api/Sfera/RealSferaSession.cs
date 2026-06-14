using System.Collections.Concurrent;
using System.Globalization;
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
            clauses.Add($"dok_Uwagi LIKE '%{EscapeLikePattern(r.NotesContains)}%'");

        if (!string.IsNullOrWhiteSpace(r.Nip))
            clauses.Add($"dok_NabKodSlownik = '{EscapeSqlLiteral(r.Nip)}'");

        return clauses.Count == 0 ? "dok_Id > 0" : string.Join(" AND ", clauses);
    }

    // TryParseExact, NIE regex (spójnie z InvoicesController.IsIsoDateOrEmpty): kształt
    // YYYY-MM-DD przepuszczałby daty niemożliwe kalendarzowo (2026-02-31), które w SQL
    // ('dok_DataWyst >= ...') kończą się błędem konwersji -> 502 zamiast pominięcia filtra.
    private static bool IsValidIsoDate(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    private static string EscapeSqlLiteral(string s) => s.Replace("'", "''");

    /// <summary>
    /// Escape dla literału użytego we wzorcu LIKE. Poza apostrofem neutralizuje wildcardy
    /// MSSQL: <c>%</c> (dowolny ciąg), <c>_</c> (dowolny znak), <c>[</c> (klasa znaków) -
    /// przez opakowanie w klasę znaków <c>[x]</c>. Bez tego referencja typu
    /// "system_order_123" dopasowałaby też "systemXorderX123" → fałszywy 409 DUPLICATE
    /// (audyt 2026-06-10 pkt 6). Kolejność: najpierw '[', potem '%' i '_' (wstawiane
    /// nawiasy nie są ponownie przetwarzane).
    /// </summary>
    private static string EscapeLikePattern(string s) => s
        .Replace("[", "[[]")
        .Replace("%", "[%]")
        .Replace("_", "[_]")
        .Replace("'", "''");

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

        // Magazyn dokumentu = magazyn roboczy sesji (Subiekt.MagazynId), ustawiany per request.
        // Per-pozycja SuPozycja.MagazynId (niżej) NIE zmienia dok_MagId na tym Subiekcie -
        // sprawdzone empirycznie 2026-06-06 (dok lądował na Głównym mimo MagazynId=N per pozycja).
        int? prevWarehouse = SetSessionWarehouse(request.WarehouseSubiektId);
        dynamic? fs = null;
        try
        {
            fs = Session.SuDokumentyManager.DodajFS();
            fs.LiczonyOdCenBrutto = true;

            long contractorId = ResolveOrCreateContractor(request.Contractor);
            fs.KontrahentId = contractorId;

            if (!request.Contractor.IsPerson)
            {
                // 1 = gtaFormaDokumentuFakturaKSeF (FormaDokumentuEnum). CELOWE: w Polsce
                // faktury B2B muszą być wystawiane jako KSeF. Bridge tylko oznacza formę -
                // samą wysyłkę do KSeF wykonuje operator w Subiekcie po sprawdzeniu
                // poprawności faktur. NIE zmieniać na 0 (tradycyjna).
                fs.FormaDokumentu = 1;
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
            fs.Uwagi = BuildUwagiWithReference(request.Notes, request.ExternalReference);

            // issue_date / sale_date z requestu - wcześniej cicho ignorowane (audyt pkt 4).
            // Data sprzedaży FS to DataZakonczeniaDostawy (DataSprzedazy dotyczy tylko ZW/PA
            // wg pomocy Sfery). Set tylko gdy data inna niż dzisiejsza.
            SetDocumentDateIfBackdated(fs, "DataWystawienia", request.IssueDate);
            SetDocumentDateIfBackdated(fs, "DataZakonczeniaDostawy", request.SaleDate);

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
            RestoreSessionWarehouse(prevWarehouse);
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

    public Task<TransferResponseDto> CreateTransferAsync(TransferRequestDto request, CancellationToken ct)
    {
        return RunOnStaAsync(() => CreateTransferCore(request), ct);
    }

    public Task<SettlementResponseDto> CreateSettlementAsync(long documentSubiektId, SettlementCreateRequestDto request, CancellationToken ct)
    {
        return RunOnStaAsync(() => CreateSettlementCore(documentSubiektId, request), ct);
    }

    public Task<SettlementStateResponseDto?> GetSettlementsAsync(long documentSubiektId, CancellationToken ct)
    {
        return RunOnStaAsync<SettlementStateResponseDto?>(() => GetSettlementsCore(documentSubiektId), ct);
    }

    public Task DeleteSettlementAsync(long documentSubiektId, long rozliczenieId, CancellationToken ct)
    {
        return RunOnStaAsync<bool>(() => { DeleteSettlementCore(documentSubiektId, rozliczenieId); return true; }, ct);
    }

    public Task<IReadOnlyList<BankOperationDto>> QueryBankOperationsAsync(BankOperationQueryRequestDto request, CancellationToken ct)
    {
        return RunOnStaAsync<IReadOnlyList<BankOperationDto>>(() => QueryBankOperationsCore(request), ct);
    }

    // Bank transactions (hb_Transakcja) - read-only SQL (hb_Transakcja nie jest wystawione przez Sfere).
    // Lecimy przez SqlConnection (jak QueryAsync), poza STA workerem (czysty SQL, zero COM).

    private string SqlConnStr() => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
    {
        DataSource = _options.Server,
        InitialCatalog = _options.Database,
        UserID = _options.DbUser,
        Password = _options.DbPassword,
        TrustServerCertificate = true,
        ConnectTimeout = 10,
    }.ToString();

    public Task<IReadOnlyList<BankTransactionDto>> QueryBankTransactionsAsync(BankTransactionQueryRequestDto request, CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<BankTransactionDto>>(() =>
        {
            string? dirChar = string.Equals(request.Direction, "in", StringComparison.OrdinalIgnoreCase) ? "C"
                            : string.Equals(request.Direction, "out", StringComparison.OrdinalIgnoreCase) ? "D" : null;
            int limit = Math.Clamp(request.Limit > 0 ? request.Limit : 200, 1, 1000);

            var where = new List<string>();
            if (request.UnbookedOnly) where.Add("t.hb_idOperacjiBankowej IS NULL");
            if (dirChar != null) where.Add("t.hb_Oznaczenie = @dir");
            if (IsIsoDate(request.From)) where.Add("t.hb_DataKsiegowania >= @from");
            if (IsIsoDate(request.To)) where.Add("t.hb_DataKsiegowania < DATEADD(day, 1, @to)");

            // Czysty passthrough surowych pol hb_Transakcja. rachunek_id/rachunek_numer = konto wyciagu przez
            // naglowek (LEFT JOIN - by NIE wyciac transakcji bez naglowka, np. recznie wprowadzonej). Surowe dane.
            string sql = "SELECT TOP (@limit) t.hb_IdTransakcji, t.hb_DataKsiegowania, t.hb_Kwota, t.hb_Oznaczenie, "
                       + "t.hb_Kontrahent, t.hb_RachKontrahent, t.hb_Tytul, t.hb_NrFaktury, t.hb_idOperacjiBankowej, "
                       + "n.hb_IdRachunku AS rachunek_id, n.hb_NumerRachunkuWyciagu AS rachunek_numer "
                       + "FROM hb_Transakcja t "
                       + "LEFT JOIN hb_NaglowekIStopka n ON n.hb_IdNaglowek = t.hb_IdNaglowekTr "
                       + (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "")
                       + "ORDER BY t.hb_DataKsiegowania DESC";

            using var conn = new Microsoft.Data.SqlClient.SqlConnection(SqlConnStr());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;
            cmd.Parameters.AddWithValue("@limit", limit);
            if (dirChar != null) cmd.Parameters.AddWithValue("@dir", dirChar);
            if (IsIsoDate(request.From)) cmd.Parameters.AddWithValue("@from", DateTime.ParseExact(request.From!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (IsIsoDate(request.To)) cmd.Parameters.AddWithValue("@to", DateTime.ParseExact(request.To!, "yyyy-MM-dd", CultureInfo.InvariantCulture));

            var list = new List<BankTransactionDto>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string oz = r["hb_Oznaczenie"]?.ToString() ?? "";
                list.Add(new BankTransactionDto(
                    HbId: Convert.ToInt64(r["hb_IdTransakcji"]),
                    Date: r["hb_DataKsiegowania"] is DateTime d ? d.ToString("yyyy-MM-dd") : null,
                    Amount: Convert.ToDecimal(r["hb_Kwota"]),
                    Direction: oz.Equals("C", StringComparison.OrdinalIgnoreCase) ? "in" : "out",
                    ContractorName: r["hb_Kontrahent"] as string,
                    ContractorAccount: r["hb_RachKontrahent"] as string,
                    Title: r["hb_Tytul"] as string,
                    InvoiceNumber: r["hb_NrFaktury"] == DBNull.Value ? null : r["hb_NrFaktury"].ToString(),
                    Booked: r["hb_idOperacjiBankowej"] != DBNull.Value,
                    BankOperationSubiektId: r["hb_idOperacjiBankowej"] != DBNull.Value ? Convert.ToInt64(r["hb_idOperacjiBankowej"]) : null,
                    RachunekId: r["rachunek_id"] != DBNull.Value ? Convert.ToInt64(r["rachunek_id"]) : null,
                    RachunekNumer: r["rachunek_numer"] == DBNull.Value ? null : r["rachunek_numer"].ToString()));
            }
            return list;
        }, ct);
    }

    // -------------------------- Ksiegowanie przelewu (hb_Transakcja -> operacja bankowa BP/BW) --------------------------

    private sealed record HbTxForBooking(decimal Kwota, DateTime? Data, string Oznaczenie, string? Tytul, long? ExistingOpId, long? RachunekId);

    // Lock per hb_id - serializuje ksiegowanie tej samej transakcji w obrebie procesu (most jest jednoinstancyjny).
    // Bez tego dwa rownolegle requesty z ROZNYMI Idempotency-Key moglyby oba przejsc guard ExistingOpId i utworzyc 2 BP.
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> _bookLocks = new();

    public async Task<BookResultDto> BookBankTransactionAsync(long hbId, long? contractorSubiektId, bool keepUnlinked, CancellationToken ct)
    {
        var gate = _bookLocks.GetOrAdd(hbId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // 1. Odczyt transakcji (SQL, poza STA). hb_Transakcja nie jest w Sferze - tylko read.
            var tx = await Task.Run(() => ReadHbTransactionForBooking(hbId), ct);
            if (tx is null) throw new BankBookingException(BookError.TransactionNotFound, $"hb_Transakcja {hbId} nie istnieje");

            // 2. Idempotent (stan): juz zaksiegowana -> zwroc istniejaca (NIE tworz drugiego BP).
            if (tx.ExistingOpId.HasValue)
            {
                _logger.LogInformation("Book: hb_id={HbId} juz zaksiegowana -> operacja {Op}", hbId, tx.ExistingOpId);
                return new BookResultDto(tx.ExistingOpId, hbId, Linked: true, AlreadyBooked: true, "Transakcja juz zaksiegowana");
            }

            if (!tx.RachunekId.HasValue)
                throw new BankBookingException(BookError.NoAccount, $"hb_Transakcja {hbId} nie ma powiazanego konta wyciagu (hb_NaglowekIStopka.hb_IdRachunku) - nie mozna zaksiegowac");

            // 3. Utworz operacje bankowa przez Sfere (STA). Sfera nie ma natywnej metody bookingu HB (brak HB-managera
            //    w CHM), wiec DodajOperacjeBankowa - i empirycznie sprawdzamy czy Subiekt sam ustawil link (krok 4).
            _logger.LogInformation("Book: tworze BP hb_id={HbId} kwota={Kwota} oznaczenie={Oz} rb_Id={Rb} kontrahent={Kh}",
                hbId, tx.Kwota, tx.Oznaczenie, tx.RachunekId, contractorSubiektId);
            long nzfId = await RunOnStaAsync(() => CreateBankOperationCore(tx, contractorSubiektId), ct);

            // 4. Re-check linku (SQL): czy hb_idOperacjiBankowej == nowy nzf_Id. Rozstrzyga Branch A (true) / B (false).
            long? linkedOp = await Task.Run(() => ReadHbLink(hbId), ct);
            bool linked = linkedOp.HasValue && linkedOp.Value == nzfId;
            if (linked)
            {
                _logger.LogInformation("Book OK: hb_id={HbId} -> BP {Op} (powiazany)", hbId, nzfId);
                return new BookResultDto(nzfId, hbId, Linked: true, AlreadyBooked: false, null);
            }

            // Branch B: Sfera NIE ustawila linku. Domyslnie COFAMY BP (zero orphanow). keep_unlinked zostawia do inspekcji.
            if (keepUnlinked)
            {
                _logger.LogWarning("Book Branch B: hb_id={HbId} BP {Op} NIEpowiazany - zostawiony (keep_unlinked)", hbId, nzfId);
                return new BookResultDto(nzfId, hbId, Linked: false, AlreadyBooked: false,
                    "Branch B: Sfera nie ustawila hb_idOperacjiBankowej. BP zostawiony (keep_unlinked) - cofnij recznie w GUI Subiekta.");
            }

            bool rolledBack = false;
            try
            {
                await RunOnStaAsync<bool>(() => { DeleteBankOperationCore(nzfId); return true; }, ct);
                rolledBack = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Book Branch B: rollback BP {Op} (hb_id={HbId}) NIE powiodl sie - ORPHAN", nzfId, hbId);
            }
            _logger.LogWarning("Book Branch B: hb_id={HbId} BP {Op} NIEpowiazany - {Stan}", hbId, nzfId, rolledBack ? "cofniety" : "ORPHAN");
            return new BookResultDto(
                BankOperationSubiektId: rolledBack ? null : nzfId,
                HbId: hbId, Linked: false, AlreadyBooked: false,
                Message: rolledBack
                    ? "Branch B: Sfera nie ksieguje home-bankingu (link NULL) - BP cofniety. Ksiegowanie zostaje w module Bankowosc Subiekta."
                    : $"Branch B + rollback BP {nzfId} NIE powiodl sie - ORPHAN, usun recznie w GUI Subiekta.");
        }
        finally
        {
            gate.Release();
        }
    }

    private void DeleteBankOperationCore(long nzfId)
    {
        dynamic? op = null;
        try
        {
            op = Session.FinManager.WczytajDokument(nzfId);
            op.Usun();
        }
        finally
        {
            if (op is not null) TryClose(op);
        }
    }

    private long CreateBankOperationCore(HbTxForBooking tx, long? contractorId)
    {
        // C (uznanie/wplata) -> BP (gtaDokFinTypBP=19); D (obciazenie/wyplata) -> BW (20). Kwota = magnitude, double.
        int typ = tx.Oznaczenie.Equals("C", StringComparison.OrdinalIgnoreCase) ? 19 : 20;
        dynamic? bp = null;
        try
        {
            bp = Session.FinManager.DodajOperacjeBankowa(typ, tx.RachunekId!.Value);
            if (tx.Data.HasValue) SetCom((object)bp, "Data", tx.Data.Value);
            SetCom((object)bp, "WartoscPoczatkowa", (double)Math.Abs(tx.Kwota));
            if (contractorId.HasValue)
            {
                bp.ObiektPowiazanyWstaw(1, contractorId.Value); // 1 = gtaDokFinObiektKontrahent
            }
            else
            {
                TrySet((object)bp, "OperacjaBezDanychKh", true);
            }
            if (!string.IsNullOrEmpty(tx.Tytul)) TrySet((object)bp, "Tytulem", tx.Tytul);
            bp.Zapisz();
            return ToInt64(bp.Identyfikator);
        }
        catch (COMException cex)
        {
            var inner = Marshal.GetExceptionForHR(cex.ErrorCode);
            _logger.LogError(cex, "DodajOperacjeBankowa/Zapisz padl 0x{Hr:X8} (rb_Id={Rb})", cex.ErrorCode, tx.RachunekId);
            throw new BankBookingException(BookError.Internal, $"DodajOperacjeBankowa/Zapisz padl: 0x{cex.ErrorCode:X8} {inner?.Message ?? cex.Message}", cex);
        }
        finally
        {
            if (bp is not null) TryClose(bp);
        }
    }

    private HbTxForBooking? ReadHbTransactionForBooking(long hbId)
    {
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(SqlConnStr());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT t.hb_Kwota, t.hb_DataKsiegowania, t.hb_Oznaczenie, t.hb_Tytul, t.hb_idOperacjiBankowej, n.hb_IdRachunku "
                        + "FROM hb_Transakcja t LEFT JOIN hb_NaglowekIStopka n ON n.hb_IdNaglowek = t.hb_IdNaglowekTr "
                        + "WHERE t.hb_IdTransakcji = @hb";
        cmd.CommandTimeout = 30;
        cmd.Parameters.AddWithValue("@hb", hbId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new HbTxForBooking(
            Kwota: Convert.ToDecimal(r["hb_Kwota"]),
            Data: r["hb_DataKsiegowania"] is DateTime d ? d : null,
            Oznaczenie: r["hb_Oznaczenie"]?.ToString() ?? "C",
            Tytul: r["hb_Tytul"] as string,
            ExistingOpId: r["hb_idOperacjiBankowej"] != DBNull.Value ? Convert.ToInt64(r["hb_idOperacjiBankowej"]) : null,
            RachunekId: r["hb_IdRachunku"] != DBNull.Value ? Convert.ToInt64(r["hb_IdRachunku"]) : null);
    }

    private long? ReadHbLink(long hbId)
    {
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(SqlConnStr());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hb_idOperacjiBankowej FROM hb_Transakcja WHERE hb_IdTransakcji = @hb";
        cmd.CommandTimeout = 30;
        cmd.Parameters.AddWithValue("@hb", hbId);
        var v = cmd.ExecuteScalar();
        return v is null || v == DBNull.Value ? null : Convert.ToInt64(v);
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
        // Anti-duplicate po external_reference w Uwagach (jak FS/MM) - retry z innym
        // Idempotency-Key nie może dublować PZ (zawyżony stan magazynowy + podwójny
        // koszt zakupu). Dodane w audycie 2026-06-10 - wcześniej PZ nie miał tej warstwy.
        var existingReceiptId = FindExistingInvoiceByReference(request.ExternalReference, "PZ");
        if (existingReceiptId.HasValue)
        {
            dynamic existingPz = Session.SuDokumentyManager.WczytajDokument(existingReceiptId.Value);
            try
            {
                throw new DuplicateInvoiceException(
                    existingReceiptId.Value,
                    (string)existingPz.NumerPelny ?? "",
                    request.ExternalReference);
            }
            finally
            {
                try { existingPz.Zamknij(); } catch { /* cleanup */ }
            }
        }

        // Magazyn dokumentu = magazyn roboczy sesji (Subiekt.MagazynId), ustawiany per request
        // (jak FS). Per-pozycja SuPozycja.MagazynId nie wystarcza na tym Subiekcie.
        int? prevWarehouse = SetSessionWarehouse(request.WarehouseSubiektId);
        dynamic? pz = null;
        try
        {
            pz = Session.SuDokumentyManager.DodajPZ();
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

            // Link PZ->FS (DoDokumentuId) usunięty jako dead code (audyt 2026-06-10 pkt 7):
            // FS wymaga stanu magazynowego, więc PZ ZAWSZE idzie przed FS i source_invoice_
            // subiekt_id było zawsze null. Pole w JSON jest nadal tolerowane (ignorowane).

            int? perLineWarehouseId = request.WarehouseSubiektId;
            foreach (var line in request.Lines)
            {
                AddLineToDocument(pz, line.Ean, line.NameFallback, line.Quantity, line.Unit, line.UnitPriceGross, perLineWarehouseId, useNetPrice: true, vatRate: line.VatRate, unitPriceNet: line.UnitPriceNet);
            }

            pz.Uwagi = BuildUwagiWithReference(request.Notes, request.ExternalReference);

            // issue_date PZ - wcześniej cicho ignorowane (audyt pkt 4). PZ to dokument
            // magazynowy: ustawiamy parę DataMagazynowa + DataWystawienia (pomoc Sfery
            // wymaga modyfikacji obu dla dokumentów magazynowych, przykład przy MM).
            // Set tylko gdy data inna niż dzisiejsza, twardo - zła data = zły okres
            // VAT/stanów, lepiej głośno odmówić.
            SetDocumentDateIfBackdated(pz, "DataMagazynowa", request.IssueDate);
            SetDocumentDateIfBackdated(pz, "DataWystawienia", request.IssueDate);

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
            // Gdy linia ma unit_price_net (opcja PZ), brutto = netto * (1 + VAT); inaczej
            // bierzemy unit_price_gross. Bez tego klient wysylajacy net + gross=0 (zgodnie
            // z dokumentacja) dostalby/zacache'owal totals.gross = 0.
            decimal? grossOpt = TryReadDecimal(pz, "WartoscBrutto");
            decimal gross = grossOpt ?? request.Lines.Sum(l =>
                (l.UnitPriceNet.HasValue ? l.UnitPriceNet.Value * (1m + l.VatRate / 100m) : l.UnitPriceGross) * l.Quantity);

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
            RestoreSessionWarehouse(prevWarehouse);
        }
    }

    private TransferResponseDto CreateTransferCore(TransferRequestDto request)
    {
        // MM - przesunięcie międzymagazynowe. Dokument WEWNĘTRZNY (nie idzie do KSeF).
        // Magazyny ustawiamy NA DOKUMENCIE: MagazynNadawczyId (źródło) + MagazynOdbiorczyId
        // (cel) - to dedykowane atrybuty MM (dla FS/PZ rzucają NotImplemented; pomoc Sfery
        // SuDokument_MagazynNadawczyId/OdbiorczyId). Pozycje tylko towarowe (po EAN), bez ceny -
        // wartość MM Subiekt liczy z kosztu towaru.

        // Anti-duplicate po external_reference w Uwagach (jak FS) - ochrona przed podwójnym MM
        // przy retry/timeout. Fail-open (FindExistingInvoiceByReference loguje i zwraca null
        // przy błędzie) - idempotency-key cache w kontrolerze to główna warstwa ochrony.
        var existingId = FindExistingInvoiceByReference(request.ExternalReference, "MM");
        if (existingId.HasValue)
        {
            dynamic existingDok = Session.SuDokumentyManager.WczytajDokument(existingId.Value);
            try
            {
                throw new DuplicateInvoiceException(
                    existingId.Value, (string)existingDok.NumerPelny ?? "", request.ExternalReference);
            }
            finally
            {
                try { existingDok.Zamknij(); } catch { /* cleanup */ }
            }
        }

        dynamic? mm = null;
        try
        {
            mm = Session.SuDokumentyManager.DodajMM();
            mm.MagazynNadawczyId = request.SourceWarehouseId;
            mm.MagazynOdbiorczyId = request.DestWarehouseId;

            foreach (var line in request.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Ean))
                {
                    throw new InvalidOperationException(
                        "MM: każda pozycja musi mieć EAN (przesuwamy realny towar magazynowy, nie usługę).");
                }
                if (!(bool)Session.Towary.Istnieje(line.Ean))
                {
                    throw new MissingProductException(line.Ean);
                }

                dynamic towar = Session.Towary.Wczytaj(line.Ean);
                try
                {
                    dynamic pos = mm.Pozycje.Dodaj(towar);
                    pos.IloscJm = line.Quantity;
                    pos.Jm = string.IsNullOrEmpty(line.Unit) ? "szt." : line.Unit;
                }
                finally
                {
                    TryClose(towar);
                }
            }

            mm.Uwagi = BuildUwagiWithReference(request.Notes, request.ExternalReference);

            mm.Zapisz();

            long subiektId = ToInt64(mm.Identyfikator);
            string number = (string)mm.NumerPelny;
            _lastInvoiceAt = DateTimeOffset.UtcNow;

            return new TransferResponseDto(
                Id: $"sub_{subiektId}",
                SubiektId: subiektId,
                Number: number,
                IssuedAt: _lastInvoiceAt.Value,
                SourceWarehouseId: request.SourceWarehouseId,
                DestWarehouseId: request.DestWarehouseId);
        }
        finally
        {
            TryClose(mm);
        }
    }

    // -------------------------- Settlements (rozliczenia rozrachunkow) --------------------------

    private SettlementResponseDto CreateSettlementCore(long documentSubiektId, SettlementCreateRequestDto req)
    {
        if (req.Amount <= 0m)
        {
            throw new SettlementException(SettlementError.InvalidAmount, "amount musi byc > 0");
        }
        // Normalizacja precyzji - WartoscBiezaca ma 2 miejsca; bez tego groszowy rozjazd przy walidacji/Rozlicz.
        decimal amount = Math.Round(req.Amount, 2, MidpointRounding.AwayFromZero);

        dynamic? dok = null;
        dynamic? rozrachunek = null;
        dynamic? bankOp = null;
        try
        {
            // 1. Dokument: istnienie + typ. SuDokument.Typ (= dok_Typ): 1=FZ, 2=FS; korekty (5/6) i inne -> odrzuc.
            try { dok = Session.SuDokumentyManager.WczytajDokument(documentSubiektId); }
            catch (Exception ex) { throw new SettlementException(SettlementError.DocumentNotFound, $"Dokument {documentSubiektId} nie istnieje", ex); }
            if (dok is null) throw new SettlementException(SettlementError.DocumentNotFound, $"Dokument {documentSubiektId} nie istnieje");
            long docType = TryReadInt64((object)dok, "Typ") ?? -1;
            if (docType != 1 && docType != 2)
            {
                throw new SettlementException(SettlementError.UnsupportedDocumentType,
                    $"Rozliczenia obsluguja tylko FS (Typ=2) i FZ (Typ=1). Dokument {documentSubiektId} ma Typ={docType} (np. korekta) - nieobslugiwany.");
            }

            // 2. Wczytaj ISTNIEJACA operacje bankowa (zaimportowany przelew) po nzf_Id - potrzebny kontrahent + saldo.
            bool bankExists;
            try { bankExists = (bool)Session.FinManager.Istnieje(req.BankOperationSubiektId); }
            catch { bankExists = false; }
            if (!bankExists) throw new SettlementException(SettlementError.BankOperationNotFound, $"Operacja bankowa {req.BankOperationSubiektId} nie istnieje w nz__Finanse");
            try { bankOp = Session.FinManager.WczytajDokument(req.BankOperationSubiektId); }
            catch (Exception ex) { throw new SettlementException(SettlementError.BankOperationNotFound, $"Nie mozna wczytac operacji bankowej {req.BankOperationSubiektId}", ex); }
            long? bankKontrahent = TryReadInt64((object)bankOp, "ObiektPowiazanyId");
            decimal bankRemaining = TryReadDecimal((object)bankOp, "WartoscBiezaca") ?? 0m;
            if (amount - bankRemaining > 0.005m)
            {
                throw new SettlementException(SettlementError.BankOperationExhausted,
                    $"Operacja bankowa {req.BankOperationSubiektId} ma dostepne saldo {bankRemaining:F2} < amount {amount:F2} (operacja czesciowo/calkowicie skonsumowana).");
            }

            // 3. Wybor wlasciwego rozrachunku. FS marketplace ma zwykle DWA rozrachunki (typ=39): wyzerowany na
            //    kupujacym + OTWARTY na platniku (Allegro Pay). NIE polegamy na PodajRozrachunek ani na indeksie
            //    kolekcji - wybieramy po OTWARTEJ KWOCIE i dopasowaniu kontrahenta do operacji bankowej (zweryfikowane
            //    na prod 2026-06: FZ zawsze 1 wiersz, FS po 2 wiersze {wyzerowany + otwarty}).
            var rozrachunki = DiscoverRozrachunki(documentSubiektId);
            if (rozrachunki.Count == 0)
            {
                throw new SettlementException(SettlementError.NoRozrachunek, $"Dokument {documentSubiektId} nie ma rozrachunku (dokument magazynowy bez platnosci?)");
            }
            var openRozr = rozrachunki.Where(r => r.Remaining > 0.005m).ToList();
            if (openRozr.Count == 0)
            {
                throw new SettlementException(SettlementError.AlreadySettled,
                    $"Rozrachunki dokumentu {documentSubiektId} sa juz rozliczone (brak otwartej kwoty) - dokument nie wymaga recznego rozliczenia (gotowka/auto?).");
            }
            var matchingRozr = openRozr.Where(r => bankKontrahent.HasValue && r.Contractor == bankKontrahent.Value).ToList();
            if (matchingRozr.Count == 0)
            {
                throw new SettlementException(SettlementError.BankOperationContractorMismatch,
                    $"Zaden otwarty rozrachunek dokumentu {documentSubiektId} nie jest na kontrahencie operacji bankowej {req.BankOperationSubiektId} (kontrahent={bankKontrahent}) - mozliwa platnosc karta/ratami (rozrachunek na centrum) lub zly przelew.");
            }
            var target = matchingRozr.FirstOrDefault(r => r.Remaining + 0.005m >= amount, matchingRozr[0]);

            // 4. GUARD waluty (MVP=PLN).
            if (!string.IsNullOrEmpty(target.Currency) && !string.Equals(target.Currency, "PLN", StringComparison.OrdinalIgnoreCase))
            {
                throw new SettlementException(SettlementError.UnsupportedCurrency,
                    $"Rozrachunek w walucie {target.Currency} - most rozlicza tylko PLN (RozliczWaluta nieobslugiwane).");
            }
            // 5. amount vs otwarta kwota wybranego rozrachunku (czesciowe dozwolone; nadplata odrzucana).
            if (amount - target.Remaining > 0.005m)
            {
                throw new SettlementException(SettlementError.AmountExceedsRemaining,
                    $"amount {amount:F2} > pozostalo {target.Remaining:F2}", details: new { remaining = target.Remaining, requested = amount });
            }

            long rozrachunekId = target.Id;
            decimal remaining = target.Remaining;
            rozrachunek = Session.FinManager.Wczytaj(rozrachunekId);

            // 6. ANTI-DUPLICATE FAIL-CLOSED: skan istniejacych rozliczen po SplataId==bankOpId.
            //    KAZDY wyjatek w skanie przerywa flow (NIE przepuszczac do Rozlicz - podwojne rozliczenie = blad ksiegowy).
            ScanForExistingSettlement(rozrachunek, req.BankOperationSubiektId, rozrachunekId);

            // 7. ROZLICZ OD STRONY ROZRACHUNKU (jedyny poprawny wariant dla metody kasowej VAT).
            dynamic? rozliczeniaCol = null;
            try
            {
                rozliczeniaCol = rozrachunek.Rozliczenia;
                // Kwota jako double (NIE decimal) - most marshaluje wszystkie kwoty pieniezne do COM
                // jako double (patrz ApplyPayment: SetCom(..., (double)payment.Amount)). decimal binduje
                // sie do VT_DECIMAL, nie do typu monetarnego oczekiwanego przez Sfere.
                dynamic finRoz = rozliczeniaCol.Rozlicz((object)bankOp, (double)amount);
                try { if (finRoz is not null) Marshal.ReleaseComObject(finRoz); } catch { /* cleanup */ }
            }
            catch (COMException cex)
            {
                var inner = Marshal.GetExceptionForHR(cex.ErrorCode);
                throw new SettlementException(SettlementError.Internal, $"Rozlicz padl: 0x{cex.ErrorCode:X8} {inner?.Message ?? cex.Message}", cex);
            }
            finally
            {
                if (rozliczeniaCol is not null) { try { Marshal.ReleaseComObject(rozliczeniaCol); } catch { /* cleanup */ } }
            }

            // 8. Zapisz NA ROZRACHUNKU (obiekt na ktorym wolano Rozlicz).
            rozrachunek.Zapisz();

            // 9. Przeladuj rozrachunek - atrybuty/RozliczenieId odswiezaja sie dopiero po reload.
            //     Reload moze rzucic transientnie (COM/sesja) - retry 2x. BEZ Thread.Sleep
            //     (jestesmy na STA workerze: sleep zablokowalby cala kolejke Sfery).
            decimal remainingAfter = remaining - amount; // best-effort fallback (PLN - dokladne)
            long newRozliczenieId = -1;
            Exception? reloadError = null;
            for (int attempt = 0; attempt < 2 && newRozliczenieId < 0; attempt++)
            {
                try
                {
                    if (rozrachunek is not null) { TryClose(rozrachunek); rozrachunek = null; }
                    rozrachunek = Session.FinManager.Wczytaj(rozrachunekId);
                    remainingAfter = TryReadDecimal((object)rozrachunek, "WartoscBiezaca") ?? remainingAfter;
                    newRozliczenieId = FindRozliczenieIdBySplata(rozrachunek, req.BankOperationSubiektId);
                }
                catch (Exception ex) { reloadError = ex; }
            }
            if (newRozliczenieId < 0)
            {
                // Rozliczenie JEST utrwalone (Zapisz przeszlo), ale nie odczytalismy RozliczenieId.
                // Surfacujemy jako Internal (nie cache'ujemy bledonego id). Klient odzyska przez retry:
                // anti-duplicate FAIL-CLOSED zwroci 409 z existing_rozliczenie_id, lub przez GET settlements.
                _logger.LogError(reloadError, "Settlement zapisany dla rozrachunku {Id}, ale RozliczenieId nieodczytany po reload (2 proby)", rozrachunekId);
                throw new SettlementException(SettlementError.Internal,
                    $"Rozliczenie rozrachunku {rozrachunekId} zostalo zapisane, ale RozliczenieId nie odczytane po przeladowaniu - retry zwroci 409 z existing_rozliczenie_id.", reloadError);
            }

            _lastInvoiceAt = DateTimeOffset.UtcNow;
            return new SettlementResponseDto(
                RozliczenieId: newRozliczenieId,
                DocumentId: $"sub_{documentSubiektId}",
                DocumentSubiektId: documentSubiektId,
                RozrachunekSubiektId: rozrachunekId,
                BankOperationSubiektId: req.BankOperationSubiektId,
                Amount: amount,
                RemainingAfter: remainingAfter,
                IsFullySettled: Math.Abs(remainingAfter) < 0.005m,
                SettledAt: _lastInvoiceAt.Value);
        }
        finally
        {
            if (bankOp is not null) TryClose(bankOp);
            if (rozrachunek is not null) TryClose(rozrachunek);
            if (dok is not null) TryClose(dok);
        }
    }

    /// <summary>
    /// FAIL-CLOSED skan: rzuca <see cref="DuplicateSettlementException"/> gdy operacja bankowa
    /// jest juz rozliczona z tym rozrachunkiem; KAZDY inny wyjatek -> SettlementException(ScanFailed).
    /// </summary>
    private void ScanForExistingSettlement(dynamic rozrachunek, long bankOperationId, long rozrachunekId)
    {
        dynamic? col = null;
        try
        {
            col = rozrachunek.Rozliczenia;
            foreach (dynamic roz in (System.Collections.IEnumerable)col)
            {
                try
                {
                    long? splataId = TryReadInt64((object)roz, "SplataId");
                    long existingRozId = TryReadInt64((object)roz, "RozliczenieId") ?? -1;
                    if (splataId == bankOperationId)
                    {
                        throw new DuplicateSettlementException(existingRozId, rozrachunekId, bankOperationId);
                    }
                }
                finally { try { Marshal.ReleaseComObject(roz); } catch { /* cleanup */ } }
            }
        }
        catch (DuplicateSettlementException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScanForExistingSettlement padl (rozrachunek={RozrachunekId}, bankOp={BankOp}) - fail-closed", rozrachunekId, bankOperationId);
            throw new SettlementException(SettlementError.ScanFailed,
                "Skan anti-duplicate rozliczen padl - przerwano (fail-closed, by NIE dopuscic podwojnego rozliczenia).", ex);
        }
        finally
        {
            if (col is not null) { try { Marshal.ReleaseComObject(col); } catch { /* cleanup */ } }
        }
    }

    /// <summary>Zwraca RozliczenieId linii o SplataId==bankOperationId, lub -1.</summary>
    private long FindRozliczenieIdBySplata(dynamic rozrachunek, long bankOperationId)
    {
        dynamic? col = null;
        long found = -1;
        try
        {
            col = rozrachunek.Rozliczenia;
            foreach (dynamic roz in (System.Collections.IEnumerable)col)
            {
                try
                {
                    long? splataId = TryReadInt64((object)roz, "SplataId");
                    if (splataId == bankOperationId)
                    {
                        found = TryReadInt64((object)roz, "RozliczenieId") ?? -1;
                        break; // jedna operacja = max jedno powiazanie z danym rozrachunkiem
                    }
                }
                finally { try { Marshal.ReleaseComObject(roz); } catch { /* cleanup */ } }
            }
        }
        finally
        {
            if (col is not null) { try { Marshal.ReleaseComObject(col); } catch { /* cleanup */ } }
        }
        return found;
    }

    /// <summary>
    /// Odczytuje rozrachunki dokumentu z nz__Finanse (FinManager.OtworzKolekcje po nzf_IdDokumentAuto).
    /// FS marketplace ma zwykle 2 rozrachunki typ=39 (wyzerowany kupujacy + otwarty platnik); FZ zawsze 1
    /// (typ=40). Zwraca (id, pozostalo, kontrahent, waluta) per wiersz - wybor wlasciwego robi wolajacy.
    /// </summary>
    private List<(long Id, decimal Remaining, long? Contractor, string? Currency)> DiscoverRozrachunki(long documentSubiektId)
    {
        var rows = new List<(long Id, decimal Remaining, long? Contractor, string? Currency)>();
        dynamic? col = null;
        try
        {
            col = Session.FinManager.OtworzKolekcje($"nzf_IdDokumentAuto={documentSubiektId} AND nzf_Typ IN (39, 40)", "");
            foreach (dynamic r in (System.Collections.IEnumerable)col)
            {
                try
                {
                    rows.Add((
                        ToInt64(r.Identyfikator),
                        TryReadDecimal((object)r, "WartoscBiezaca") ?? 0m,
                        TryReadInt64((object)r, "ObiektPowiazanyId"),
                        TryReadString((object)r, "Waluta")));
                }
                finally { try { Marshal.ReleaseComObject(r); } catch { /* cleanup */ } }
            }
        }
        finally
        {
            if (col is not null) { try { Marshal.ReleaseComObject(col); } catch { /* cleanup */ } }
        }
        return rows;
    }

    private SettlementStateResponseDto? GetSettlementsCore(long documentSubiektId)
    {
        // 1. Istnienie dokumentu (404 gdy brak).
        dynamic? dok = null;
        try { dok = Session.SuDokumentyManager.WczytajDokument(documentSubiektId); }
        catch (Exception ex) { _logger.LogInformation(ex, "GetSettlements: dokument {Id} nie znaleziony", documentSubiektId); return null; }
        try { if (dok is null) return null; }
        finally { if (dok is not null) TryClose(dok); }

        // 2. Rozrachunki dokumentu; raportujemy OTWARTY (max pozostalo), inaczej dowolny (rozliczony).
        var rozrachunki = DiscoverRozrachunki(documentSubiektId);
        if (rozrachunki.Count == 0)
        {
            throw new SettlementException(SettlementError.NoRozrachunek, $"Dokument {documentSubiektId} nie ma rozrachunku");
        }
        var open = rozrachunki.Where(r => r.Remaining > 0.005m).OrderByDescending(r => r.Remaining).ToList();
        long targetId = open.Count > 0 ? open[0].Id : rozrachunki[0].Id;

        dynamic? rozrachunek = null;
        dynamic? col = null;
        try
        {
            rozrachunek = Session.FinManager.Wczytaj(targetId);
            long rozrachunekId = ToInt64(rozrachunek.Identyfikator);
            decimal original = TryReadDecimal((object)rozrachunek, "WartoscPoczatkowa") ?? 0m;
            decimal remaining = TryReadDecimal((object)rozrachunek, "WartoscBiezaca") ?? 0m;
            DateTimeOffset? lastSettlement = TryReadDate((object)rozrachunek, "DataOstatniejSplaty");

            var lines = new List<SettlementLineDto>();
            col = rozrachunek.Rozliczenia;
            foreach (dynamic roz in (System.Collections.IEnumerable)col)
            {
                try
                {
                    lines.Add(new SettlementLineDto(
                        RozliczenieId: TryReadInt64((object)roz, "RozliczenieId") ?? -1,
                        Amount: TryReadDecimal((object)roz, "Kwota") ?? 0m,
                        SettledAt: TryReadDate((object)roz, "Data"),
                        SplataSubiektId: TryReadInt64((object)roz, "SplataId"),
                        DlugSubiektId: TryReadInt64((object)roz, "DlugId"),
                        Type: (int?)TryReadInt64((object)roz, "Typ")));
                }
                finally { try { Marshal.ReleaseComObject(roz); } catch { /* cleanup */ } }
            }

            return new SettlementStateResponseDto(
                DocumentId: $"sub_{documentSubiektId}",
                DocumentSubiektId: documentSubiektId,
                RozrachunekSubiektId: rozrachunekId,
                OriginalAmount: original,
                RemainingAmount: remaining,
                IsFullySettled: Math.Abs(remaining) < 0.005m,
                LastSettlementAt: lastSettlement,
                Settlements: lines);
        }
        finally
        {
            if (col is not null) { try { Marshal.ReleaseComObject(col); } catch { /* cleanup */ } }
            if (rozrachunek is not null) TryClose(rozrachunek);
        }
    }

    private void DeleteSettlementCore(long documentSubiektId, long rozliczenieId)
    {
        // 1. Istnienie dokumentu.
        dynamic? dok = null;
        try { dok = Session.SuDokumentyManager.WczytajDokument(documentSubiektId); }
        catch (Exception ex) { throw new SettlementException(SettlementError.DocumentNotFound, $"Dokument {documentSubiektId} nie istnieje", ex); }
        try { if (dok is null) throw new SettlementException(SettlementError.DocumentNotFound, $"Dokument {documentSubiektId} nie istnieje"); }
        finally { if (dok is not null) TryClose(dok); }

        // 2. Rozliczenie moze byc na ktorymkolwiek rozrachunku dokumentu (FS = 2 rozrachunki) - szukamy po wszystkich.
        var rozrachunki = DiscoverRozrachunki(documentSubiektId);
        if (rozrachunki.Count == 0)
        {
            throw new SettlementException(SettlementError.SettlementNotFound, $"Rozliczenie {rozliczenieId} nie istnieje dla dokumentu {documentSubiektId}");
        }

        foreach (var row in rozrachunki)
        {
            dynamic? rozrachunek = null;
            dynamic? col = null;
            dynamic? target = null;
            try
            {
                rozrachunek = Session.FinManager.Wczytaj(row.Id);
                col = rozrachunek.Rozliczenia;
                foreach (dynamic roz in (System.Collections.IEnumerable)col)
                {
                    long rozId = TryReadInt64((object)roz, "RozliczenieId") ?? -1;
                    if (rozId == rozliczenieId) { target = roz; break; } // NIE zwalniamy - do Usun
                    try { Marshal.ReleaseComObject(roz); } catch { /* cleanup */ }
                }

                if (target is not null)
                {
                    try { target.Usun(); }
                    catch (COMException cex)
                    {
                        var inner = Marshal.GetExceptionForHR(cex.ErrorCode);
                        throw new SettlementException(SettlementError.Internal, $"Usun rozliczenia padl: 0x{cex.ErrorCode:X8} {inner?.Message ?? cex.Message}", cex);
                    }
                    finally { try { Marshal.ReleaseComObject(target); } catch { /* cleanup */ } target = null; }

                    rozrachunek.Zapisz();
                    _lastInvoiceAt = DateTimeOffset.UtcNow;
                    return; // znaleziono i usunieto
                }
            }
            finally
            {
                if (target is not null) { try { Marshal.ReleaseComObject(target); } catch { /* cleanup */ } }
                if (col is not null) { try { Marshal.ReleaseComObject(col); } catch { /* cleanup */ } }
                if (rozrachunek is not null) TryClose(rozrachunek);
            }
        }

        throw new SettlementException(SettlementError.SettlementNotFound, $"Rozliczenie {rozliczenieId} nie istnieje dla dokumentu {documentSubiektId}");
    }

    private IReadOnlyList<BankOperationDto> QueryBankOperationsCore(BankOperationQueryRequestDto request)
    {
        // Filtr po KOLUMNIE DB nzf_Typ (NIE FinDokument.Typ - od v1.17 enum Sfery != nzf_Typ).
        // 19 = BP (wplata), 20 = BW (wyplata).
        var conditions = new List<string>();
        var dir = request.Direction?.Trim().ToLowerInvariant();
        if (dir == "in") conditions.Add("nzf_Typ = 19");
        else if (dir == "out") conditions.Add("nzf_Typ = 20");
        else conditions.Add("nzf_Typ IN (19, 20)");

        if (!string.IsNullOrWhiteSpace(request.From) && IsIsoDate(request.From))
            conditions.Add($"nzf_Data >= '{request.From}'");
        if (!string.IsNullOrWhiteSpace(request.To) && IsIsoDate(request.To))
            conditions.Add($"nzf_Data <= '{request.To}'");

        string filter = string.Join(" AND ", conditions);
        int limit = Math.Clamp(request.Limit > 0 ? request.Limit : 200, 1, 1000);

        var results = new List<BankOperationDto>();
        dynamic? kolekcja = null;
        try
        {
            kolekcja = Session.FinManager.OtworzKolekcje(filter, "nzf_Data DESC");
            foreach (dynamic op in (System.Collections.IEnumerable)kolekcja)
            {
                try
                {
                    decimal? remaining = TryReadDecimal((object)op, "WartoscBiezaca");
                    if (request.UnsettledOnly && (remaining ?? 0m) <= 0.005m)
                    {
                        continue;
                    }

                    long? typ = TryReadInt64((object)op, "Typ");
                    string direction = typ switch { 19 => "in", 20 => "out", _ => "unknown" };
                    DateTimeOffset? data = TryReadDate((object)op, "Data");

                    results.Add(new BankOperationDto(
                        SubiektId: ToInt64(op.Identyfikator),
                        Direction: direction,
                        Date: data?.ToString("yyyy-MM-dd"),
                        Amount: TryReadDecimal((object)op, "WartoscPoczatkowa"),
                        Remaining: remaining,
                        ContractorId: TryReadInt64((object)op, "ObiektPowiazanyId"),
                        Title: TryReadString((object)op, "Tytulem"),
                        Number: TryReadString((object)op, "NumerPelny")));

                    if (results.Count >= limit)
                    {
                        break;
                    }
                }
                finally { try { Marshal.ReleaseComObject(op); } catch { /* cleanup */ } }
            }
        }
        finally
        {
            if (kolekcja is not null) { try { Marshal.ReleaseComObject(kolekcja); } catch { /* cleanup */ } }
        }

        return results;
    }

    private static bool IsIsoDate(string? date) =>
        !string.IsNullOrWhiteSpace(date)
        && DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static DateTimeOffset? TryReadDate(object target, string propName)
    {
        try
        {
            object? raw = target.GetType().InvokeMember(propName,
                BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public,
                null, target, Array.Empty<object>());
            return raw is null ? null : new DateTimeOffset(Convert.ToDateTime(raw));
        }
        catch { return null; }
    }

    private InvoiceResponseDto CreateCorrectionCore(long sourceSubiektId, InvoiceCorrectionRequestDto request)
    {
        // Magazyn KFS = magazyn źródłowej FS (dok_MagId). Subiekt bierze dok_MagId z magazynu
        // SESJI (Subiekt.MagazynId) w chwili Zapisz() - tak samo jak FS/PZ (v0.7.50). NaPodstawie()
        // kopiuje pozycje, ale NIE dziedziczy magazynu dokumentu. Bez jawnego ustawienia magazynu
        // sesji KFS ląduje na domyślnym magazynie roboczym operatora zamiast cofać towar z magazynu,
        // z którego FS go wydała (bug: korekty wpadały na nowy magazyn zamiast na magazyn FS).
        //
        // Twardy set (NIE cichy fallback na magazyn sesji - to BYŁ bug): gdy nie da się odczytać
        // dok_MagId źródłowej FS, fail-loud (retry job) zamiast wystawić KFS na zły magazyn.
        // KFSn (SourceIsExternal) nie ma dokumentu źródłowego w bazie - zostaje magazyn sesji.
        // Anti-duplicate po external_reference w Uwagach (jak FS/PZ/MM) - Uwagi KFS
        // zawsze zawierają "ref: <external_reference>" (niżej), więc pre-check chroni
        // przed podwójną korektą przy retry z innym Idempotency-Key (audyt 2026-06-10).
        var existingCorrectionId = FindExistingInvoiceByReference(request.ExternalReference, "KFS");
        if (existingCorrectionId.HasValue)
        {
            dynamic existingKfs = Session.SuDokumentyManager.WczytajDokument(existingCorrectionId.Value);
            try
            {
                throw new DuplicateInvoiceException(
                    existingCorrectionId.Value,
                    (string)existingKfs.NumerPelny ?? "",
                    request.ExternalReference);
            }
            finally
            {
                try { existingKfs.Zamknij(); } catch { /* cleanup */ }
            }
        }

        int? prevWarehouse = null;
        if (!request.SourceIsExternal)
        {
            int sourceWarehouse = ReadDocumentWarehouseIdOrThrow(sourceSubiektId);
            prevWarehouse = SetSessionWarehouse(sourceWarehouse);
        }

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
                    // TryParseExact spójnie z resztą dat (kontroler waliduje format, to defensywa).
                    if (!DateTime.TryParseExact(request.SourceInvoiceDate, "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var sourceDate))
                    {
                        throw new InvalidOperationException(
                            $"Nieprawidłowy format source_invoice_date '{request.SourceInvoiceDate}' (oczekiwane YYYY-MM-DD).");
                    }
                    SetComProperty(kfs, "DoDokumentuDataWystawienia", sourceDate);
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

            // issue_date korekty - wcześniej cicho ignorowane (audyt pkt 4). Set tylko gdy
            // data inna niż dzisiejsza; po NaPodstawie(), żeby nic jej nie nadpisało.
            SetDocumentDateIfBackdated(kfs, "DataWystawienia", request.IssueDate);

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
            RestoreSessionWarehouse(prevWarehouse);
        }
    }

    /// <summary>
    /// Odczytuje magazyn (dok_MagId) dokumentu źródłowego przez bezpośredni SqlConnection -
    /// Sfera ComObject (Subiekt.Baza.PolaczenieAdoNet) nie binduje CreateCommand (patrz QueryAsync).
    /// Hard fail gdy nieczytelne: KFS MUSI cofać towar z magazynu źródłowej FS; cichy fallback na
    /// magazyn sesji jest dokładnie tym bugiem, który ta metoda eliminuje. sourceSubiektId == dok_Id
    /// (to samo id przekazywane do WczytajDokument/NaPodstawie).
    /// </summary>
    private int ReadDocumentWarehouseIdOrThrow(long documentId)
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
            cmd.CommandText = "SELECT dok_MagId FROM dok__Dokument WHERE dok_Id = @id";
            cmd.Parameters.AddWithValue("@id", documentId);
            cmd.CommandTimeout = 10;
            var result = cmd.ExecuteScalar();

            if (result == null || result == DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"Nie znaleziono dok_MagId dla dokumentu źródłowego dok_Id={documentId} - " +
                    "KFS nie może ustalić magazynu źródłowej FS.");
            }

            int warehouseId = Convert.ToInt32(result);
            if (warehouseId <= 0)
            {
                // mag_Id w sl_Magazyn startuje od 1 - wartość ≤0 to anomalia danych, nie ciche
                // SetSessionWarehouse(0). Fail-loud zamiast wystawiać KFS na nieokreślony magazyn.
                throw new InvalidOperationException(
                    $"dok_MagId={warehouseId} dla dokumentu źródłowego dok_Id={documentId} jest nieprawidłowy " +
                    "(oczekiwane mag_Id ≥ 1) - KFS nie może ustalić magazynu źródłowej FS.");
            }

            return warehouseId;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Odczyt magazynu (dok_MagId) dokumentu źródłowego dok_Id={documentId} nie powiódł się: {ex.Message}",
                ex);
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
        // "WartoscVat", NIE "WartoscPodatku" - ten drugi atrybut nie istnieje w Sferze
        // (0 trafień w całym CHM), przez co totals.vat było zawsze null (audyt 2026-06-10).
        decimal? vat = TryReadDecimal(document, "WartoscVat");

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
    /// Buduje Uwagi dokumentu tak, by NA PEWNO zawierały external_reference - na tym
    /// opiera się anty-duplikat (<see cref="FindExistingInvoiceByReference"/> szuka
    /// dok_Uwagi LIKE '%ref%'). Nie polegamy na tym, że klient sam wklei referencję
    /// do notes (kontrakt §5 obiecuje tę warstwę bezwarunkowo); doklejamy ją, jeśli
    /// jeszcze jej tam nie ma.
    /// </summary>
    private static string BuildUwagiWithReference(string? notes, string externalReference)
    {
        string baseNotes = notes ?? string.Empty;
        if (string.IsNullOrWhiteSpace(externalReference)
            || baseNotes.Contains(externalReference, StringComparison.Ordinal))
        {
            return baseNotes;
        }

        return baseNotes.Length == 0
            ? $"ref: {externalReference}"
            : $"{baseNotes} | ref: {externalReference}";
    }

    /// <summary>
    /// Ustawia datę na dokumencie, ale TYLKO gdy klient podał datę inną niż dzisiejsza.
    /// Dla domyślnego przypadku (issue_date = dziś) nie dotykamy atrybutu - Subiekt sam
    /// wpisuje bieżącą datę, więc zachowanie produkcyjne się nie zmienia. Data wsteczna
    /// (backdating, np. fakturowanie na przełomie miesiąca) była wcześniej CICHO ignorowana
    /// i dokument dostawał datę bieżącą = zły okres VAT (audyt 2026-06-10 pkt 4). Teraz
    /// ustawiamy twardo (SetCom) - ewentualna odmowa Sfery jest widocznym błędem zamiast
    /// FV ze złą datą.
    /// </summary>
    private static void SetDocumentDateIfBackdated(dynamic document, string attribute, string? isoDate)
    {
        if (string.IsNullOrWhiteSpace(isoDate))
        {
            return;
        }

        if (!DateTime.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            throw new InvalidOperationException(
                $"Nieprawidłowy format daty '{isoDate}' dla atrybutu {attribute} (oczekiwane YYYY-MM-DD).");
        }

        if (date.Date == DateTime.Today)
        {
            return;
        }

        SetCom((object)document, attribute, date.Date);
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

        var escaped = EscapeLikePattern(externalReference);
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

    private void AddLineToDocument(dynamic document, string? ean, string name, int quantity, string unit, decimal unitPriceGross, int? warehouseId = null, bool useNetPrice = false, decimal vatRate = 23m, decimal? unitPriceNet = null)
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
            // ustawione, ob_CenaBrutto wyliczone z VatProc).
            // Gdy klient podal unit_price_net wprost - uzywamy go bez przeliczania
            // (eliminuje groszowe rozjazdy zaokraglen przy cenach zakupu). W przeciwnym
            // razie przeliczamy brutto -> netto po VAT-cie z LineDto.
            decimal netPrice = unitPriceNet ?? (unitPriceGross / (1m + (vatRate / 100m)));
            position.CenaNettoPrzedRabatem = (double)netPrice;
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

    /// <summary>
    /// Ustawia magazyn roboczy sesji (Subiekt.MagazynId) na czas wystawiania dokumentu i zwraca
    /// poprzednią wartość do przywrócenia. Subiekt bierze dok_MagId z magazynu SESJI, NIE z
    /// SuPozycja.MagazynId - ustawienie per-pozycja nie zmienia magazynu dokumentu na
    /// jednomagazynowej sesji (sprawdzone empirycznie 2026-06-06: dok lądował na Głównym mimo
    /// SuPozycja.MagazynId=N). Twardy set (NIE silently jak TrySet) - lepiej nie wystawić niż
    /// wystawić na zły magazyn.
    /// </summary>
    private int? SetSessionWarehouse(int? warehouseId)
    {
        if (!warehouseId.HasValue)
        {
            return null;
        }

        // Odczyt poprzedniego magazynu MUSI się udać zanim cokolwiek przestawimy - bez
        // znanej wartości nie dałoby się go przywrócić i przestawiony magazyn wyciekałby
        // na kolejne dokumenty wystawiane bez warehouse_subiekt_id (audyt 2026-06-10 pkt 8;
        // wcześniej catch ustawiał previous=0 i restore był po cichu pomijany).
        int previous;
        try
        {
            previous = Convert.ToInt32(Session.MagazynId);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Nie udało się odczytać bieżącego magazynu sesji (Subiekt.MagazynId) - " +
                "przerwano PRZED przestawieniem magazynu, bo bez znanej poprzedniej wartości " +
                $"nie dałoby się go przywrócić po wystawieniu dokumentu. {ex.Message}", ex);
        }

        if (previous == warehouseId.Value)
        {
            return null; // magazyn już właściwy - nic do przestawiania ani przywracania
        }

        try
        {
            Session.MagazynId = warehouseId.Value;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Nie udało się ustawić magazynu sesji na {warehouseId.Value} - operator " +
                $"'{_options.Operator}' może nie mieć dostępu do tego magazynu w Subiekcie. {ex.Message}", ex);
        }

        return previous;
    }

    /// <summary>Przywraca magazyn roboczy sesji (best-effort), by nie wyciekał na kolejne dokumenty/KFS.</summary>
    private void RestoreSessionWarehouse(int? previous)
    {
        if (!previous.HasValue)
        {
            return;
        }

        try
        {
            Session.MagazynId = previous.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się przywrócić magazynu sesji do {Mag}", previous.Value);
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
        if (obj is null)
        {
            return;
        }

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
