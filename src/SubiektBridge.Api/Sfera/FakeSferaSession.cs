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
            grossSum += line.UnitPriceGross * line.Quantity;
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
            GrossAmount: 100.00m,
            Notes: "fake"));
    }

    public Task<byte[]?> GetInvoicePdfAsync(long subiektId, CancellationToken ct)
    {
        // Minimal valid empty PDF stream
        var minimalPdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%fake bridge\n");
        return Task.FromResult<byte[]?>(minimalPdf);
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
