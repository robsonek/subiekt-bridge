using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Idempotency;
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
    private readonly IdempotencyStore _idempotency;
    private readonly ILogger<BankTransactionsController> _logger;

    public BankTransactionsController(ISferaSession sfera, IdempotencyStore idempotency, ILogger<BankTransactionsController> logger)
    {
        _sfera = sfera;
        _idempotency = idempotency;
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
    /// Zaksięguj surowy przelew na operację bankową BP/BW przez Sferę. Zwraca bank_operation_subiekt_id
    /// (gotowy do POST /invoices/{id}/settlements) + `linked` (czy Subiekt ustawił hb_idOperacjiBankowej).
    /// Most NIE matchuje - dostaje rozkaz "zaksięguj hb_id". Idempotency-Key wymagany.
    /// </summary>
    [HttpPost("{hbId:long}/book")]
    public async Task<ActionResult<BookResultDto>> Book(
        long hbId,
        [FromBody] BookRequestDto? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return BadRequest(new ErrorResponseDto(
                Code: "MISSING_IDEMPOTENCY_KEY",
                Message: "Nagłówek 'Idempotency-Key' jest wymagany."));
        }

        // Replay: ten sam klucz -> ten sam wynik (chroni przed drugim BP przy retry, gdy Sfera nie auto-linkuje).
        var cached = await _idempotency.TryGetAsync<BookResultDto>(idempotencyKey, ct);
        if (cached is not null)
        {
            return StatusCode(StatusForBook(cached), cached);
        }

        try
        {
            var result = await _sfera.BookBankTransactionAsync(hbId, request?.ContractorSubiektId, request?.KeepUnlinked ?? false, ct);
            await _idempotency.SaveAsync(idempotencyKey, result, ct);
            return StatusCode(StatusForBook(result), result);
        }
        catch (BankBookingException ex) when (ex.Reason == BookError.TransactionNotFound)
        {
            return NotFound(new ErrorResponseDto("BANK_TRANSACTION_NOT_FOUND", ex.Message));
        }
        catch (BankBookingException ex) when (ex.Reason == BookError.NoAccount)
        {
            return UnprocessableEntity(new ErrorResponseDto("NO_BANK_ACCOUNT", ex.Message));
        }
        catch (NotImplementedException ex)
        {
            _logger.LogError(ex, "Book NotImplemented");
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto("NOT_IMPLEMENTED", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BookBankTransaction failed (hb_id={HbId})", hbId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "INTERNAL_ERROR",
                Message: ex.GetType().Name + ": " + ex.Message,
                Details: new { stack = ex.StackTrace?.Split('\n').Take(10).ToArray() }));
        }
    }

    // 201 tylko dla czystego nowego księgowania z powiązaniem; 200 dla already_booked ORAZ linked=false
    // (Branch B - klient MUSI sprawdzić pole `linked`, nie sam status). Spójne dla fresh i replay.
    private static int StatusForBook(BookResultDto r)
        => r is { AlreadyBooked: false, Linked: true } ? StatusCodes.Status201Created : StatusCodes.Status200OK;
}
