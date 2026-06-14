using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

/// <summary>
/// Otwarte rozrachunki (należności 39 / zobowiązania 40 z niewykorzystaną kwotą) - read-only.
/// Druga strona danych do matchingu po stronie Laravela (przelewy ↔ otwarte rozrachunki).
/// Filtry direction/contractor_id/amount pozwalają na celowane zapytania (np. "otwarte tej kwoty
/// dla tego kontrahenta"). Most NIE dopasowuje sam.
/// </summary>
[ApiController]
[Route("api/v1/open-receivables")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class OpenReceivablesController : ControllerBase
{
    private readonly ISferaSession _sfera;
    private readonly ILogger<OpenReceivablesController> _logger;

    public OpenReceivablesController(ISferaSession sfera, ILogger<OpenReceivablesController> logger)
    {
        _sfera = sfera;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OpenReceivableDto>>> Query(
        [FromQuery] string? direction,
        [FromQuery(Name = "contractor_id")] long? contractorId,
        [FromQuery] decimal? amount,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        var request = new OpenReceivableQueryRequestDto(
            Direction: direction,
            ContractorId: contractorId,
            Amount: amount,
            Limit: limit > 0 ? limit : 500);

        try
        {
            var items = await _sfera.QueryOpenReceivablesAsync(request, ct);
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryOpenReceivables failed");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto(
                Code: "SUBIEKT_QUERY_FAILED",
                Message: ex.Message));
        }
    }
}
