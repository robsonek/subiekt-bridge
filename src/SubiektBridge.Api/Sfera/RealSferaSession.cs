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
/// - Sesja trzymana przez cały czas życia procesu (lazy init), auto-recreate przy crashu.
/// - Single-threaded apartment: wszystkie wywołania COM serializowane przez globalny <c>lock</c>
///   (MVP - jeden klient Laravela, niski wolumen kilka FV/min). Dla większego ruchu dorobimy
///   dedykowany STA thread + Channel queue.
///
/// Wymagania na Windowsie klienta:
/// - Subiekt GT zainstalowany (rekomendowane x64)
/// - Sfera aktywna i wykupiona dla operatora
/// - .NET 10 self-contained runtime jest wbudowany w SubiektBridge.Api.exe (nie wymaga instalacji)
/// - Bit-level binarki Bridge'a MUSI pasować do Subiekta (x64 -> x64). InsERT.GT to in-proc COM.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealSferaSession : ISferaSession
{
    private readonly SubiektOptions _options;
    private readonly ILogger<RealSferaSession> _logger;
    private readonly Encoding _cp1250;
    private readonly object _comLock = new();
    private dynamic? _subiekt;
    private DateTimeOffset? _lastInvoiceAt;
    private string? _subiektVersion;

    public RealSferaSession(SubiektOptions options, ILogger<RealSferaSession> logger)
    {
        _options = options;
        _logger = logger;

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _cp1250 = Encoding.GetEncoding(options.Encoding);
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
        _logger.LogInformation("Opening Sfera session: server={Server}, db={Db}, operator={Operator}",
            _options.Server, _options.Database, _options.Operator);

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

        return session;
    }

    // -------------------------- Health --------------------------

    public Task<SferaHealthDto> HealthAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            lock (_comLock)
            {
                try
                {
                    _ = Session; // trigger lazy open
                    return new SferaHealthDto(_subiektVersion ?? "unknown", true, _lastInvoiceAt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sfera health check failed");
                    ResetSession();
                    return new SferaHealthDto("unknown", false, _lastInvoiceAt);
                }
            }
        }, ct);
    }

    // -------------------------- Invoices --------------------------

    public Task<InvoiceResponseDto> CreateInvoiceAsync(InvoiceRequestDto request, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            lock (_comLock)
            {
                return CreateInvoiceCore(request);
            }
        }, ct);
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
        return Task.Run(() =>
        {
            lock (_comLock)
            {
                return CreateCorrectionCore(sourceSubiektId, request);
            }
        }, ct);
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
        return Task.Run<ProductDto?>(() =>
        {
            lock (_comLock)
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
            }
        }, ct);
    }

    public Task<ContractorDto?> FindContractorByNipAsync(string nip, CancellationToken ct)
    {
        return Task.Run<ContractorDto?>(() =>
        {
            lock (_comLock)
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
            }
        }, ct);
    }

    public Task<object?> InvokeRawAsync(string method, IReadOnlyList<object?> args, CancellationToken ct)
    {
        return Task.Run<object?>(() =>
        {
            lock (_comLock)
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
            }
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

    private void ResetSession()
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
        lock (_comLock)
        {
            ResetSession();
        }
        return ValueTask.CompletedTask;
    }
}
