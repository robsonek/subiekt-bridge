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
}
