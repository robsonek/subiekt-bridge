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

    /// <summary>
    /// Księgowanie przelewu na operację bankową. WYŁĄCZONE (501): empirycznie potwierdzone (probe prod +
    /// SQL Profiler + CHM + adwersaryjny research), że Sfera NIE wystawia API księgowania home-bankingu -
    /// `DodajOperacjeBankowa` tworzy operację SAMODZIELNĄ, a powiązania z linią wyciągu (hb_idOperacjiBankowej)
    /// ani przypisania do wyciągu Sfera nie ustawia. Księgowanie robi operator w module Bankowość Subiekta;
    /// most rozlicza już zaksięgowaną operację przez POST /api/v1/invoices/{id}/settlements.
    /// (Hybryda Sfera + raw UPDATE hb_Transakcja jest rozważana - patrz memory/home-banking-booking.)
    /// </summary>
    [HttpPost("{hbId:long}/book")]
    public IActionResult Book(long hbId, [FromBody] BookRequestDto? request)
    {
        _logger.LogInformation("Book wywolany dla hb_id={HbId} - zwracam 501 (ksiegowanie HB poza API Sfery)", hbId);
        return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
            Code: "HB_BOOKING_NOT_SUPPORTED",
            Message: "Ksiegowanie przelewu z wyciagu nie jest mozliwe przez Sfere (potwierdzone empirycznie). " +
                     "Zaksieguj przelew w module Bankowosc Subiekta, a nastepnie rozlicz przez POST /api/v1/invoices/{id}/settlements."));
    }
}
