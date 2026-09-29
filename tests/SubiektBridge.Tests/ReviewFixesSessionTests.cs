using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// D1 z przeglądu (2026-09-29): pad sesji Sfery przy replay-with-verify NIE kasuje klucza idempotencji
/// i daje 503 (retry), zamiast "dokument nie istnieje" -> nowy dokument (duplikat). GET /{id} -> 503, nie 404.
/// Sonda sesji w Real jest windows-only; Fake symuluje ją flagą.
/// </summary>
public class ReviewFixesSessionTests
{
    private static IdempotencyStore NewStore()
        => new(new BridgeOptions
        {
            IdempotencyStorePath = Path.Combine(Path.GetTempPath(), $"idem_rsess_{Guid.NewGuid():N}.db"),
            IdempotencyTtlDays = 30,
        }, NullLogger<IdempotencyStore>.Instance);

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

    private static InvoiceRequestDto Invoice() => new(
        Type: "FS", IssueDate: "", SaleDate: "",
        Payment: new PaymentDto("PlatnoscPrzelew", null, 100m, true),
        Currency: "PLN",
        Contractor: new ContractorDto(true, "K1", null, "Test Klient", null, null, null, null,
            new AddressDto("ul. Testowa 1", "00-001", "Warszawa", "PL")),
        Lines: new[] { new LineDto("5901234123457", "Towar testowy", 1m, "szt.", 100m, 23m) },
        Shipping: new ShippingDto(false, "Wysyłka", 0m, 23m),
        Totals: new InvoiceTotalsDto(Gross: 100m),
        ExternalReference: $"test:{Guid.NewGuid():N}", Notes: "");

    [Fact]
    public async Task ReplayVerify_SferaDown_Returns503_KeepsIdempotencyKey()
    {
        var fake = new FakeSferaSession();
        var store = NewStore();
        var controller = new InvoicesController(fake, store, NullLogger<InvoicesController>.Instance);
        const string key = "k-session";
        var created = Unwrap((await controller.Create(Invoice(), key, CancellationToken.None)).Result);
        Assert.Equal(201, created.status);

        fake.SferaUnavailableForTests = true;
        var (status, value) = Unwrap((await controller.Create(Invoice(), key, CancellationToken.None)).Result);

        Assert.Equal(503, status);
        Assert.Equal("SUBIEKT_UNAVAILABLE", Assert.IsType<ErrorResponseDto>(value).Code);
        // Klucz przetrwał: po powrocie sesji replay zwróci pierwotny dokument, nie wystawi drugiego.
        Assert.NotNull(await store.TryGetAsync<InvoiceResponseDto>(key, CancellationToken.None));

        fake.SferaUnavailableForTests = false;
        var replay = Unwrap((await controller.Create(Invoice(), key, CancellationToken.None)).Result);
        Assert.True(replay.status is 200 or 201);
        Assert.Equal(Assert.IsType<InvoiceResponseDto>(created.value).SubiektId, Assert.IsType<InvoiceResponseDto>(replay.value).SubiektId);
    }

    [Fact]
    public async Task GetById_SferaDown_Returns503_Not404()
    {
        var fake = new FakeSferaSession { SferaUnavailableForTests = true };
        var controller = new InvoicesController(fake, NewStore(), NullLogger<InvoicesController>.Instance);

        var (status, value) = Unwrap((await controller.Get("sub_1", CancellationToken.None)).Result);

        Assert.Equal(503, status);
        Assert.Equal("SUBIEKT_UNAVAILABLE", Assert.IsType<ErrorResponseDto>(value).Code);
    }
}
