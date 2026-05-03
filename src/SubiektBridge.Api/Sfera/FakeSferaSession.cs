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

    public Task<SferaHealthDto> HealthAsync(CancellationToken ct)
    {
        return Task.FromResult(new SferaHealthDto(
            SubiektVersion: "FAKE-1.78.0",
            SessionActive: true,
            LastInvoiceAt: _lastInvoiceAt));
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
