using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SubiektBridge.Api.Configuration;
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
    private readonly BridgeOptions _options;
    private readonly ILogger<BankTransactionsController> _logger;

    public BankTransactionsController(
        ISferaSession sfera,
        IdempotencyStore idempotency,
        IOptions<BridgeOptions> options,
        ILogger<BankTransactionsController> logger)
    {
        _sfera = sfera;
        _idempotency = idempotency;
        _options = options.Value;
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
    /// Zaksięguj surowy przelew na operację bankową BP/BW (wariant B): Sfera tworzy operację, most domyka
    /// link raw UPDATE hb_Transakcja. Zwraca bank_operation_subiekt_id (gotowy do POST /invoices/{id}/settlements)
    /// + `linked` (zawsze true na sukcesie). Most NIE matchuje - dostaje rozkaz "zaksięguj hb_id". Idempotency-Key wymagany.
    ///
    /// Bramka <see cref="BridgeOptions.EnableHbBooking"/> (domyślnie FALSE) → 501-stub. Raw UPDATE hb_Transakcja
    /// jest poza biblioteką Sfery; włączać dopiero po zielonym teście odwracalnym na prodzie (plan §7).
    /// </summary>
    [HttpPost("{hbId:long}/book")]
    public async Task<ActionResult<BookResultDto>> Book(
        long hbId,
        [FromBody] BookRequestDto? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!_options.EnableHbBooking)
        {
            _logger.LogInformation("Book wywolany dla hb_id={HbId} - EnableHbBooking=false -> 501", hbId);
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "HB_BOOKING_NOT_SUPPORTED",
                Message: "Ksiegowanie przelewu z wyciagu jest wylaczone (Bridge:EnableHbBooking=false). " +
                         "Zaksieguj przelew w module Bankowosc Subiekta, a nastepnie rozlicz przez POST /api/v1/invoices/{id}/settlements."));
        }

        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return BadRequest(new ErrorResponseDto(
                Code: "MISSING_IDEMPOTENCY_KEY",
                Message: "Nagłówek 'Idempotency-Key' jest wymagany."));
        }

        // Replay z weryfikacja: gdy cache mowi linked+op, sprawdz ze operacja WCIAZ powiazana (mogla zostac usunieta
        // recznie w Subiekcie). Jak znikla -> invaliduj cache i wykonaj pelny flow (jak w endpointach faktur/rozrachunkow).
        var cached = await _idempotency.TryGetAsync<BookResultDto>(idempotencyKey, ct);
        if (cached is not null)
        {
            bool stale = false;
            if (cached.Linked && cached.BankOperationSubiektId.HasValue)
            {
                try
                {
                    var live = await _sfera.GetBookedOperationIdAsync(hbId, ct);
                    stale = live != cached.BankOperationSubiektId;
                }
                catch (Exception ex)
                {
                    // FAIL-CLOSED (jak SettlementsController): nie potrafimy zweryfikowac, czy operacja wciaz istnieje
                    // -> NIE zwracamy cache (mogla zostac usunieta w GUI). Pelny flow jest idempotentny (guard
                    // ExistingOpId zwroci AlreadyBooked bez drugiego BP), wiec downside zerowy, a fail-open ryzykowal
                    // oddanie nieaktualnego bank_operation_subiekt_id do /settlements.
                    _logger.LogWarning(ex, "Book replay-verify failed (hb_id={HbId}) - fail-closed, pelny flow", hbId);
                    stale = true;
                }
            }
            if (!stale)
            {
                return StatusCode(StatusForBook(cached), cached);
            }
            _logger.LogWarning("Book idempotent cache invalidated: hb_id={HbId}, operacja {Op} zniknela - nowy flow", hbId, cached.BankOperationSubiektId);
            await _idempotency.DeleteAsync(idempotencyKey, ct);
        }

        try
        {
            var result = await _sfera.BookBankTransactionAsync(hbId, request?.ContractorSubiektId, ct);
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
        catch (BankBookingException ex) when (ex.Reason == BookError.InvalidDirection)
        {
            return UnprocessableEntity(new ErrorResponseDto("INVALID_DIRECTION", ex.Message));
        }
        catch (BankBookingException ex) when (ex.Reason == BookError.ForeignAccount)
        {
            return UnprocessableEntity(new ErrorResponseDto("UNSUPPORTED_FOREIGN_ACCOUNT", ex.Message));
        }
        catch (BankBookingException ex) when (ex.Reason == BookError.UnsupportedStatus)
        {
            return UnprocessableEntity(new ErrorResponseDto("UNSUPPORTED_HB_STATUS", ex.Message));
        }
        catch (BankBookingException ex) when (ex.Reason == BookError.Internal)
        {
            // COM/raw UPDATE padl, ale BP zostal CZYSTO cofniety (stan spojny) -> retryowalne. Bez stacka w body.
            _logger.LogError(ex, "Book internal failure (hb_id={HbId})", hbId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto("HB_BOOKING_FAILED", ex.Message));
        }
        catch (BankBookingException ex) when (ex.Reason == BookError.Orphan)
        {
            // ORPHAN: operacja bankowa powstala bez linku i rollback padl - stan NIESPOJNY, interwencja reczna.
            // 500 (NIE 2xx) by klient NIE potraktowal tego jako sukces i NIE retry'owal na slepo (kolejny BP).
            _logger.LogError(ex, "Book ORPHAN - operacja bez linku, rollback padl (hb_id={HbId})", hbId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto("HB_BOOKING_ORPHAN", ex.Message));
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

    // 201 tylko dla czystego nowego księgowania z powiązaniem; 200 dla already_booked (też wyścig @@ROWCOUNT=0).
    // Spójne dla fresh i replay.
    private static int StatusForBook(BookResultDto r)
        => r is { AlreadyBooked: false, Linked: true } ? StatusCodes.Status201Created : StatusCodes.Status200OK;
}
