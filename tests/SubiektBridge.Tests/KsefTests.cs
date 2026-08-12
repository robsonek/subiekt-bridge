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

    private static KsefController NewController(FakeSferaSession fake)
        => new(fake, NullLogger<KsefController>.Instance);

    private static (int status, object? value) Unwrap(IActionResult? result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, o.Value),
        StatusCodeResult s => (s.StatusCode, null),
        null => (0, null),
        _ => (-1, null),
    };

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

    // ----------------------------- KsefController: mapowanie HTTP -----------------------------

    [Fact]
    public async Task Post_HappyPath_Returns200WithNumber()
    {
        var ctrl = NewController(new FakeSferaSession());
        var (status, value) = Unwrap((await ctrl.Send("fake_inv_000001", CancellationToken.None)).Result);

        Assert.Equal(200, status);
        var dto = Assert.IsType<KsefStatusResponseDto>(value);
        Assert.Equal("registered", dto.KsefStatus);
        Assert.False(string.IsNullOrEmpty(dto.KsefNumber));
    }

    [Fact]
    public async Task Post_Processing_Returns202()
    {
        var ctrl = NewController(new FakeSferaSession());
        var (status, value) = Unwrap((await ctrl.Send("sub_1910004", CancellationToken.None)).Result);

        Assert.Equal(202, status);
        var dto = Assert.IsType<KsefStatusResponseDto>(value);
        Assert.Equal("processing", dto.KsefStatus);
    }

    [Fact]
    public async Task Post_NotFound_Returns404()
    {
        var ctrl = NewController(new FakeSferaSession());
        var (status, value) = Unwrap((await ctrl.Send("sub_-5", CancellationToken.None)).Result);

        Assert.Equal(404, status);
        Assert.Equal("INVOICE_NOT_FOUND", Assert.IsType<ErrorResponseDto>(value).Code);
    }

    [Fact]
    public async Task Post_InvalidBridgeId_Returns422()
    {
        var ctrl = NewController(new FakeSferaSession());
        var (status, value) = Unwrap((await ctrl.Send("zly-format", CancellationToken.None)).Result);

        Assert.Equal(422, status);
        Assert.Equal("INVALID_BRIDGE_ID", Assert.IsType<ErrorResponseDto>(value).Code);
    }

    [Theory]
    [InlineData("sub_3000001", 422, "UNSUPPORTED_DOCUMENT_TYPE")]
    [InlineData("sub_1910001", 422, "NOT_KSEF_INVOICE")]
    [InlineData("sub_1910002", 422, "KSEF_VALIDATION_FAILED")]
    [InlineData("sub_1910003", 422, "KSEF_REJECTED")]
    [InlineData("sub_1910005", 502, "KSEF_COMMUNICATION_ERROR")]
    [InlineData("sub_1910006", 502, "KSEF_SEND_INCOMPLETE")] // stan nie-koncowy != sukces (exhaustive mapping)
    public async Task Post_ErrorScenarios_MapToHttp(string bridgeId, int expectedStatus, string expectedCode)
    {
        var ctrl = NewController(new FakeSferaSession());
        var (status, value) = Unwrap((await ctrl.Send(bridgeId, CancellationToken.None)).Result);

        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedCode, Assert.IsType<ErrorResponseDto>(value).Code);
    }

    [Fact]
    public async Task Post_FakeKfsBridgeId_IsParsedAndSupported()
    {
        // KSeF wspiera KFS -> parser musi znac "fake_kfs_" (id 2M+n z CreateCorrectionAsync fake'a).
        var ctrl = NewController(new FakeSferaSession());
        var (status, value) = Unwrap((await ctrl.Send("fake_kfs_000001", CancellationToken.None)).Result);

        Assert.Equal(200, status);
        Assert.Equal("registered", Assert.IsType<KsefStatusResponseDto>(value).KsefStatus);
    }

    [Fact]
    public async Task Get_Fresh_Returns200None()
    {
        var ctrl = NewController(new FakeSferaSession());
        var (status, value) = Unwrap((await ctrl.GetStatus("sub_1000009", CancellationToken.None)).Result);

        Assert.Equal(200, status);
        Assert.Equal("none", Assert.IsType<KsefStatusResponseDto>(value).KsefStatus);
    }

    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        var ctrl = NewController(new FakeSferaSession());
        var (status, value) = Unwrap((await ctrl.GetStatus("sub_-5", CancellationToken.None)).Result);

        Assert.Equal(404, status);
        Assert.Equal("INVOICE_NOT_FOUND", Assert.IsType<ErrorResponseDto>(value).Code);
    }
}
