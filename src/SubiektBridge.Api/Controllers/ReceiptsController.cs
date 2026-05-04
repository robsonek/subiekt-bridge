using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

/// <summary>
/// PZ - Przyjęcia Zewnętrzne (dropshipping flow). Klient kupuje u dostawcy po zamówieniu,
/// PZ dodaje towar na magazyn z ceną zakupu zanim FS sprzeda do klienta końcowego.
/// </summary>
[ApiController]
[Route("api/v1/receipts")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class ReceiptsController : ControllerBase
{
    private readonly ISferaSession _sfera;
    private readonly IdempotencyStore _idempotency;
    private readonly ILogger<ReceiptsController> _logger;

    public ReceiptsController(
        ISferaSession sfera,
        IdempotencyStore idempotency,
        ILogger<ReceiptsController> logger)
    {
        _sfera = sfera;
        _idempotency = idempotency;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<InvoiceResponseDto>> Create(
        [FromBody] ReceiptIssueRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return BadRequest(new ErrorResponseDto(
                Code: "MISSING_IDEMPOTENCY_KEY",
                Message: "Nagłówek 'Idempotency-Key' jest wymagany."));
        }

        var cached = await _idempotency.TryGetAsync<InvoiceResponseDto>(idempotencyKey, ct);
        if (cached is not null)
        {
            _logger.LogInformation("Idempotent replay for key {Key} -> PZ {Number}",
                idempotencyKey, cached.Number);
            return Ok(cached);
        }

        if (request.Lines.Count == 0)
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "EMPTY_LINES",
                Message: "PZ nie może być wystawione bez pozycji."));
        }

        try
        {
            var response = await _sfera.CreateReceiptAsync(request, ct);
            await _idempotency.SaveAsync(idempotencyKey, response, ct);
            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch (NotImplementedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
    }
}
