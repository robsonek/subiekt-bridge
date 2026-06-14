using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

/// <summary>
/// Surowe przelewy z wyciągu (hb_Transakcja) - read-only pula "do zaksięgowania". Most wystawia
/// DANE; dopasowanie do należności (tiery, auto vs ręcznie) robi konsument (Laravel), tak jak
/// dopasowuje GET /invoices do zamówień. Księgowanie/rozliczenie to osobne endpointy operacji Sfery.
/// </summary>
[ApiController]
[Route("api/v1/bank-transactions")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class BankTransactionsController : ControllerBase
{
    private readonly ISferaSession _sfera;
    private readonly ILogger<BankTransactionsController> _logger;

    public BankTransactionsController(ISferaSession sfera, ILogger<BankTransactionsController> logger)
    {
        _sfera = sfera;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BankTransactionDto>>> Query(
        [FromQuery] string? direction,
        [FromQuery(Name = "unbooked_only")] bool? unbookedOnly,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        var request = new BankTransactionQueryRequestDto(
            Direction: direction,
            UnbookedOnly: unbookedOnly ?? true,
            From: from,
            To: to,
            Limit: limit > 0 ? limit : 200);

        try
        {
            var items = await _sfera.QueryBankTransactionsAsync(request, ct);
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryBankTransactions failed");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto(
                Code: "SUBIEKT_QUERY_FAILED",
                Message: ex.Message));
        }
    }
}
