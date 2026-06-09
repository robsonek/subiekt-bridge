using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using MissingProductException = SubiektBridge.Api.Sfera.MissingProductException;

namespace SubiektBridge.Api.Controllers;

/// <summary>
/// MM - Przesunięcia Międzymagazynowe. Przenosi stan towaru między magazynami
/// (source -> dest). Dokument wewnętrzny magazynowy - NIE idzie do KSeF (magazyn nie jest
/// polem schematu e-faktury). Używane np. do korekty stanu po dokumencie wystawionym na
/// zły magazyn, gdy samego dokumentu nie można już zmienić (np. w KSeF).
/// </summary>
[ApiController]
[Route("api/v1/transfers")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class TransfersController : ControllerBase
{
    private readonly ISferaSession _sfera;
    private readonly IdempotencyStore _idempotency;
    private readonly ILogger<TransfersController> _logger;

    public TransfersController(
        ISferaSession sfera,
        IdempotencyStore idempotency,
        ILogger<TransfersController> logger)
    {
        _sfera = sfera;
        _idempotency = idempotency;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<TransferResponseDto>> Create(
        [FromBody] TransferRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return BadRequest(new ErrorResponseDto(
                Code: "MISSING_IDEMPOTENCY_KEY",
                Message: "Nagłówek 'Idempotency-Key' jest wymagany."));
        }

        // Idempotency: replay na ten sam klucz, z weryfikacją że cached subiekt_id wciąż
        // istnieje (gdyby MM usunięto w Subiekcie). Wzorzec jak InvoicesController v0.7.23.
        var cached = await _idempotency.TryGetAsync<TransferResponseDto>(idempotencyKey, ct);
        if (cached is not null)
        {
            var stillExists = await _sfera.FindInvoiceByIdAsync(cached.SubiektId, ct);
            if (stillExists is not null)
            {
                _logger.LogInformation("Idempotent replay for key {Key} -> MM {Number}",
                    idempotencyKey, cached.Number);
                return Ok(cached);
            }
            _logger.LogWarning("Idempotent cache invalidated: MM subiekt_id={Id} ({Number}) nie istnieje juz w Subiekcie - traktujemy jako nowy request",
                cached.SubiektId, cached.Number);
            await _idempotency.DeleteAsync(idempotencyKey, ct);
        }

        if (request.Lines.Count == 0)
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "EMPTY_LINES",
                Message: "MM nie może być wystawione bez pozycji."));
        }

        if (request.SourceWarehouseId == request.DestWarehouseId)
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "SAME_WAREHOUSE",
                Message: "Magazyn źródłowy i docelowy są identyczne - MM bezcelowe."));
        }

        try
        {
            var response = await _sfera.CreateTransferAsync(request, ct);
            await _idempotency.SaveAsync(idempotencyKey, response, ct);
            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch (MissingProductException ex)
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "MISSING_PRODUCT",
                Message: ex.Message,
                Details: new { missing_eans = new[] { ex.MissingEan } }));
        }
        catch (DuplicateInvoiceException ex)
        {
            _logger.LogWarning("Duplicate MM blocked: ref={Ref}, existing={Number} (subiekt_id={Id})",
                ex.ExternalReference, ex.ExistingNumber, ex.ExistingSubiektId);
            return Conflict(new ErrorResponseDto(
                Code: "DUPLICATE_TRANSFER",
                Message: ex.Message,
                Details: new
                {
                    existing_subiekt_id = ex.ExistingSubiektId,
                    existing_number = ex.ExistingNumber,
                    existing_bridge_id = $"sub_{ex.ExistingSubiektId}",
                    external_reference = ex.ExternalReference,
                }));
        }
        catch (NotImplementedException ex)
        {
            _logger.LogError(ex, "MM operation NotImplemented: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MM operation failed unexpectedly");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "INTERNAL_ERROR",
                Message: ex.GetType().Name + ": " + ex.Message,
                Details: new { stack = ex.StackTrace?.Split('\n').Take(10).ToArray() }));
        }
    }
}
