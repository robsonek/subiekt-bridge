using System.Threading;
using System.Threading.Tasks;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

public class OpenPayablesTests
{
    private static OpenReceivablesQueryRequestDto Req(
        decimal? min = null, decimal? max = null, long? contractor = null,
        string? from = null, string? to = null, int limit = 50, string? search = null)
        => new(min, max, "PLN", contractor, from, to, limit, search);

    [Fact]
    public async Task Payables_ReturnsOnlyFzRows_InAmountWindow()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenPayablesAsync(Req(min: 100m, max: 5000m), CancellationToken.None);
        Assert.NotEmpty(r);
        Assert.All(r, x => Assert.Equal("FZ", x.DocType));
        Assert.All(r, x => Assert.InRange(x.Remaining, 100m, 5000m));
    }

    [Fact]
    public async Task Payables_FiltersByContractor()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenPayablesAsync(Req(contractor: 5001), CancellationToken.None);
        Assert.NotEmpty(r);
        Assert.All(r, x => Assert.Equal(5001, x.ContractorId));
    }

    [Fact]
    public async Task Payables_ExcludesNonPln()
    {
        var fake = new FakeSferaSession();
        var r = await fake.QueryOpenPayablesAsync(Req(), CancellationToken.None);
        Assert.All(r, x => Assert.Equal("PLN", x.Currency));
    }
}
