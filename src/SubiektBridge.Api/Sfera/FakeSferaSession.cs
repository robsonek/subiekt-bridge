using System.Globalization;
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
    private DateTimeOffset? _lastInvoiceAt;

    public Task<InvoiceResponseDto> CreateInvoiceAsync(InvoiceRequestDto request, CancellationToken ct)
    {
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
            ContractorSubiektId: 0,
            Totals: new InvoiceTotalsDto(Net: net, Vat: vat, Gross: grossSum),
            PdfUrl: $"/api/v1/invoices/fake_kfs_{counter:D6}/pdf",
            PdfBase64: null));
    }

    public Task<ProductDto?> FindProductByEanAsync(string ean, CancellationToken ct)
    {
        // Deterministyczny mock: EAN-y zaczynające się na "5901" są "znane".
        if (string.IsNullOrEmpty(ean) || !ean.StartsWith("5901"))
        {
            return Task.FromResult<ProductDto?>(null);
        }

        return Task.FromResult<ProductDto?>(new ProductDto(
            SubiektId: HashString(ean) % 100000,
            Symbol: $"SKU-{ean[^4..]}",
            Ean: ean,
            Name: $"Mock product {ean}",
            VatRate: 23m,
            Unit: "szt.",
            IsActive: true));
    }

    public Task<InvoiceResponseDto> CreateReceiptAsync(ReceiptIssueRequestDto request, CancellationToken ct)
    {
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

        return Task.FromResult(new InvoiceResponseDto(
            Id: $"fake_pz_{counter:D6}",
            SubiektId: 3_000_000 + counter,
            Number: $"PZ {counter}/{year}",
            IssuedAt: _lastInvoiceAt.Value,
            ContractorSubiektId: HashString(request.Supplier.Symbol) % 100000,
            Totals: new InvoiceTotalsDto(Net: grossSum, Vat: 0m, Gross: grossSum),
            PdfUrl: $"/api/v1/receipts/fake_pz_{counter:D6}/pdf",
            PdfBase64: null));
    }

    public Task<TransferResponseDto> CreateTransferAsync(TransferRequestDto request, CancellationToken ct)
    {
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
        // Deterministyczne otwarte naleznosci (rozrachunki sprzedazy z otwartym saldem). Most NIE matchuje -
        // zwraca okno kwotowe; filtrowanie min/max/contractor/limit lustrem RealSferaSession. TYLKO PLN:
        // 51900 (EUR) jest tu po to, by potwierdzic ze most go WYKLUCZA (Real: WartoscBiezaca zawsze w PLN,
        // wiersz walutowy bylby mylacy + nierozliczalny -> controller odrzuca currency!=PLN, Core pomija nie-PLN).
        // 52001 (ten sam kontrahent 13292 co 53447) sprawdza filtr contractor_id.
        OpenReceivableDto[] all =
        {
            new("sub_53447", 53447, "FS", "PLN", 3372.50m, 13292, "Szyszka Krzysztof", "FS 573/05/2026"),
            new("sub_53310", 53310, "FS", "PLN", 371.12m, 14001, "Google Commerce Limited", "FS 540/05/2026"),
            new("sub_52001", 52001, "FS", "PLN", 120.00m, 13292, "Szyszka Krzysztof", "FS 410/04/2026"),
            new("sub_51900", 51900, "FS", "EUR", 99.00m, 15000, "Mock Foreign GmbH", "FS 300/03/2026"),
        };

        decimal min = request.MinAmount ?? 0m;
        decimal max = request.MaxAmount ?? decimal.MaxValue;
        int limit = Math.Clamp(request.Limit > 0 ? request.Limit : 50, 1, 200);

        // Tylko PLN (parytet z Real: rozrachunki walutowe pomijane; controller i tak odrzuca currency!=PLN).
        IEnumerable<OpenReceivableDto> q = all
            .Where(r => r.Remaining > 0.005m)
            .Where(r => r.Remaining >= min && r.Remaining <= max)
            .Where(r => string.Equals(r.Currency, "PLN", StringComparison.OrdinalIgnoreCase));
        if (request.ContractorId.HasValue) q = q.Where(r => r.ContractorId == request.ContractorId.Value);

        return Task.FromResult<IReadOnlyList<OpenReceivableDto>>(q.Take(limit).ToList());
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
        return Task.FromResult(new SferaHealthDto(
            SubiektVersion: "FAKE-1.78.0",
            SessionActive: true,
            LastInvoiceAt: _lastInvoiceAt,
            LastError: null));
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
        if (request.Amount <= 0m) throw new SettlementException(SettlementError.InvalidAmount, "amount musi byc > 0");
        decimal amount = Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero);

        if (documentSubiektId < 0) throw new SettlementException(SettlementError.DocumentNotFound, $"Dokument {documentSubiektId} nie istnieje");
        if (documentSubiektId >= 3_000_000) throw new SettlementException(SettlementError.NoRozrachunek, "Dokument bez rozrachunku (PZ magazynowy)");
        if (documentSubiektId is >= 2_000_000 and < 3_000_000) throw new SettlementException(SettlementError.UnsupportedDocumentType, "Typ nieobslugiwany (np. korekta)");
        if (request.BankOperationSubiektId < 0) throw new SettlementException(SettlementError.BankOperationNotFound, $"Operacja bankowa {request.BankOperationSubiektId} nie istnieje");
        if (request.BankOperationSubiektId == 0) throw new SettlementException(SettlementError.BankOperationExhausted, "Operacja bankowa skonsumowana");

        var r = _settlements.GetOrAdd(documentSubiektId, id => new FakeRozrachunek
        {
            Original = 100m,
            Remaining = id == 1_900_001 ? 0m : 100m,
        });

        lock (r)
        {
            if (r.Remaining <= 0.005m) throw new SettlementException(SettlementError.AlreadySettled, "Rozrachunek juz rozliczony");

            var dup = r.Lines.FirstOrDefault(l => l.BankOpId == request.BankOperationSubiektId);
            if (dup is not null)
            {
                throw new DuplicateSettlementException(dup.RozliczenieId, documentSubiektId, request.BankOperationSubiektId);
            }

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
        if (documentSubiektId < 0) return Task.FromResult<SettlementStateResponseDto?>(null);
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
        if (documentSubiektId < 0) throw new SettlementException(SettlementError.DocumentNotFound, $"Dokument {documentSubiektId} nie istnieje");
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
        };

        IEnumerable<BankTransactionDto> q = all;
        if (request.UnbookedOnly) q = q.Where(t => !t.Booked);
        if (string.Equals(request.Direction, "in", StringComparison.OrdinalIgnoreCase)) q = q.Where(t => t.Direction == "in");
        else if (string.Equals(request.Direction, "out", StringComparison.OrdinalIgnoreCase)) q = q.Where(t => t.Direction == "out");
        return Task.FromResult<IReadOnlyList<BankTransactionDto>>(q.Take(request.Limit > 0 ? request.Limit : 200).ToList());
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _bookedHb = new();
    internal static void ResetBankBookingForTests() => _bookedHb.Clear();

    public Task<BookResultDto> BookBankTransactionAsync(long hbId, long? contractorSubiektId, CancellationToken ct)
    {
        if (hbId < 0) throw new BankBookingException(BookError.TransactionNotFound, $"hb_Transakcja {hbId} nie istnieje");
        if (hbId == 77_777) throw new BankBookingException(BookError.NoAccount, "hb_Transakcja bez konta wyciagu (fake)");
        // Sentinel: rachunek walutowy (rb_IdWaluty != 'PLN') -> ForeignAccount (R7), zanim utworzymy BP.
        if (hbId == 66_666) throw new BankBookingException(BookError.ForeignAccount, "hb_Transakcja na rachunku walutowym (fake)");
        // Sentinele do testu mapowania błędów 500: Internal (BP cofnięty/nie powstał) i Orphan (rollback padł).
        if (hbId == 55_555) throw new BankBookingException(BookError.Internal, "COM/raw UPDATE padl, BP cofniety (fake)");
        if (hbId == 44_444) throw new BankBookingException(BookError.Orphan, "ORPHAN - operacja bez linku, rollback padl (fake)");

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
