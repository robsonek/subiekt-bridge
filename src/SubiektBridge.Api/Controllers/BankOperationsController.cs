using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

/// <summary>
/// Operacje bankowe (BP/BW) z zaimportowanych wyciągów - read-only listing do dopasowania
/// przelewów z fakturami. Źródło bank_operation_subiekt_id dla POST /invoices/{id}/settlements.
/// </summary>
[ApiController]
[Route("api/v1/bank-operations")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class BankOperationsController : ControllerBase
{
    private readonly ISferaSession _sfera;
    private readonly ILogger<BankOperationsController> _logger;

    public BankOperationsController(ISferaSession sfera, ILogger<BankOperationsController> logger)
    {
        _sfera = sfera;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BankOperationDto>>> Query(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? direction,
        [FromQuery(Name = "unsettled_only")] bool unsettledOnly,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        var request = new BankOperationQueryRequestDto(
            From: from,
            To: to,
            Direction: direction,
            UnsettledOnly: unsettledOnly,
            Limit: limit > 0 ? limit : 200);

        try
        {
            var items = await _sfera.QueryBankOperationsAsync(request, ct);
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryBankOperations failed");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto(
                Code: "SUBIEKT_QUERY_FAILED",
                Message: ex.Message));
        }
    }
}
