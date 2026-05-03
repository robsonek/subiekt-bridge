using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

[ApiController]
[Route("api/v1/health")]
[AllowAnonymous] // Publiczny dla LB/monitoring (autoryzowany IP-whitelistą firewalla).
public sealed class HealthController : ControllerBase
{
    private readonly ISferaSession _sfera;
    private static readonly string BridgeVersion =
        typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public HealthController(ISferaSession sfera)
    {
        _sfera = sfera;
    }

    [HttpGet]
    public async Task<ActionResult<HealthResponseDto>> Get(CancellationToken ct)
    {
        try
        {
            var sfera = await _sfera.HealthAsync(ct);

            var response = new HealthResponseDto(
                Status: sfera.SessionActive ? "ok" : "degraded",
                BridgeVersion: BridgeVersion,
                SubiektVersion: sfera.SubiektVersion,
                SferaSession: sfera.SessionActive ? "active" : "down",
                LastInvoiceAt: sfera.LastInvoiceAt,
                QueueDepth: 0,
                LastError: sfera.LastError);

            return sfera.SessionActive ? Ok(response) : StatusCode(503, response);
        }
        catch (Exception ex)
        {
            return StatusCode(503, new ErrorResponseDto(
                Code: "BRIDGE_DEGRADED",
                Message: ex.Message));
        }
    }
}
