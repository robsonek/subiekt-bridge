using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

/// <summary>
/// Rozliczenia rozrachunków - spinanie zaimportowanych z wyciągu operacji bankowych (przelewów BP)
/// z fakturami przez Sferę (FinRozliczenia.Rozlicz OD STRONY ROZRACHUNKU). FS i FZ (Typ 2/1); korekty (KFS/KFZ) -> 422.
///
/// Drugi kontroler z [Route("api/v1/invoices")] obok InvoicesController - ASP.NET Core to dopuszcza,
/// bo szablony akcji są różne ({id}/settlements vs {id}/{id}/pdf/{id}/corrections). NIE duplikujemy
/// [HttpGet("{id}")] bez sufiksu (byłby ambiguous z InvoicesController.Get).
/// </summary>
[ApiController]
[Route("api/v1/invoices")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class SettlementsController : ControllerBase
{
    private readonly ISferaSession _sfera;
    private readonly IdempotencyStore _idempotency;
    private readonly ILogger<SettlementsController> _logger;

    public SettlementsController(
        ISferaSession sfera,
        IdempotencyStore idempotency,
        ILogger<SettlementsController> logger)
    {
        _sfera = sfera;
        _idempotency = idempotency;
        _logger = logger;
    }

    [HttpPost("{id}/settlements")]
    public async Task<ActionResult<SettlementResponseDto>> Create(
        string id,
        [FromBody] SettlementCreateRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        // Kolejnosc: parse bridge-id (422) -> missing key (400) -> replay-with-verify -> flow.
        // (Inaczej niz CreateCorrection - replay-verify wymaga document_subiekt_id.)
        if (!TryParseSubiektIdFromBridgeId(id, out long documentSubiektId))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_BRIDGE_ID",
                Message: $"Bridge ID '{id}' ma nieznany format. Oczekiwane: 'sub_<id>' (real Sfera) lub 'fake_inv_<id>' (dev mock)."));
        }

        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return BadRequest(new ErrorResponseDto(
                Code: "MISSING_IDEMPOTENCY_KEY",
                Message: "Nagłówek 'Idempotency-Key' jest wymagany."));
        }

        // Replay-with-verify: weryfikujemy po RozliczenieId (NIE po istnieniu dokumentu - ten
        // zawsze istnieje). Operator cofnal rozliczenie -> cache invalidate -> nowy flow.
        var cached = await _idempotency.TryGetAsync<SettlementResponseDto>(idempotencyKey, ct);
        if (cached is not null)
        {
            bool stillExists = false;
            try
            {
                var state = await _sfera.GetSettlementsAsync(documentSubiektId, ct);
                stillExists = state is not null && state.Settlements.Any(s => s.RozliczenieId == cached.RozliczenieId);
            }
            catch (Exception ex)
            {
                // Nie udalo sie zweryfikowac - schodzimy do pelnego flow (anti-duplicate FAIL-CLOSED
                // i tak zlapie ewentualny duplikat). Nie maskujemy bledem zapisu.
                _logger.LogWarning(ex, "Settlement replay verify failed for key {Key}; fallthrough do pelnego flow", idempotencyKey);
            }

            if (stillExists)
            {
                _logger.LogInformation("Idempotent replay for key {Key} -> rozliczenie {RozId}", idempotencyKey, cached.RozliczenieId);
                return Ok(cached);
            }
            _logger.LogWarning("Idempotent cache invalidated: rozliczenie_id={RozId} nie istnieje juz - traktujemy jako nowy request", cached.RozliczenieId);
            await _idempotency.DeleteAsync(idempotencyKey, ct);
        }

        try
        {
            var response = await _sfera.CreateSettlementAsync(documentSubiektId, request, ct);
            await _idempotency.SaveAsync(idempotencyKey, response, ct);
            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch (DuplicateSettlementException ex)
        {
            _logger.LogWarning("Duplicate settlement blocked: bankOp={BankOp}, rozrachunek={Rozr}, existing_rozliczenie_id={RozId}",
                ex.BankOperationSubiektId, ex.RozrachunekSubiektId, ex.ExistingRozliczenieId);
            return Conflict(new ErrorResponseDto(
                Code: "DUPLICATE_SETTLEMENT",
                Message: ex.Message,
                Details: new
                {
                    existing_rozliczenie_id = ex.ExistingRozliczenieId,
                    rozrachunek_subiekt_id = ex.RozrachunekSubiektId,
                    bank_operation_subiekt_id = ex.BankOperationSubiektId,
                }));
        }
        catch (SettlementException ex)
        {
            return MapSettlementError(ex);
        }
        catch (NotImplementedException ex)
        {
            _logger.LogError(ex, "Settlement operation NotImplemented: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Settlement operation failed unexpectedly");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "INTERNAL_ERROR",
                Message: ex.GetType().Name + ": " + ex.Message,
                Details: new { stack = ex.StackTrace?.Split('\n').Take(10).ToArray() }));
        }
    }

    [HttpGet("{id}/settlements")]
    public async Task<ActionResult<SettlementStateResponseDto>> GetState(string id, CancellationToken ct)
    {
        if (!TryParseSubiektIdFromBridgeId(id, out long documentSubiektId))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_BRIDGE_ID",
                Message: $"Bridge ID '{id}' ma nieznany format. Oczekiwane: 'sub_<id>'."));
        }

        try
        {
            var state = await _sfera.GetSettlementsAsync(documentSubiektId, ct);
            if (state is null)
            {
                return NotFound(new ErrorResponseDto(
                    Code: "INVOICE_NOT_FOUND",
                    Message: $"Dokument o subiekt_id={documentSubiektId} nie istnieje w Subiekcie."));
            }
            return Ok(state);
        }
        catch (SettlementException ex)
        {
            return MapSettlementError(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSettlements failed");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto(
                Code: "SUBIEKT_QUERY_FAILED",
                Message: ex.Message));
        }
    }

    [HttpDelete("{id}/settlements/{rozliczenieId:long}")]
    public async Task<IActionResult> Delete(string id, long rozliczenieId, CancellationToken ct)
    {
        if (!TryParseSubiektIdFromBridgeId(id, out long documentSubiektId))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_BRIDGE_ID",
                Message: $"Bridge ID '{id}' ma nieznany format. Oczekiwane: 'sub_<id>'."));
        }

        try
        {
            await _sfera.DeleteSettlementAsync(documentSubiektId, rozliczenieId, ct);
            return NoContent();
        }
        catch (SettlementException ex)
        {
            return MapSettlementError(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteSettlement failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "INTERNAL_ERROR",
                Message: ex.GetType().Name + ": " + ex.Message,
                Details: new { stack = ex.StackTrace?.Split('\n').Take(10).ToArray() }));
        }
    }

    private ActionResult MapSettlementError(SettlementException ex) => ex.Reason switch
    {
        SettlementError.DocumentNotFound => NotFound(new ErrorResponseDto("INVOICE_NOT_FOUND", ex.Message)),
        SettlementError.SettlementNotFound => NotFound(new ErrorResponseDto("SETTLEMENT_NOT_FOUND", ex.Message)),
        SettlementError.NoRozrachunek => UnprocessableEntity(new ErrorResponseDto("SETTLEMENT_NOT_SUPPORTED", ex.Message)),
        SettlementError.SettlementNotSupported => UnprocessableEntity(new ErrorResponseDto("SETTLEMENT_NOT_SUPPORTED", ex.Message)),
        SettlementError.AlreadySettled => UnprocessableEntity(new ErrorResponseDto("ALREADY_SETTLED", ex.Message)),
        SettlementError.UnsupportedDocumentType => UnprocessableEntity(new ErrorResponseDto("UNSUPPORTED_DOCUMENT_TYPE", ex.Message)),
        SettlementError.InvalidAmount => UnprocessableEntity(new ErrorResponseDto("INVALID_AMOUNT", ex.Message)),
        SettlementError.AmountExceedsRemaining => UnprocessableEntity(new ErrorResponseDto("AMOUNT_EXCEEDS_REMAINING", ex.Message, ex.Details)),
        SettlementError.UnsupportedCurrency => UnprocessableEntity(new ErrorResponseDto("UNSUPPORTED_CURRENCY", ex.Message)),
        SettlementError.BankOperationNotFound => UnprocessableEntity(new ErrorResponseDto("BANK_OPERATION_NOT_FOUND", ex.Message)),
        SettlementError.BankOperationExhausted => UnprocessableEntity(new ErrorResponseDto("BANK_OPERATION_EXHAUSTED", ex.Message)),
        SettlementError.BankOperationContractorMismatch => UnprocessableEntity(new ErrorResponseDto("BANK_OPERATION_CONTRACTOR_MISMATCH", ex.Message)),
        SettlementError.ScanFailed => StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto("SUBIEKT_QUERY_FAILED", ex.Message)),
        _ => StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto("INTERNAL_ERROR", ex.Message)),
    };

    /// <summary>
    /// Parsuje bridge_id na subiekt_id (kopia z InvoicesController - parser jest private static,
    /// nie współdzielony). Akceptuje "sub_{N}" (real) i "fake_inv_{NNNNNN}" (dev mock).
    /// </summary>
    private static bool TryParseSubiektIdFromBridgeId(string bridgeId, out long subiektId)
    {
        subiektId = 0;

        const string realPrefix = "sub_";
        if (bridgeId.StartsWith(realPrefix, StringComparison.Ordinal) &&
            long.TryParse(bridgeId.AsSpan(realPrefix.Length), out var realId))
        {
            subiektId = realId;
            return true;
        }

        const string fakePrefix = "fake_inv_";
        if (bridgeId.StartsWith(fakePrefix, StringComparison.Ordinal) &&
            int.TryParse(bridgeId.AsSpan(fakePrefix.Length), out var counter))
        {
            subiektId = 1_000_000 + counter;
            return true;
        }

        return false;
    }
}
