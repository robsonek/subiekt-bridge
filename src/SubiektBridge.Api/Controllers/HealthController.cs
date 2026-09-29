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
            var (statusCode, response) = Map(sfera);
            return StatusCode(statusCode, response);
        }
        catch (Exception ex)
        {
            return StatusCode(503, new ErrorResponseDto(
                Code: "BRIDGE_DEGRADED",
                Message: ex.Message));
        }
    }

    /// <summary>
    /// 503 tylko gdy padła sesja Sfery (wystawianie dokumentów niemożliwe). Padnięty SqlClient =
    /// "degraded" z 200: dokumenty przez COM dalej idą, ale raw SQL (lookup NIP, /bank-transactions) nie.
    /// </summary>
    internal static (int StatusCode, HealthResponseDto Response) Map(SferaHealthDto sfera)
    {
        var response = new HealthResponseDto(
            Status: sfera.SessionActive && sfera.SqlConnectionOk != false ? "ok" : "degraded",
            BridgeVersion: BridgeVersion,
            SubiektVersion: sfera.SubiektVersion,
            SferaSession: sfera.SessionActive ? "active" : "down",
            LastInvoiceAt: sfera.LastInvoiceAt,
            QueueDepth: 0,
            LastError: sfera.LastError,
            SqlConnection: sfera.SqlConnectionOk switch { true => "ok", false => "down", null => "unknown" },
            SqlError: sfera.SqlError);

        return (sfera.SessionActive ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, response);
    }
}
