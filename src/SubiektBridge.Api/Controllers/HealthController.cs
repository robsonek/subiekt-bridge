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
    /// Od v0.24.0 "busy" z 200: wątek Sfery jest zajęty jednym długim zadaniem i sonda sesji nie zdążyła się wykonać w budżecie -
    /// most żyje i odpowiada, a `queue_depth` / `busy_for_seconds` mówią, co się dzieje (do v0.23.0 /health wisiał wtedy do limitu
    /// klienta i wyglądał jak awaria całego mostu - incydent 2026-11-09).
    /// </summary>
    internal static (int StatusCode, HealthResponseDto Response) Map(SferaHealthDto sfera)
    {
        string status = sfera.StaBusy ? "busy" : sfera.SessionActive && sfera.SqlConnectionOk != false ? "ok" : "degraded";
        string session = sfera.StaBusy ? "busy" : sfera.SessionActive ? "active" : "down";
        var response = new HealthResponseDto(
            Status: status,
            BridgeVersion: BridgeVersion,
            SubiektVersion: sfera.SubiektVersion,
            SferaSession: session,
            LastInvoiceAt: sfera.LastInvoiceAt,
            QueueDepth: sfera.QueueDepth,
            LastError: sfera.LastError,
            SqlConnection: sfera.SqlConnectionOk switch { true => "ok", false => "down", null => "unknown" },
            SqlError: sfera.SqlError,
            BusyForSeconds: sfera.StaBusyForSeconds);

        return (sfera.StaBusy || sfera.SessionActive ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, response);
    }
}
