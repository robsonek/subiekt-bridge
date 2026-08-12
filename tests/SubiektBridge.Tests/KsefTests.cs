using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SubiektBridge.Api.Controllers;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using Xunit;

namespace SubiektBridge.Tests;

/// <summary>
/// Testy KSeF: mapper statusow + FakeSferaSession (maszyna stanow advance) + KsefController
/// (mapowanie HTTP). RealSferaSession (COM) testuje sie manualnie na Windows (prod).
/// </summary>
public class KsefTests
{
    public KsefTests() => FakeSferaSession.ResetKsefForTests();

    // ----------------------------- Mapper statusow -----------------------------

    [Theory]
    [InlineData(0, "none")]
    [InlineData(1, "validated")]
    [InlineData(2, "generated")]
    [InlineData(3, "sending")]
    [InlineData(4, "processing")]
    [InlineData(5, "registered")]
    [InlineData(6, "rejected")]
    [InlineData(7, "validation_failed")]
    [InlineData(8, "communication_error")]
    [InlineData(99, "unknown")]
    public void KsefStatusMap_MapsEnumToApiString(int status, string expected)
        => Assert.Equal(expected, KsefStatusMap.ToApiString(status));

    // ----------------------------- FakeSfera: maszyna stanow -----------------------------

    [Fact]
    public async Task Send_HappyPath_RegistersAndReturnsNumber()
    {
        var fake = new FakeSferaSession();
        var r = await fake.SendInvoiceToKsefAsync(1_000_001, CancellationToken.None);

        Assert.Equal("registered", r.KsefStatus);
        Assert.Equal("sub_1000001", r.DocumentId);
        Assert.False(string.IsNullOrEmpty(r.KsefNumber));
        Assert.False(string.IsNullOrEmpty(r.KsefNumberDate));
        Assert.Null(r.Message);
    }

    [Fact]
    public async Task Send_SecondPost_IsIdempotent_SameNumber()
    {
        var fake = new FakeSferaSession();
        var first = await fake.SendInvoiceToKsefAsync(1_000_002, CancellationToken.None);
        var second = await fake.SendInvoiceToKsefAsync(1_000_002, CancellationToken.None);

        Assert.Equal("registered", second.KsefStatus);
        Assert.Equal(first.KsefNumber, second.KsefNumber);
    }

    [Fact]
    public async Task Send_Kfs_IsSupported()
    {
        var fake = new FakeSferaSession();
        var r = await fake.SendInvoiceToKsefAsync(2_000_001, CancellationToken.None);
        Assert.Equal("registered", r.KsefStatus);
    }

    [Fact]
    public async Task Send_ProcessingThenRegistered_AdvancesOnSecondPost()
    {
        var fake = new FakeSferaSession();
        var first = await fake.SendInvoiceToKsefAsync(1_910_004, CancellationToken.None);
        Assert.Equal("processing", first.KsefStatus);
        Assert.Null(first.KsefNumber);

        var second = await fake.SendInvoiceToKsefAsync(1_910_004, CancellationToken.None);
        Assert.Equal("registered", second.KsefStatus);
        Assert.False(string.IsNullOrEmpty(second.KsefNumber));
    }

    [Fact]
    public async Task Send_NotFound_Throws404Reason()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<KsefException>(
            () => fake.SendInvoiceToKsefAsync(-5, CancellationToken.None));
        Assert.Equal(KsefError.DocumentNotFound, ex.Reason);
    }

    [Fact]
    public async Task Send_Pz_UnsupportedType()
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<KsefException>(
            () => fake.SendInvoiceToKsefAsync(3_000_001, CancellationToken.None));
        Assert.Equal(KsefError.UnsupportedDocumentType, ex.Reason);
    }

    [Theory]
    [InlineData(1_910_001, KsefError.NotKsefInvoice)]
    [InlineData(1_910_002, KsefError.ValidationFailed)]
    [InlineData(1_910_003, KsefError.Rejected)]
    [InlineData(1_910_005, KsefError.CommunicationError)]
    public async Task Send_ErrorScenarios_ThrowExpectedReason(long docId, KsefError expected)
    {
        var fake = new FakeSferaSession();
        var ex = await Assert.ThrowsAsync<KsefException>(
            () => fake.SendInvoiceToKsefAsync(docId, CancellationToken.None));
        Assert.Equal(expected, ex.Reason);
    }

    [Fact]
    public async Task Get_FreshDoc_ReturnsNone()
    {
        var fake = new FakeSferaSession();
        var r = await fake.GetKsefStatusAsync(1_000_003, CancellationToken.None);
        Assert.NotNull(r);
        Assert.Equal("none", r!.KsefStatus);
        Assert.Null(r.KsefNumber);
    }

    [Fact]
    public async Task Get_AfterSend_ReturnsRegistered()
    {
        var fake = new FakeSferaSession();
        await fake.SendInvoiceToKsefAsync(1_000_004, CancellationToken.None);
        var r = await fake.GetKsefStatusAsync(1_000_004, CancellationToken.None);
        Assert.Equal("registered", r!.KsefStatus);
        Assert.False(string.IsNullOrEmpty(r.KsefNumber));
    }

    [Fact]
    public async Task Get_NotFound_ReturnsNull()
    {
        var fake = new FakeSferaSession();
        var r = await fake.GetKsefStatusAsync(-5, CancellationToken.None);
        Assert.Null(r);
    }
}
