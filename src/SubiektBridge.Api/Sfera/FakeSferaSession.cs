using System.Globalization;
using System.Runtime.InteropServices;
using SubiektBridge.Api.Models;

namespace SubiektBridge.Api.Sfera;

/// <summary>
/// Mock Sfery dla dev na macOS/Linux i testów. Generuje deterministyczne odpowiedzi
/// żeby Laravel-side mógł testować pełen happy path bez Subiekta.
///
/// W production NIE używamy - <c>RealSferaSession</c> bierze przy <c>UseFakeSfera=false</c>.
/// </summary>
public sealed class FakeSferaSession : ISferaSession
{
    private static int _invoiceCounter = 0;

    /// <summary>
    /// Test-only: id dokumentu, ktorego Fake "pozytywnie nie zna" (404). Ujemne id tez, ale od v0.21.0 nie przechodza przez
    /// BridgeIds (1..int.MaxValue, 422 INVALID_BRIDGE_ID) - testy kontrolerow uzywaja tego sentinela.
    /// </summary>
    internal const long NotFoundIdForTests = 999_999_999;
    private static bool IsMissingDocument(long id) => id < 0 || id == NotFoundIdForTests;
    private DateTimeOffset? _lastInvoiceAt;

    public Task<InvoiceResponseDto> CreateInvoiceAsync(InvoiceRequestDto request, CancellationToken ct)
    {
        if (SferaUnavailableForTests) throw new SferaUnavailableException("Sesja Sfery niedostepna (fake) - nic nie zapisano");
        ThrowIfDuplicateCheckFails(request.ExternalReference, "FS");
        ThrowIfContractorLookupFails(request.Contractor);
        ThrowIfSymbolInvalidWithoutNip(request.Contractor);
        var counter = Interlocked.Increment(ref _invoiceCounter);
        var year = DateTimeOffset.UtcNow.Year;
        _lastInvoiceAt = DateTimeOffset.UtcNow;

        var net = Math.Round(request.Totals.Gross / 1.23m, 2);
        var vat = request.Totals.Gross - net;

        return Task.FromResult(new InvoiceResponseDto(
            Id: $"fake_inv_{counter:D6}",
            SubiektId: 1_000_000 + counter,
            Number: $"FS {counter}/{year}",
            IssuedAt: _lastInvoiceAt.Value,
            ContractorSubiektId: HashString(request.Contractor.Symbol) % 100000,
            Totals: new InvoiceTotalsDto(Net: net, Vat: vat, Gross: request.Totals.Gross),
            PdfUrl: $"/api/v1/invoices/fake_inv_{counter:D6}/pdf",
            PdfBase64: null));
    }

    public Task<InvoiceResponseDto> CreateCorrectionAsync(
        long sourceSubiektId,
        InvoiceCorrectionRequestDto request,
        CancellationToken ct)
    {
        if (SferaUnavailableForTests) throw new SferaUnavailableException("Sesja Sfery niedostepna (fake) - nic nie zapisano");
        ThrowIfDuplicateCheckFails(request.ExternalReference, "KFS");
        var counter = Interlocked.Increment(ref _invoiceCounter);
        var year = DateTimeOffset.UtcNow.Year;
        _lastInvoiceAt = DateTimeOffset.UtcNow;

        decimal grossSum = 0m;
        foreach (var line in request.Lines)
        {
            grossSum += line.UnitPriceGross * line.QuantityChange;
        }
        var net = Math.Round(grossSum / 1.23m, 2);
        var vat = grossSum - net;

        return Task.FromResult(new InvoiceResponseDto(
            Id: $"fake_kfs_{counter:D6}",
            SubiektId: 2_000_000 + counter,
            Number: $"KFS {counter}/{year}",
            IssuedAt: _lastInvoiceAt.Value,
            // Kontrahent KFS = kontrahent (płatnik) FS źródłowej - Real czyta KontrahentId po NaPodstawie.
            // Deterministycznie z id źródła (fake nie zna symbolu kontrahenta FS); 0 = "nieznany" (sentinel).
            ContractorSubiektId: 1_000 + sourceSubiektId % 100_000,
            Totals: new InvoiceTotalsDto(Net: net, Vat: vat, Gross: grossSum),
            PdfUrl: $"/api/v1/invoices/fake_kfs_{counter:D6}/pdf",
            PdfBase64: null));
    }

    /// <summary>Symbol mocka z końcówki EAN; krótki EAN (1–3 zn.) w całości - wycinek [^4..] bez kontroli długości rzucał (Greptile PR #20).</summary>
    private static string MockSymbol(string ean) => "SKU-" + (ean.Length > 4 ? ean[^4..] : ean);

    public Task<ProductDto?> FindProductByEanAsync(string ean, CancellationToken ct)
    {
        // Deterministyczny mock: EAN-y zaczynające się na "5901" są "znane".
        if (string.IsNullOrEmpty(ean) || !ean.StartsWith("5901"))
        {
            return Task.FromResult<ProductDto?>(null);
        }

        return Task.FromResult<ProductDto?>(new ProductDto(
            SubiektId: HashString(ean) % 100000,
            Symbol: MockSymbol(ean),
            Ean: ean,
            Name: $"Mock product {ean}",
            VatRate: 23m,
            Unit: "szt.",
            IsActive: true));
    }

    public Task<InvoiceResponseDto> CreateReceiptAsync(ReceiptIssueRequestDto request, CancellationToken ct)
    {
        if (SferaUnavailableForTests) throw new SferaUnavailableException("Sesja Sfery niedostepna (fake) - nic nie zapisano");
        ThrowIfDuplicateCheckFails(request.ExternalReference, "PZ");
        ThrowIfContractorLookupFails(request.Supplier);
        ThrowIfSymbolInvalidWithoutNip(request.Supplier);
        var counter = Interlocked.Increment(ref _invoiceCounter);
        var year = DateTimeOffset.UtcNow.Year;
        _lastInvoiceAt = DateTimeOffset.UtcNow;

        decimal grossSum = 0m;
        foreach (var line in request.Lines)
        {
            // Gdy klient podal cene netto wprost (opcja PZ), liczymy brutto z niej + VAT;
            // inaczej bierzemy unit_price_gross. Lustro logiki RealSferaSession dla PZ.
            decimal lineGross = line.UnitPriceNet.HasValue
                ? line.UnitPriceNet.Value * (1m + line.VatRate / 100m)
                : line.UnitPriceGross;
            grossSum += lineGross * line.Quantity;
        }

        long contractorId = HashString(request.Supplier.Symbol) % 100000;
        RegisterReceipt(3_000_000 + counter, request, counter, $"PZ {counter}/{year}", contractorId);

        return Task.FromResult(new InvoiceResponseDto(
            Id: $"fake_pz_{counter:D6}",
            SubiektId: 3_000_000 + counter,
            Number: $"PZ {counter}/{year}",
            IssuedAt: _lastInvoiceAt.Value,
            ContractorSubiektId: contractorId,
            Totals: new InvoiceTotalsDto(Net: grossSum, Vat: 0m, Gross: grossSum),
            PdfUrl: $"/api/v1/receipts/fake_pz_{counter:D6}/pdf",
            PdfBase64: null));
    }

    public Task<TransferResponseDto> CreateTransferAsync(TransferRequestDto request, CancellationToken ct)
    {
        if (SferaUnavailableForTests) throw new SferaUnavailableException("Sesja Sfery niedostepna (fake) - nic nie zapisano");
        ThrowIfDuplicateCheckFails(request.ExternalReference, "MM");
        var counter = Interlocked.Increment(ref _invoiceCounter);
        var year = DateTimeOffset.UtcNow.Year;
        _lastInvoiceAt = DateTimeOffset.UtcNow;

        return Task.FromResult(new TransferResponseDto(
            Id: $"fake_mm_{counter:D6}",
            SubiektId: 4_000_000 + counter,
            Number: $"MM {counter}/{year}",
            IssuedAt: _lastInvoiceAt.Value,
            SourceWarehouseId: request.SourceWarehouseId,
            DestWarehouseId: request.DestWarehouseId));
    }

    public Task<ContractorDto?> FindContractorByNipAsync(string nip, CancellationToken ct)
    {
        // Mock: konkretny NIP testowy "5252344078" istnieje.
        if (nip != "5252344078")
        {
            return Task.FromResult<ContractorDto?>(null);
        }

        return Task.FromResult<ContractorDto?>(new ContractorDto(
            IsPerson: false,
            Symbol: nip,
            Nip: nip,
            Name: "Mock Test Sp. z o.o.",
            FullName: "Mock Test Spółka z ograniczoną odpowiedzialnością",
            FirstName: null,
            LastName: null,
            Email: "mock@example.com",
            Address: new AddressDto(
                Street: "ul. Testowa 1",
                PostCode: "00-001",
                City: "Warszawa",
                CountryCode: "PL")));
    }

    public Task<IReadOnlyList<InvoiceQueryItemDto>> QueryInvoicesAsync(InvoiceQueryRequestDto request, CancellationToken ct)
    {
        // Mock: 2 sample FV w odpowiedzi.
        IReadOnlyList<InvoiceQueryItemDto> items = new[]
        {
            new InvoiceQueryItemDto(
                SubiektId: 50001,
                Number: "FS 1/2026",
                Type: "FS",
                IssueDate: "2026-01-15",
                ContractorId: 1,
                ContractorNip: "1234567890",
                ContractorName: "Mock Klient Sp. z o.o.",
                NetAmount: 100.37m,
                VatAmount: 23.08m,
                GrossAmount: 123.45m,
                Notes: "Zamowienie test-1"),
            new InvoiceQueryItemDto(
                SubiektId: 50002,
                Number: "FS 2/2026",
                Type: "FS",
                IssueDate: "2026-01-16",
                ContractorId: 2,
                ContractorNip: null,
                ContractorName: "Mock Osoba",
                NetAmount: 55.20m,
                VatAmount: 12.69m,
                GrossAmount: 67.89m,
                Notes: "Zamowienie test-2"),
        };
        return Task.FromResult(items);
    }

    public Task<InvoiceQueryItemDto?> FindInvoiceByIdAsync(long subiektId, CancellationToken ct)
    {
        if (SferaUnavailableForTests) throw new SferaUnavailableException("Sesja Sfery niedostepna (fake)");
        return Task.FromResult<InvoiceQueryItemDto?>(new InvoiceQueryItemDto(
            SubiektId: subiektId,
            Number: $"FS {subiektId}/2026",
            Type: "FS",
            IssueDate: "2026-01-15",
            ContractorId: 1,
            ContractorNip: "1234567890",
            ContractorName: "Mock Klient",
            NetAmount: 81.30m,
            VatAmount: 18.70m,
            GrossAmount: 100.00m,
            Notes: "fake"));
    }

    public Task<IReadOnlyList<OpenReceivableDto>> QueryOpenReceivablesAsync(OpenReceivablesQueryRequestDto request, CancellationToken ct)
    {
        // Deterministyczne otwarte naleznosci (rozrachunki sprzedazy z otwartym saldem) + data (nzf_Data) do
        // testu filtra from/to. Most NIE matchuje - zwraca okno; filtr min/max/contractor/from/to/limit lustrem
        // RealSferaSession. TYLKO PLN: 51900 (EUR) jest tu po to, by potwierdzic ze most go WYKLUCZA (Real:
        // nzf_IdWaluty='PLN' w filtrze + controller odrzuca currency!=PLN). 52001 (ten sam kontrahent 13292 co
        // 53447, starsza data) sprawdza filtr contractor_id ORAZ from/to.
        // Date/Original/Nip w DTO: data (nzf_Data) do testu filtra from/to ORAZ do prezentacji u klienta;
        // original (WartoscPoczatkowa, brutto) i nip (Kontrahenci.NIP) - dane decyzyjne operatora. 53310 ma
        // nip=null (parytet z B2C/osoba prywatna - brak NIP); 51900 (EUR) jest tu by potwierdzic wykluczenie.
        OpenReceivableDto[] all =
        {
            new("sub_53447", 53447, "FS", "PLN", 3372.50m, 13292, "Szyszka Krzysztof", "FS 573/05/2026", "2026-06-12", 3372.50m, "1234563218"),
            new("sub_53310", 53310, "FS", "PLN", 371.12m, 14001, "Google Commerce Limited", "FS 540/05/2026", "2026-06-11", 382.60m, null),
            new("sub_52001", 52001, "FS", "PLN", 120.00m, 13292, "Szyszka Krzysztof", "FS 410/04/2026", "2026-04-15", 120.00m, "1234563218"),
            new("sub_51900", 51900, "FS", "EUR", 99.00m, 15000, "Mock Foreign GmbH", "FS 300/03/2026", "2026-03-01", 99.00m, "DE123456789"),
            // Wiersz KOLIZYJNY (adwersaryjny dla Search_ContractorScope_TakesPrecedenceOverNumber): OBCY
            // kontrahent (16100 "Nowak Anna" - NIE pasuje do "Szyszka" ani nazwa, ani NIP), ale NUMER FV
            // zawiera "szyszka". Gdy fraza "Szyszka" trafia w scope kontrahenta 13292, ten wiersz NIE moze
            // wrocic - number-path jest WYKLUCZONY (nie OR-owany ze scope). Twardy dowod precedencji scope.
            new("sub_53500", 53500, "FS", "PLN", 250.00m, 16100, "Nowak Anna", "FS szyszka-99/06/2026", "2026-06-13", 250.00m, "9999999999"),
        };

        return Task.FromResult(FilterOpenSettlements(all, request));
    }

    public Task<IReadOnlyList<OpenReceivableDto>> QueryOpenPayablesAsync(OpenReceivablesQueryRequestDto request, CancellationToken ct)
    {
        // Deterministyczne otwarte ZOBOWIAZANIA (FZ, nzf_Typ=40). Lustro receivables: PLN-only, okno
        // kwoty/kontrahent/data/limit + parytet wyszukiwarki (contractor-scope albo number-path).
        OpenReceivableDto[] all =
        {
            new("sub_60010", 60010, "FZ", "PLN", 1500.00m, 5001, "Sumernet Sp. z o.o.", "FZ 120/05/2026", "2026-05-20", 1500.00m, "5223044881"),
            new("sub_60011", 60011, "FZ", "PLN", 430.00m, 5002, "MPL Power Sp. z o.o.", "FZ 88/05/2026", "2026-05-18", 430.00m, "7282836136"),
            new("sub_60012", 60012, "FZ", "PLN", 1500.00m, 5001, "Sumernet Sp. z o.o.", "FZ 95/04/2026", "2026-04-30", 1500.00m, "5223044881"),
            new("sub_60013", 60013, "FZ", "EUR", 200.00m, 5099, "Foreign Supplier GmbH", "FZ 5/03/2026", "2026-03-10", 200.00m, "DE987654321"),
            // Wiersz KOLIZYJNY (adwersaryjny dla Search_ContractorScope_TakesPrecedenceOverNumber): OBCY
            // kontrahent (5199 "Nowak Logistyka" - NIE pasuje do "Sumernet" ani nazwa, ani NIP), ale NUMER FZ
            // zawiera "sumernet". Gdy fraza "Sumernet" trafia w scope kontrahenta 5001, ten wiersz NIE moze
            // wrocic - number-path jest WYKLUCZONY (nie OR-owany ze scope). Kwota 9999 jest poza oknem
            // [100,5000] testu Payables_ReturnsOnlyFzRows_InAmountWindow, by go nie popsuc.
            new("sub_60014", 60014, "FZ", "PLN", 9999.00m, 5199, "Nowak Logistyka", "FZ sumernet-9/06/2026", "2026-06-09", 9999.00m, "8888888888"),
        };
        return Task.FromResult(FilterOpenSettlements(all, request));
    }

    // Parytet z RealSferaSession.ResolveOrCreateContractor: bez NIP Real od razu idzie po Symbolu i waliduje
    // go (z NIP-em waliduje dopiero po chybionym lookupie - Fake nie ma bazy kontrahentow, wiec tego nie odtwarza).
    private static void ThrowIfSymbolInvalidWithoutNip(ContractorDto contractor)
    {
        if (string.IsNullOrEmpty(contractor.Nip) && ContractorFields.ValidateSymbol(contractor.Symbol) is { } error)
        {
            throw new InvalidContractorSymbolException(contractor.Symbol, error);
        }
    }

    // Test-only: symuluje padniety lookup po NIP (RealSferaSession.FindContractorIdByNip -> SQL error).
    internal bool FailContractorLookupForTests { get; set; }

    // Test-only: symuluje awarie skanu duplikatow po external_reference (SQL/COM/deadline) - Real rzuca
    // DuplicateCheckUnavailableException PRZED pierwszym zapisem (fail-closed, 503 DUPLICATE_CHECK_UNAVAILABLE).
    internal bool FailDuplicateCheckForTests { get; set; }

    private void ThrowIfDuplicateCheckFails(string externalReference, string documentType)
    {
        if (FailDuplicateCheckForTests)
        {
            throw new DuplicateCheckUnavailableException(externalReference, documentType, new InvalidOperationException("symulowany blad skanu duplikatow"));
        }
    }

    // Test-only: symuluje martwa sesje Sfery - przy odczycie (FindInvoiceByIdAsync: sonda po wyjatku), na wejsciu
    // KAZDEJ mutacji (Real: preflight EnsureSessionForMutation, nic nie zapisano -> 503 SUBIEKT_UNAVAILABLE) i w health.
    internal bool SferaUnavailableForTests { get; set; }

    // Real pyta SQL tylko dla kontrahenta z NIP-em - tak samo tu.
    private void ThrowIfContractorLookupFails(ContractorDto contractor)
    {
        if (FailContractorLookupForTests && !string.IsNullOrEmpty(contractor.Nip))
        {
            throw new ContractorLookupUnavailableException(contractor.Nip, new InvalidOperationException("symulowany blad SQL"));
        }
    }

    // Wspolny filtr in-memory dla open-receivables (FS) i open-payables (FZ) - parytet z RealSferaSession
    // QueryOpenSettlementsCore (rozne strony rozni TYLKO zbior 'all'; semantyka filtra identyczna):
    // PLN-only, okno kwoty (WartoscBiezaca > 0 + min/max), kontrahent, okno daty (from/to), wyszukiwarka
    // (contractor-scope-albo-number) i limit. Wydzielone (DRY) - obie metody wolaja ten sam kod.
    private static IReadOnlyList<OpenReceivableDto> FilterOpenSettlements(
        OpenReceivableDto[] all, OpenReceivablesQueryRequestDto request)
    {
        decimal min = request.MinAmount ?? 0m;
        decimal max = request.MaxAmount ?? decimal.MaxValue;
        int limit = Math.Clamp(request.Limit > 0 ? request.Limit : 50, 1, 200);

        // Daty ISO (yyyy-MM-dd) porownywalne leksykograficznie = chronologicznie.
        static bool IsIso(string? d) => !string.IsNullOrWhiteSpace(d)
            && DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

        // Tylko PLN (parytet z Real: filtr nzf_IdWaluty='PLN'; controller i tak odrzuca currency!=PLN).
        IEnumerable<OpenReceivableDto> q = all
            .Where(r => r.Remaining > 0.005m)
            .Where(r => r.Remaining >= min && r.Remaining <= max)
            .Where(r => string.Equals(r.Currency, "PLN", StringComparison.OrdinalIgnoreCase));
        if (request.ContractorId.HasValue) q = q.Where(r => r.ContractorId == request.ContractorId.Value);
        if (IsIso(request.From)) q = q.Where(r => string.CompareOrdinal(r.Date, request.From) >= 0);
        if (IsIso(request.To)) q = q.Where(r => string.CompareOrdinal(r.Date, request.To) <= 0);

        // Wyszukiwarka (v0.14.0) - parytet z Real: contractor-scope-albo-number.
        // Real: SQL LIKE nazwa/NIP -> kh_Id; jesli niepuste, zawez OtworzKolekcje nzf_IdObiektu IN (...) (match
        // po kontrahencie); jesli puste (fraza = numer FV), skanuj po numerze. Fake nie ma SQL, wiec liczy
        // scope z danych: kontrahenci pasujacy nazwa/NIP -> tylko ich wiersze; brak -> match po numerze.
        //
        // OGRANICZENIE PARYTETU (akceptowane): Real wyznacza scope SQL-em na CALEJ tabeli kh__Kontrahent
        // (FindOpenReceivableContractorIds) - niezaleznie od tego, czy kontrahent ma otwarta naleznosc. Fake
        // NIE ma globalnej bazy klientow, wiec aproksymuje scope tylko z 'all' (otwarte naleznosci). Rozjazd
        // wystepuje wylacznie w sztucznym przypadku brzegowym: fraza pasuje do NAZWY/NIP kontrahenta, ktory
        // ISTNIEJE w bazie, ale NIE MA otwartej naleznosci w oknie, a JEDNOCZESNIE jest fragmentem numeru
        // obcej, otwartej FV. Wtedy Real: contractorScope=true -> IN(<kh bez naleznosci>) -> PUSTO (number-path
        // nigdy nie wchodzi); Fake: scope puste -> number-path -> zwraca te obca FV. Fake jest tu liberalniejszy.
        // Niewykrywalne na CI (Real = COM/SQL windows-only). Produkcyjnie nieistotne: operator szuka po nazwie
        // KLIENTA Z OTWARTA FV (oba zwracaja to samo) lub po numerze FV (oba number-path). Fake celowo NIE
        // modeluje "kontrahent znany w bazie, ale bez otwartej naleznosci" - to wymagaloby drugiej tabeli mock.
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var scopeIds = all
                .Where(r => r.ContractorId.HasValue
                    && OpenReceivableFields.MatchesSearch(request.Search, null, r.ContractorName, r.Nip))
                .Select(r => r.ContractorId!.Value)
                .ToHashSet();

            q = scopeIds.Count > 0
                ? q.Where(r => r.ContractorId.HasValue && scopeIds.Contains(r.ContractorId.Value))
                : q.Where(r => OpenReceivableFields.MatchesSearch(request.Search, r.Number, null, null));
        }

        return q.Take(limit).ToList();
    }

    public Task<byte[]?> GetInvoicePdfAsync(long subiektId, CancellationToken ct)
    {
        // Minimal valid empty PDF stream
        var minimalPdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%fake bridge\n");
        return Task.FromResult<byte[]?>(minimalPdf);
    }

    public Task<QueryResultDto> QueryAsync(string sql, int maxRows, CancellationToken ct)
    {
        return Task.FromResult(new QueryResultDto(
            Columns: new[] { "fake_col" },
            Rows: new IReadOnlyList<object?>[] { new object?[] { "fake_value" } },
            Truncated: false));
    }

    public Task<SferaHealthDto> HealthAsync(CancellationToken ct)
    {
        // Real sonduje sesje (IsSessionAlive) - martwa daje SessionActive=false (503 z /health), nie "active".
        if (SferaUnavailableForTests)
            return Task.FromResult(new SferaHealthDto("unknown", false, _lastInvoiceAt, "SferaUnavailableException: sesja martwa (fake)", true));
        return Task.FromResult(new SferaHealthDto(
            SubiektVersion: "FAKE-1.78.0",
            SessionActive: true,
            LastInvoiceAt: _lastInvoiceAt,
            LastError: null,
            SqlConnectionOk: true));
    }

    public Task<object?> InvokeRawAsync(string method, IReadOnlyList<object?> args, CancellationToken ct)
    {
        return Task.FromResult<object?>(new
        {
            method,
            args,
            note = "Fake response - real Sfera tylko na Windowsie"
        });
    }

    // ----------------------------- Settlements (rozliczenia) -----------------------------
    //
    // Minimalny in-memory store, żeby anti-duplicate, częściowe rozliczenia, idempotency-replay
    // i GET były spójne w obrębie procesu (dev/test). Stan GLOBALNY (jak _invoiceCounter) -
    // testy muszą wołać ResetSettlementsForTests() i używać unikalnych documentSubiektId.
    //
    // Sentinele documentSubiektId (deterministyczne mapowanie błędów dla testów controllera):
    //   < 0              -> dokument nie istnieje (POST/DELETE: DocumentNotFound; GET: null)
    //   >= 3_000_000     -> dokument bez rozrachunku (PZ magazynowy) -> NoRozrachunek
    //   2_000_000..2_999_999 -> typ nieobsługiwany (np. korekta KFS/KFZ) -> UnsupportedDocumentType
    //   == 1_900_001     -> rozrachunek już rozliczony (Remaining=0) -> AlreadySettled (w zakresie FS, <2M)
    //   == 1_900_002     -> DuplicateSettlement z existing_rozliczenie_id = null (numer nieodczytany, W5)
    // Sentinele bank_operation_subiekt_id:
    //   < 0  -> BankOperationNotFound
    //   == 0 -> BankOperationExhausted
    // SWIADOME ograniczenia: nie symuluje walut (zawsze PLN), metody kasowej, guardu kontrahenta BP.

    private static long _settlementCounter = 0;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, FakeRozrachunek> _settlements = new();

    private sealed class FakeRozrachunek
    {
        public decimal Original;
        public decimal Remaining;
        public List<FakeRozliczenie> Lines = new();
    }

    private sealed class FakeRozliczenie
    {
        public long RozliczenieId;
        public long BankOpId;
        public decimal Amount;
        public DateTimeOffset At;
    }

    /// <summary>Reset globalnego stanu rozliczeń - wołać w ctor/fixture testu.</summary>
    internal static void ResetSettlementsForTests()
    {
        _settlements.Clear();
        Interlocked.Exchange(ref _settlementCounter, 0);
    }

    /// <summary>
    /// Wstrzykuje linię rozliczenia BEZ przejścia przez Create - symuluje crash między
    /// Zapisz a SaveAsync (utrwalone w Subiekcie, klucz idempotency niezapisany).
    /// </summary>
    internal static void InjectSettlementLineForTests(long documentSubiektId, long bankOpId, decimal amount)
    {
        var r = _settlements.GetOrAdd(documentSubiektId, _ => new FakeRozrachunek { Original = 100m, Remaining = 100m });
        lock (r)
        {
            var id = Interlocked.Increment(ref _settlementCounter);
            r.Lines.Add(new FakeRozliczenie { RozliczenieId = id, BankOpId = bankOpId, Amount = amount, At = DateTimeOffset.UtcNow });
            r.Remaining -= amount;
        }
    }

    public Task<SettlementResponseDto> CreateSettlementAsync(long documentSubiektId, SettlementCreateRequestDto request, CancellationToken ct)
    {
        if (SferaUnavailableForTests) throw new SferaUnavailableException("Sesja Sfery niedostepna (fake) - nic nie zapisano");
        if (request.Amount <= 0m) throw new SettlementException(SettlementError.InvalidAmount, "amount musi byc > 0");
        decimal amount = Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero);

        if (IsMissingDocument(documentSubiektId)) throw new SettlementException(SettlementError.DocumentNotFound, $"Dokument {documentSubiektId} nie istnieje");
        if (documentSubiektId >= 3_000_000) throw new SettlementException(SettlementError.NoRozrachunek, "Dokument bez rozrachunku (PZ magazynowy)");
        if (documentSubiektId is >= 2_000_000 and < 3_000_000) throw new SettlementException(SettlementError.UnsupportedDocumentType, "Typ nieobslugiwany (np. korekta)");
        // 1_900_002: duplikat wykryty po SplataId, ale RozliczenieId linii nieodczytany -> existing_rozliczenie_id = null (W5).
        if (documentSubiektId == 1_900_002) throw new DuplicateSettlementException(null, documentSubiektId, request.BankOperationSubiektId);
        if (request.BankOperationSubiektId < 0) throw new SettlementException(SettlementError.BankOperationNotFound, $"Operacja bankowa {request.BankOperationSubiektId} nie istnieje");
        // Parytet z Real (guard nzf_Typ IN (19,20)): id >= 9_000_000 = wiersz nz__Finanse, ktory nie jest BP/BW.
        if (request.BankOperationSubiektId >= 9_000_000) throw new SettlementException(SettlementError.UnsupportedBankOperationType, $"nz__Finanse {request.BankOperationSubiektId} nie jest operacja bankowa BP/BW");

        var r = _settlements.GetOrAdd(documentSubiektId, id => new FakeRozrachunek
        {
            Original = 100m,
            Remaining = id == 1_900_001 ? 0m : 100m,
        });

        lock (r)
        {
            // Parytet z Real: duplikat ma pierwszenstwo nad AlreadySettled - retry PELNEGO rozliczenia musi dac
            // 409 z existing_rozliczenie_id (auto-recovery), nie 422.
            var dup = r.Lines.FirstOrDefault(l => l.BankOpId == request.BankOperationSubiektId);
            if (dup is not null)
            {
                throw new DuplicateSettlementException(dup.RozliczenieId, documentSubiektId, request.BankOperationSubiektId);
            }

            // Parytet z Real: saldo BP sprawdzane PO skanie duplikatow (po pelnym rozliczeniu 1:1 saldo BP = 0).
            if (request.BankOperationSubiektId == 0) throw new SettlementException(SettlementError.BankOperationExhausted, "Operacja bankowa skonsumowana");
            if (r.Remaining <= 0.005m) throw new SettlementException(SettlementError.AlreadySettled, "Rozrachunek juz rozliczony");

            if (amount - r.Remaining > 0.005m)
            {
                throw new SettlementException(SettlementError.AmountExceedsRemaining, $"amount {amount:F2} > pozostalo {r.Remaining:F2}");
            }

            var id = Interlocked.Increment(ref _settlementCounter);
            r.Remaining -= amount;
            var at = DateTimeOffset.UtcNow;
            r.Lines.Add(new FakeRozliczenie { RozliczenieId = id, BankOpId = request.BankOperationSubiektId, Amount = amount, At = at });
            _lastInvoiceAt = at;

            return Task.FromResult(new SettlementResponseDto(
                RozliczenieId: id,
                DocumentId: $"sub_{documentSubiektId}",
                DocumentSubiektId: documentSubiektId,
                RozrachunekSubiektId: documentSubiektId + 500_000,
                BankOperationSubiektId: request.BankOperationSubiektId,
                Amount: amount,
                RemainingAfter: r.Remaining,
                IsFullySettled: Math.Abs(r.Remaining) < 0.005m,
                SettledAt: at));
        }
    }

    public Task<SettlementStateResponseDto?> GetSettlementsAsync(long documentSubiektId, CancellationToken ct)
    {
        if (IsMissingDocument(documentSubiektId)) return Task.FromResult<SettlementStateResponseDto?>(null);
        if (documentSubiektId >= 3_000_000) throw new SettlementException(SettlementError.NoRozrachunek, "Dokument bez rozrachunku");

        if (!_settlements.TryGetValue(documentSubiektId, out var r))
        {
            // Dokument istnieje, brak rozliczen -> pusty stan (spojnie z FindInvoiceById, ktory zawsze zwraca rekord).
            return Task.FromResult<SettlementStateResponseDto?>(new SettlementStateResponseDto(
                $"sub_{documentSubiektId}", documentSubiektId, documentSubiektId + 500_000, 100m, 100m, false, null, Array.Empty<SettlementLineDto>()));
        }

        lock (r)
        {
            var lines = r.Lines
                .Select(l => new SettlementLineDto(l.RozliczenieId, l.Amount, l.At, l.BankOpId, documentSubiektId + 500_000, 1))
                .ToList();
            DateTimeOffset? last = r.Lines.Count > 0 ? r.Lines.Max(l => l.At) : null;
            return Task.FromResult<SettlementStateResponseDto?>(new SettlementStateResponseDto(
                $"sub_{documentSubiektId}", documentSubiektId, documentSubiektId + 500_000, r.Original, r.Remaining,
                Math.Abs(r.Remaining) < 0.005m, last, lines));
        }
    }

    public Task DeleteSettlementAsync(long documentSubiektId, long rozliczenieId, CancellationToken ct)
    {
        if (IsMissingDocument(documentSubiektId)) throw new SettlementException(SettlementError.DocumentNotFound, $"Dokument {documentSubiektId} nie istnieje");
        if (documentSubiektId >= 3_000_000) throw new SettlementException(SettlementError.NoRozrachunek, "Dokument bez rozrachunku");

        if (!_settlements.TryGetValue(documentSubiektId, out var r))
        {
            throw new SettlementException(SettlementError.SettlementNotFound, $"Rozliczenie {rozliczenieId} nie istnieje");
        }

        lock (r)
        {
            var line = r.Lines.FirstOrDefault(l => l.RozliczenieId == rozliczenieId);
            if (line is null) throw new SettlementException(SettlementError.SettlementNotFound, $"Rozliczenie {rozliczenieId} nie istnieje");
            r.Lines.Remove(line);
            r.Remaining += line.Amount;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BankOperationDto>> QueryBankOperationsAsync(BankOperationQueryRequestDto request, CancellationToken ct)
    {
        IReadOnlyList<BankOperationDto> ops = new[]
        {
            new BankOperationDto(70001, "in", "2026-06-10", 123.45m, 123.45m, 1, "Zaplata FS 1/2026", "BP 1/2026"),
            new BankOperationDto(70002, "in", "2026-06-11", 67.89m, 0m, 2, "Zaplata FS 2/2026", "BP 2/2026"),
        };

        if (string.Equals(request.Direction, "out", StringComparison.OrdinalIgnoreCase))
        {
            ops = Array.Empty<BankOperationDto>();
        }
        if (request.UnsettledOnly)
        {
            ops = ops.Where(o => (o.Remaining ?? 0m) > 0.005m).ToList();
        }
        return Task.FromResult(ops);
    }

    public Task<IReadOnlyList<BankTransactionDto>> QueryBankTransactionsAsync(BankTransactionQueryRequestDto request, CancellationToken ct)
    {
        IReadOnlyList<BankTransactionDto> all = new[]
        {
            new BankTransactionDto(13109, "2026-06-12", 371.12m, "in", "Google Commerce Limited", "PL61109010140000071219812874", "PRZELEW - payout", null, false, null, 49, "PL49 1870 0000 0000 0000 0000 0049"),
            new BankTransactionDto(13127, "2026-06-11", 3372.50m, "in", "Jan Szyszka", "PL27114020040000300201355387", "PRZELEW - zaplata", null, false, null, 1, "PL08 1140 0000 0000 0000 0000 8831"),
            new BankTransactionDto(12001, "2026-06-01", 500.00m, "out", "Dostawca XYZ", "PL11111111111111111111111111", "PRZELEW wychodzacy", null, true, 88001, 49, "PL49 1870 0000 0000 0000 0000 0049"),
            // Bez linku, ale POMINIETA przez operatora (hb_Status=3) - NIE jest "do zaksiegowania".
            new BankTransactionDto(13200, "2026-06-10", 12.00m, "in", "Oplata bankowa", null, "PROWIZJA", null, false, null, 49, "PL49 1870 0000 0000 0000 0000 0049", HbStatus: 3),
        };

        IEnumerable<BankTransactionDto> q = all;
        // Parytet z Real: unbooked_only = bez linku I hb_Status IN (0,4).
        if (request.UnbookedOnly) q = q.Where(t => !t.Booked && t.HbStatus is 0 or 4);
        if (string.Equals(request.Direction, "in", StringComparison.OrdinalIgnoreCase)) q = q.Where(t => t.Direction == "in");
        else if (string.Equals(request.Direction, "out", StringComparison.OrdinalIgnoreCase)) q = q.Where(t => t.Direction == "out");
        return Task.FromResult<IReadOnlyList<BankTransactionDto>>(q.Take(request.Limit > 0 ? request.Limit : 200).ToList());
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _bookedHb = new();
    internal static void ResetBankBookingForTests() => _bookedHb.Clear();

    public Task<BookResultDto> BookBankTransactionAsync(long hbId, long? contractorSubiektId, CancellationToken ct)
    {
        if (SferaUnavailableForTests) throw new SferaUnavailableException("Sesja Sfery niedostepna (fake) - nic nie zapisano");
        if (hbId < 0) throw new BankBookingException(BookError.TransactionNotFound, $"hb_Transakcja {hbId} nie istnieje");
        if (hbId == 77_777) throw new BankBookingException(BookError.NoAccount, "hb_Transakcja bez konta wyciagu (fake)");
        // Sentinel: rachunek walutowy (rb_IdWaluty != 'PLN') -> ForeignAccount (R7), zanim utworzymy BP.
        if (hbId == 66_666) throw new BankBookingException(BookError.ForeignAccount, "hb_Transakcja na rachunku walutowym (fake)");
        // Sentinele do testu mapowania błędów 500: Internal (BP cofnięty/nie powstał) i Orphan (rollback padł).
        if (hbId == 55_555) throw new BankBookingException(BookError.Internal, "COM/raw UPDATE padl, BP cofniety (fake)");
        // 44_444: link padł, rollback padł -> nzfId ZNANY (details.bank_operation_subiekt_id = 90_000 + hb_id).
        if (hbId == 44_444) throw new BankBookingException(BookError.Orphan, "ORPHAN - operacja bez linku, rollback padl (fake)", bankOperationSubiektId: 90_000 + hbId);
        // 44_445: wyjątek PO Zapisz (odczyt Identyfikator padł) -> BP mógł powstać, id NIEZNANE (W1: nigdy Internal).
        if (hbId == 44_445) throw new BankBookingException(BookError.Orphan, "ORPHAN - Zapisz przeszlo, id nieodczytane (fake)");
        // Sentinel: hb_Kwota NULL/0 -> InvalidAmount (422 INVALID_HB_AMOUNT).
        if (hbId == 33_333) throw new BankBookingException(BookError.InvalidAmount, "hb_Transakcja ma hb_Kwota=NULL (fake)");

        // Wariant B: most sam ustawia link (raw UPDATE), wiec sukces zawsze linked=true. Drugie wywolanie
        // tego samego hb_id (stan) -> already_booked (jak guard ExistingOpId w RealSferaSession).
        if (_bookedHb.TryGetValue(hbId, out var existing))
        {
            return Task.FromResult(new BookResultDto(existing, hbId, Linked: true, AlreadyBooked: true, "Transakcja juz zaksiegowana"));
        }
        long opId = 90_000 + hbId;
        _bookedHb[hbId] = opId;
        return Task.FromResult(new BookResultDto(opId, hbId, Linked: true, AlreadyBooked: false, null));
    }

    public Task<long?> GetBookedOperationIdAsync(long hbId, CancellationToken ct)
        => Task.FromResult(_bookedHb.TryGetValue(hbId, out var op) ? op : (long?)null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ----------------------------- KSeF (wysylka e-Faktur) -----------------------------
    // Maszyna stanow advance jak w RealSferaSession, na statycznym slowniku (jak _settlements);
    // testy woluja ResetKsefForTests() i uzywaja unikalnych documentSubiektId.
    // Konwencja id: <0 nie istnieje; 1M-3M FS/KFS (wspierane); >=3M PZ/MM (unsupported).
    // Specjalne: 1_910_001 NotKsefInvoice, 1_910_002 ValidationFailed, 1_910_003 Rejected,
    //            1_910_004 processing na 1. POST -> registered na 2., 1_910_005 CommunicationError,
    //            1_910_006 niedokonczona wysylka (status 'generated' -> kontroler 502 KSEF_SEND_INCOMPLETE).

    private sealed class FakeKsefState
    {
        public string Status = KsefStatusMap.None;
        public string? Number;
        public string? NumberDate;
        public int PostCount;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, FakeKsefState> _ksefStates = new();

    internal static void ResetKsefForTests() => _ksefStates.Clear();

    private static KsefStatusResponseDto KsefSnapshot(long documentSubiektId, FakeKsefState st)
        => new(
            DocumentId: $"sub_{documentSubiektId}",
            KsefStatus: st.Status,
            KsefNumber: st.Number,
            KsefNumberDate: st.NumberDate,
            Message: null);

    public Task<KsefStatusResponseDto> SendInvoiceToKsefAsync(long documentSubiektId, CancellationToken ct)
    {
        if (IsMissingDocument(documentSubiektId))
            throw new KsefException(KsefError.DocumentNotFound, $"Dokument {documentSubiektId} nie istnieje");
        if (documentSubiektId >= 3_000_000)
            throw new KsefException(KsefError.UnsupportedDocumentType,
                "KSeF obsluguje FS (Typ=2) i KFS (Typ=6); dokument magazynowy (PZ/MM) - nie");
        switch (documentSubiektId)
        {
            case 1_910_001:
                throw new KsefException(KsefError.NotKsefInvoice, "FormaDokumentu=0 (tradycyjna) - nie podlega KSeF (mock)");
            case 1_910_002:
                throw new KsefException(KsefError.ValidationFailed, "Dokument nie spelnia wymagan schemy e-Faktury (mock)");
            case 1_910_003:
                throw new KsefException(KsefError.Rejected, "KSeF odrzucil dokument (mock)");
            case 1_910_005:
                throw new KsefException(KsefError.CommunicationError, "Blad komunikacji z KSeF (mock)");
            case 1_910_006:
                // Niedokonczona wysylka: operacja padla bez statusu koncowego - DTO 'generated',
                // kontroler mapuje na 502 KSEF_SEND_INCOMPLETE (exhaustive mapping).
                return Task.FromResult(new KsefStatusResponseDto(
                    DocumentId: $"sub_{documentSubiektId}",
                    KsefStatus: KsefStatusMap.Generated,
                    KsefNumber: null,
                    KsefNumberDate: null,
                    Message: "Operacja zakonczona bez rejestracji w KSeF (mock)"));
        }

        var st = _ksefStates.GetOrAdd(documentSubiektId, _ => new FakeKsefState());
        lock (st)
        {
            // 1_910_004: pierwszy POST konczy sie na 'processing' (symulacja capu czasowego),
            // drugi POST dociaga numer (sciezka PobierzNumerKSeF w Real).
            if (documentSubiektId == 1_910_004 && st.PostCount == 0)
            {
                st.PostCount++;
                st.Status = KsefStatusMap.Processing;
                return Task.FromResult(KsefSnapshot(documentSubiektId, st));
            }

            st.PostCount++;
            st.Status = KsefStatusMap.Registered;
            // Fake NIP 1111111111 - repo publiczne, zero realnych danych.
            st.Number ??= $"1111111111-{DateTime.UtcNow:yyyyMMdd}-{Math.Abs(documentSubiektId):D12}-AA";
            st.NumberDate ??= DateTime.UtcNow.ToString("yyyy-MM-dd");
            _lastInvoiceAt = DateTimeOffset.UtcNow;
            return Task.FromResult(KsefSnapshot(documentSubiektId, st));
        }
    }

    public Task<KsefStatusResponseDto?> GetKsefStatusAsync(long documentSubiektId, CancellationToken ct)
    {
        if (IsMissingDocument(documentSubiektId)) return Task.FromResult<KsefStatusResponseDto?>(null);
        var st = _ksefStates.GetOrAdd(documentSubiektId, _ => new FakeKsefState());
        lock (st) { return Task.FromResult<KsefStatusResponseDto?>(KsefSnapshot(documentSubiektId, st)); }
    }

    // ----------------------------- Receipt detail / PATCH (v0.21.0) -----------------------------
    // Pamięć dokumentów (PZ z CreateReceiptAsync + seed innych typów) i haki odtwarzające zachowania Sfery wg specu §5.5.
    // Fake przechodzi przez TE SAME ReceiptRevision/ReceiptStockFacts/ReceiptStateComparer/ReceiptEditOutcome co Real.

    private sealed class FakeItem
    {
        public long PositionId; public int Lp; public long? ProductId; public string? Ean; public string Name = ""; public ReceiptItemKind Kind = ReceiptItemKind.Product;
        public decimal Quantity; public string Unit = "szt."; public decimal UnitPriceNet; public decimal VatRate = 23m;
    }

    private sealed class FakeDocument
    {
        public long Id; public int DokTyp = Sfera.DokTyp.PZ; public int Status = 1; public string Number = ""; public string IssueDate = "2026-01-15";
        public long ContractorId; public string? ContractorNip; public string? ContractorName; public string OriginalNumber = ""; public string Uwagi = "";
        public List<FakeItem> Items = new(); public FakeReceiptFacts Facts = new(); public int SaveCount; public int LoadCount;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, FakeDocument> _documents = new();
    private static int _seedCounter = 0;

    internal static void ResetReceiptsForTests() { _documents.Clear(); SessionResetCountForTests = 0; }
    /// <summary>Liczba resetów sesji Fake (lustro ResetSessionOnSta po niepotwierdzonym Zamknij / SessionResetAfterSave).</summary>
    internal static int SessionResetCountForTests;
    internal static Task? StaWorkForTests(long id) => Doc(id).Facts.StaWork;

    internal static long SeedDocumentForTests(int dokTyp, int status = 1, string originalNumber = "", int items = 1)
    {
        int n = Interlocked.Increment(ref _seedCounter);
        long id = 7_000_000 + n;
        var doc = new FakeDocument
        {
            Id = id, DokTyp = dokTyp, Status = status, Number = $"{Sfera.DokTyp.NameOrUnknown(dokTyp)} {n}/2026",
            ContractorId = 1, ContractorNip = "1111111111", ContractorName = "Mock Kontrahent", OriginalNumber = originalNumber, Uwagi = "seed",
        };
        for (int i = 1; i <= items; i++)
        {
            doc.Items.Add(new FakeItem { PositionId = 9_500_000 + n * 100 + i, Lp = i, ProductId = 1000 + i, Ean = $"590100000000{i}", Name = $"Seed {i}", Quantity = 1m, UnitPriceNet = 10m });
        }
        _documents[id] = doc;
        return id;
    }

    private static FakeDocument Doc(long id) => _documents.TryGetValue(id, out var d) ? d : throw new InvalidOperationException($"Fake: brak dokumentu {id} (uzyj CreateReceiptAsync/SeedDocumentForTests)");
    internal static void SetReceiptFactsForTests(long id, Action<FakeReceiptFacts> configure) => configure(Doc(id).Facts);
    internal static int SaveCountForTests(long id) => Doc(id).SaveCount;
    internal static int LoadCountForTests(long id) => Doc(id).LoadCount;
    internal static void DeleteDocumentForTests(long id) => _documents.TryRemove(id, out _);
    internal static string OriginalNumberForTests(long id) => Doc(id).OriginalNumber;
    internal static void SetOriginalNumberForTests(long id, string value) => Doc(id).OriginalNumber = value;

    private static void RegisterReceipt(long id, ReceiptIssueRequestDto request, int counter, string number, long contractorId)
    {
        var doc = new FakeDocument
        {
            Id = id, DokTyp = Sfera.DokTyp.PZ, Number = number, ContractorId = contractorId, ContractorNip = request.Supplier.Nip,
            ContractorName = request.Supplier.Name, Uwagi = UwagiFields.Build(request.Notes, request.ExternalReference),
            // Lustro Real: NumerOryginalny przy POST wciaz ciety do 30 (TrySet) - edycja idzie przez PATCH z 422.
            OriginalNumber = request.OriginalNumber is { Length: > 30 } o ? o.Substring(0, 30) : request.OriginalNumber ?? string.Empty,
            IssueDate = string.IsNullOrWhiteSpace(request.IssueDate) ? DateTime.UtcNow.ToString("yyyy-MM-dd") : request.IssueDate,
        };
        int lp = 0;
        foreach (var line in request.Lines)
        {
            lp++;
            decimal net = line.UnitPriceNet ?? Math.Round(line.UnitPriceGross / (1m + line.VatRate / 100m), 4);
            doc.Items.Add(new FakeItem
            {
                PositionId = 9_000_000 + counter * 100 + lp, Lp = lp, ProductId = string.IsNullOrEmpty(line.Ean) ? null : HashString(line.Ean) % 100000,
                // Usługa jednorazowa nie ma towaru → ean/symbol null (lustro Real: tw_* z SQL po TowarId); "" nie trafia do Ean.
                Ean = string.IsNullOrEmpty(line.Ean) ? null : line.Ean, Name = line.NameFallback,
                Kind = string.IsNullOrEmpty(line.Ean) ? ReceiptItemKind.OneOffService : ReceiptItemKind.Product,
                Quantity = line.Quantity, Unit = string.IsNullOrEmpty(line.Unit) ? "szt." : line.Unit, UnitPriceNet = net, VatRate = line.VatRate,
            });
        }
        _documents[id] = doc;
    }

    private static int EffectiveStatus(FakeDocument d) => d.Facts.Cancelled ? DuplicateScan.StatusAnulowany : d.Status;

    private static ReceiptStateSnapshot Snapshot(FakeDocument d) => new(
        d.OriginalNumber, EffectiveStatus(d), d.Facts.BookingStatus,
        d.Items.Select(i => new ReceiptItemSnapshot(i.PositionId, i.Lp, i.ProductId, i.Kind, i.Quantity, i.Quantity, i.Unit, i.UnitPriceNet, 0m, i.VatRate)).ToList());

    /// <summary>Wspólny odczyt (GET i PATCH): preflight sesji, istnienie, haki awarii odczytu, dopisanie zmiany odłożonej przez CancelWhileWaiting.</summary>
    private FakeDocument? LoadDocument(long id)
    {
        if (SferaUnavailableForTests) throw new SferaUnavailableException("Sesja Sfery niedostepna (fake)");
        if (!_documents.TryGetValue(id, out var doc)) return null;
        if (doc.Facts.ReadFails) throw new ReceiptEditException(ReceiptEditError.ReadFailed, $"Fake: odczyt dokumentu {id} padl przy zywej sesji (Istnieje=true).");
        if (doc.Facts.LinkedIdReadFails) throw new ReceiptEditException(ReceiptEditError.ReadFailed, $"Fake: odczyt DoDokumentuId dokumentu {id} padl - nie zgadujemy braku linku.");
        doc.LoadCount++;
        return doc;
    }

    private static IReadOnlyList<LinkedDocumentDto> LinkedDocuments(FakeDocument d, bool sqlOk)
        => d.Facts.LinkedDocument is { } l ? new[] { new LinkedDocumentDto("linked", sqlOk ? l.Type : null, l.Id, l.Number) } : Array.Empty<LinkedDocumentDto>();

    private static ReceiptDetailDto BuildDetail(FakeDocument d)
    {
        var snapshot = Snapshot(d);
        var warnings = new List<string>();
        bool sqlOk = !d.Facts.FailSql;
        if (!sqlOk) warnings.Add("SQL_FACTS_UNAVAILABLE");

        IReadOnlyDictionary<long, StockFact?> facts = new Dictionary<long, StockFact?>();
        if (sqlOk)
        {
            var rows = d.Facts.StockRows ?? d.Items.Where(i => i.Kind == ReceiptItemKind.Product).Select(i => new StockRow(i.PositionId, i.Quantity, i.Quantity, 1)).ToList();
            var (f, inconsistent) = ReceiptStockFacts.FromRows(rows, snapshot);
            facts = f;
            if (inconsistent) warnings.Add("STOCK_FACTS_INCONSISTENT");
        }

        var items = d.Items.Select(i =>
        {
            facts.TryGetValue(i.PositionId, out var fact);
            decimal net = i.Quantity * i.UnitPriceNet;
            return new ReceiptItemDto(i.PositionId, i.Lp, i.ProductId, sqlOk ? i.Ean : null, sqlOk && i.Ean is not null ? MockSymbol(i.Ean) : null, i.Name,
                KindName(i.Kind), i.Quantity, i.Unit, i.Quantity, i.UnitPriceNet, 0m, i.VatRate, net, net * (1m + i.VatRate / 100m),
                fact?.IssuedStockUnits, fact?.IssuedQuantity);
        }).ToList();

        decimal netAmount = d.Items.Sum(i => i.Quantity * i.UnitPriceNet);
        decimal vatAmount = d.Items.Sum(i => i.Quantity * i.UnitPriceNet * i.VatRate / 100m);
        bool? periodLocked = sqlOk ? (d.Facts.PeriodLocked ?? false) : null;
        var linked = LinkedDocuments(d, sqlOk);
        var reasons = ReceiptEditability.Reasons(d.DokTyp, EffectiveStatus(d), periodLocked, d.Facts.BookingStatus, d.Facts.LinkedDocument?.Id, d.Facts.SferaCanEdit);

        return new ReceiptDetailDto(
            SubiektId: d.Id, Number: d.Number, Type: Sfera.DokTyp.NameOrUnknown(d.DokTyp), IssueDate: d.IssueDate,
            ContractorId: d.ContractorId, ContractorNip: d.ContractorNip, ContractorName: d.ContractorName,
            NetAmount: netAmount, VatAmount: vatAmount, GrossAmount: netAmount + vatAmount, Notes: d.Uwagi,
            OriginalNumber: d.OriginalNumber, ExternalReference: ReceiptEditFields.ExtractExternalReference(d.Uwagi),
            WarehouseId: sqlOk ? 1 : null, Status: EffectiveStatus(d), BookingStatus: d.Facts.BookingStatus, PeriodLocked: periodLocked,
            LinkedDocuments: linked, Editability: new ReceiptEditabilityDto(d.Facts.SferaCanEdit, reasons),
            Revision: ReceiptRevision.Compute(snapshot), Items: items, Warnings: warnings);
    }

    private static string KindName(ReceiptItemKind kind) => kind switch
    {
        ReceiptItemKind.Product => "product",
        ReceiptItemKind.Service => "service",
        ReceiptItemKind.OneOffService => "one_off_service",
        ReceiptItemKind.Package => "package",
        ReceiptItemKind.Bundle => "bundle",
        ReceiptItemKind.Suggestion => "suggestion",
        _ => "unknown",
    };

    public Task<ReceiptDetailDto?> GetReceiptDetailAsync(long subiektId, CancellationToken ct)
        => EnqueueOnFakeSta(() => Task.FromResult(LoadDocument(subiektId) is { } doc ? BuildDetail(doc) : null), ct).Caller;

    // Lustro RunOnStaAsync (Codex r5 P2 - czas zycia pracy STA): jeden job naraz (semafor = watek STA); job KONCZY SIE niezaleznie od anulowania
    // wolajacego, semafor zwalniany dopiero po zakonczeniu pracy; anulowanie (ct) przerywa TYLKO oczekiwanie wolajacego (Real: rejestracja tokenu na tcs),
    // a anulowanie PRZED startem jobu pomija go (Real: if (ct.IsCancellationRequested) tcs.TrySetCanceled). GET i PATCH ida przez TE SAMA kolejke.
    private static readonly SemaphoreSlim _staQueue = new(1, 1);

    private static (Task<T> Work, Task<T> Caller) EnqueueOnFakeSta<T>(Func<Task<T>> job, CancellationToken ct)
    {
        var work = Task.Run(async () =>
        {
            await _staQueue.WaitAsync(CancellationToken.None);
            try
            {
                ct.ThrowIfCancellationRequested();
                return await job();
            }
            finally { _staQueue.Release(); }
        });
        // Po anulowaniu wolajacego nikt nie odbiera wyniku jobu - obserwujemy wyjatek (jak log z fazami w Real).
        _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return (work, work.WaitAsync(ct));
    }

    public Task<ReceiptDetailDto> UpdateReceiptHeaderAsync(long subiektId, string originalNumber, string? expectedRevision, CancellationToken ct)
    {
        // Anulowanie wolajacego modelowane WYLACZNIE przez bramke StaGate (praca zaparkowana przed Zapisz, test anuluje token): deterministyczne.
        // Anulowanie "w trakcie" bez bramki (dawny hak CancelDuringApply) usuniete - wyscig obserwacji anulowania (Codex r6) nie daje dowodu.
        var (work, caller) = EnqueueOnFakeSta(() => UpdateReceiptHeaderCore(subiektId, originalNumber, expectedRevision), ct);
        if (_documents.TryGetValue(subiektId, out var d)) d.Facts.StaWork = work;
        return caller;
    }

    private async Task<ReceiptDetailDto> UpdateReceiptHeaderCore(long subiektId, string originalNumber, string? expectedRevision)
    {
        // Preflight sesji (Real: EnsureSessionForMutation) - nic nie zapisano.
        var doc = LoadDocument(subiektId) ?? throw new ReceiptEditException(ReceiptEditError.NotFound, $"PZ o subiekt_id={subiektId} nie istnieje w Subiekcie.");
        var facts = doc.Facts;
        var before = Snapshot(doc);

        if (doc.DokTyp != Sfera.DokTyp.PZ)
            throw Enrich(new ReceiptEditException(ReceiptEditError.NotAReceipt, $"Dokument {subiektId} nie jest PZ (dok_Typ={doc.DokTyp}).", details: new { document_type = Sfera.DokTyp.NameOrUnknown(doc.DokTyp) }), doc);
        if (before.Status == DuplicateScan.StatusAnulowany)
            throw Enrich(new ReceiptEditException(ReceiptEditError.Cancelled, $"PZ {subiektId} jest uniewazniony (dok_Status=2) - edycja niemozliwa."), doc);
        string currentRevision = ReceiptRevision.Compute(before);
        if (expectedRevision is not null && !string.Equals(expectedRevision, currentRevision, StringComparison.Ordinal))
            throw Enrich(new ReceiptEditException(ReceiptEditError.RevisionMismatch, "expected_revision nie zgadza sie z biezacym stanem PZ - pobierz GET i zdecyduj.") { CurrentRevision = currentRevision }, doc);
        if (string.Equals(originalNumber, before.OriginalNumber, StringComparison.Ordinal))
            return BuildDetail(doc); // no-op: bez locka, bez zapisu

        var intended = before with { OriginalNumber = originalNumber };

        // Faza Modify: pierwszy SetComProperty otwiera lock Sfery; wyjatek = nic nie utrwalono.
        if (facts.GuiLocked)
        {
            var locked = new COMException("Nie mozna zablokowac obiektu do edycji (otwarty w GUI).", unchecked((int)0x80040F19));
            throw Enrich(ReceiptEditOutcome.Decide(ReceiptSavePhase.Modify, locked, null, true, false, null)!, doc);
        }

        // Faza Save. Bramka STA (T16b/T17*): praca stoi PRZED Zapisz, az test ja otworzy; anulowanie wolajacego jej nie przerywa (kolejka trzymana),
        // a Zapisz/odmowa/Zamknij/reset/odczyt kontrolny/Decide wykonuja sie w calosci po bramce - takze gdy nikt juz nie czeka na wynik.
        if (facts.StaGate is { } gate)
        {
            facts.StaStarted?.TrySetResult(true);
            await gate.Task;
        }
        Exception? saveEx = null;
        if (facts.ZapiszThrows is { } zt)
        {
            if (zt.Commits) Apply(doc, originalNumber);
            saveEx = new COMException(zt.Message, zt.Hresult);
        }
        else if (!facts.RefuseSaveSilently)
        {
            Apply(doc, originalNumber);
        }

        // CloseVerified (ReceiptCloseGuard w Real): niepotwierdzone Zamknij = reset sesji -> sessionReset, bez odczytu kontrolnego, 502.
        bool closeConfirmed = !facts.CloseFails;
        bool sessionReset = facts.SessionResetAfterSave || !closeConfirmed;
        if (sessionReset) SessionResetCountForTests++;

        // Faza ReadBack: swiezy odczyt -> komparator po ob_Id.
        ReceiptStateMatch? match = null;
        bool? existsAfter = null;
        if (!sessionReset && !facts.ReadBackFails)
        {
            match = ReceiptStateComparer.Compare(before, intended, Snapshot(doc));
            existsAfter = true;
        }
        var phase = saveEx is not null ? ReceiptSavePhase.Save : ReceiptSavePhase.ReadBack;
        var decision = ReceiptEditOutcome.Decide(phase, saveEx, match, closeConfirmed, sessionReset, existsAfter);
        if (decision is not null) throw Enrich(decision, doc);
        return BuildDetail(doc);

        static void Apply(FakeDocument d, string value) { d.OriginalNumber = value; d.SaveCount++; }
    }

    private static ReceiptEditException Enrich(ReceiptEditException ex, FakeDocument doc)
    {
        ex.SferaCanEdit = doc.Facts.SferaCanEdit;
        ex.LinkedDocuments = LinkedDocuments(doc, !doc.Facts.FailSql);
        return ex;
    }

    private static int HashString(string value)
    {
        var hash = 5381;
        foreach (var c in value)
        {
            hash = ((hash << 5) + hash) + c;
        }
        return Math.Abs(hash);
    }
}

/// <summary>Fakty i haki testowe PZ w Fake (lustro zachowań Sfery wg specu §5.5). Ustawiane przez SetReceiptFactsForTests.</summary>
internal sealed class FakeReceiptFacts
{
    public bool Cancelled;
    public bool? PeriodLocked;
    public int? BookingStatus = 0;
    public (long Id, string? Number, string? Type)? LinkedDocument;
    public bool? SferaCanEdit = true;
    public bool GuiLocked;
    public List<StockRow>? StockRows;
    public bool FailSql;
    public bool ReadFails;
    public bool LinkedIdReadFails;
    public (int Hresult, string Message, bool Commits)? ZapiszThrows;
    public bool ReadBackFails;
    public bool CloseFails;
    public bool SessionResetAfterSave;
    public bool RefuseSaveSilently;
    /// <summary>Bramka STA (T16b/T17*): praca na STA staje PRZED Zapisz do otwarcia StaGate; StaStarted sygnalizuje dojście do bramki; StaWork = ostatni job
    /// PATCH tego dokumentu (kończy się wynikiem albo wyjątkiem decyzji także po anulowaniu wołającego).</summary>
    public TaskCompletionSource<bool>? StaGate;
    public TaskCompletionSource<bool>? StaStarted;
    public Task? StaWork;
}
