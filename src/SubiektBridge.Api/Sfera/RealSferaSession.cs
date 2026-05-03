using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Models;

namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Prawdziwa sesja COM/Sfera Subiekt GT. Wyłącznie Windows.
///
/// Architektura:
/// - <c>dynamic</c> + <c>Type.GetTypeFromProgID("InsERT.GT")</c> - late binding, bez TLB.
/// - Encoding pól tekstowych: UTF-8 -> Windows-1250 (CP1250 wymaga CodePagesEncodingProvider).
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
    private readonly Encoding _cp1250;
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

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _cp1250 = Encoding.GetEncoding(options.Encoding);

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

    // -------------------------- Invoices --------------------------

    public Task<InvoiceResponseDto> CreateInvoiceAsync(InvoiceRequestDto request, CancellationToken ct)
    {
        return RunOnStaAsync(() => CreateInvoiceCore(request), ct);
    }

    private InvoiceResponseDto CreateInvoiceCore(InvoiceRequestDto request)
    {
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

            foreach (var line in request.Lines)
            {
                AddLineToDocument(fs, line.Ean, line.NameFallback, line.Quantity, line.Unit, line.UnitPriceGross);
            }

            if (request.Shipping.Include)
            {
                AddShippingLineToDocument(fs, request.Shipping);
            }

            ApplyPayment(fs, request.Payment);
            fs.Rozliczony = request.Payment.IsSettled;
            fs.Uwagi = EncodeForSfera(request.Notes ?? string.Empty);

            fs.Zapisz();

            long subiektId = ToInt64(fs.Identyfikator);
            string number = (string)fs.NumerPelny;
            DateTimeOffset issuedAt = DateTimeOffset.UtcNow;
            _lastInvoiceAt = issuedAt;

            string? pdfBase64 = TryGeneratePdf(fs, subiektId);

            // Net/VAT po zapisaniu - Sfera wylicza bo LiczonyOdCenBrutto=true
            decimal net = TryReadDecimal(fs, "WartoscNetto") ?? Math.Round(request.Totals.Gross / 1.23m, 2);
            decimal vat = TryReadDecimal(fs, "WartoscPodatku") ?? (request.Totals.Gross - net);
            decimal gross = TryReadDecimal(fs, "WartoscBrutto") ?? request.Totals.Gross;

            return new InvoiceResponseDto(
                Id: $"sub_{subiektId}",
                SubiektId: subiektId,
                Number: number,
                IssuedAt: issuedAt,
                ContractorSubiektId: contractorId,
                Totals: new InvoiceTotalsDto(net, vat, gross),
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
                    kfs.DoDokumentuNumerPelny = EncodeForSfera(request.SourceInvoiceNumber);
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

            kfs.Uwagi = EncodeForSfera($"Korekta: {request.Reason} | ref: {request.ExternalReference}");

            kfs.Zapisz();

            long subiektId = ToInt64(kfs.Identyfikator);
            string number = (string)kfs.NumerPelny;
            var issuedAt = DateTimeOffset.UtcNow;
            _lastInvoiceAt = issuedAt;

            string? pdfBase64 = TryGeneratePdf(kfs, subiektId);

            decimal net = TryReadDecimal(kfs, "WartoscNetto") ?? 0m;
            decimal vat = TryReadDecimal(kfs, "WartoscPodatku") ?? 0m;
            decimal gross = TryReadDecimal(kfs, "WartoscBrutto") ?? 0m;

            return new InvoiceResponseDto(
                Id: $"sub_{subiektId}",
                SubiektId: subiektId,
                Number: number,
                IssuedAt: issuedAt,
                ContractorSubiektId: 0,
                Totals: new InvoiceTotalsDto(net, vat, gross),
                PdfUrl: null,
                PdfBase64: pdfBase64);
        }
        finally
        {
            TryClose(kfs);
        }
    }

    // -------------------------- Lookup --------------------------

    public Task<ProductDto?> FindProductByEanAsync(string ean, CancellationToken ct)
    {
        return RunOnStaAsync<ProductDto?>(() =>
        {
            string symbol = EncodeForSfera(ean);
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
            string symbol = EncodeForSfera(nip);
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
        string symbol = EncodeForSfera(c.Symbol);

        if ((bool)Session.Kontrahenci.Istnieje(symbol))
        {
            dynamic existing = Session.Kontrahenci.Wczytaj(symbol);
            try
            {
                return ToInt64(existing.Identyfikator);
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
            kh.Nazwa = EncodeForSfera(c.Name);
            kh.NazwaPelna = EncodeForSfera(c.FullName ?? c.Name);

            if (!string.IsNullOrEmpty(c.Nip))
            {
                kh.NIP = c.Nip;
            }

            kh.Miejscowosc = EncodeForSfera(c.Address.City);
            kh.KodPocztowy = c.Address.PostCode;
            kh.Ulica = EncodeForSfera(c.Address.Street);

            if (!string.IsNullOrEmpty(c.Email))
            {
                TrySet(kh, "AdresEMail", c.Email);
            }

            kh.Zapisz();
            return ToInt64(kh.Identyfikator);
        }
        finally
        {
            TryClose(kh);
        }
    }

    private void AddLineToDocument(dynamic document, string? ean, string name, int quantity, string unit, decimal unitPriceGross)
    {
        dynamic position;

        bool useTowar = !string.IsNullOrEmpty(ean) && (bool)Session.Towary.Istnieje(EncodeForSfera(ean));

        if (useTowar)
        {
            dynamic towar = Session.Towary.Wczytaj(EncodeForSfera(ean!));
            try
            {
                position = document.Pozycje.Dodaj(towar);
            }
            finally
            {
                TryClose(towar);
            }
        }
        else
        {
            position = document.Pozycje.DodajUslugeJednorazowa();
            position.UslJednNazwa = EncodeForSfera(name);
        }

        position.IloscJm = quantity;
        position.Jm = string.IsNullOrEmpty(unit) ? "szt." : unit;
        position.CenaBruttoPrzedRabatem = (double)unitPriceGross;
    }

    private void AddShippingLineToDocument(dynamic document, ShippingDto shipping)
    {
        dynamic position = document.Pozycje.DodajUslugeJednorazowa();
        position.UslJednNazwa = EncodeForSfera(shipping.Name);
        position.IloscJm = 1;
        position.Jm = "szt.";
        position.CenaBruttoPrzedRabatem = (double)shipping.UnitPriceGross;
    }

    private void ApplyPayment(dynamic document, PaymentDto payment)
    {
        // payment.Attribute = "PlatnoscPrzelew" -> setujemy "PlatnoscPrzelewId" + "PlatnoscPrzelewKwota"
        TrySet(document, payment.Attribute + "Id", payment.MethodSubiektId);
        TrySet(document, payment.Attribute + "Kwota", (double)payment.Amount);
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

    private void TryClose(dynamic obj)
    {
        try { obj.Zamknij(); } catch { /* obiekt już zamknięty albo metoda niedostępna */ }
        try { Marshal.ReleaseComObject(obj); } catch { /* ignore */ }
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

    /// <summary>UTF-8 z JSONa -> string CP1250 dla COM Sfery.</summary>
    public string EncodeForSfera(string utf8Input)
    {
        if (string.IsNullOrEmpty(utf8Input)) return utf8Input;
        var bytes = _cp1250.GetBytes(utf8Input);
        return _cp1250.GetString(bytes);
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
