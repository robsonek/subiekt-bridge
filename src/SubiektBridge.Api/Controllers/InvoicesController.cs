using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

[ApiController]
[Route("api/v1/invoices")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class InvoicesController : ControllerBase
{
    private readonly ISferaSession _sfera;
    private readonly IdempotencyStore _idempotency;
    private readonly ILogger<InvoicesController> _logger;

    public InvoicesController(
        ISferaSession sfera,
        IdempotencyStore idempotency,
        ILogger<InvoicesController> logger)
    {
        _sfera = sfera;
        _idempotency = idempotency;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<InvoiceResponseDto>> Create(
        [FromBody] InvoiceRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return BadRequest(new ErrorResponseDto(
                Code: "MISSING_IDEMPOTENCY_KEY",
                Message: "Nagłówek 'Idempotency-Key' jest wymagany."));
        }

        // Idempotency: powtórny request z tym samym kluczem = ten sam response.
        var cached = await _idempotency.TryGetAsync<InvoiceResponseDto>(idempotencyKey, ct);
        if (cached is not null)
        {
            _logger.LogInformation("Idempotent replay for key {Key} -> invoice {Number}",
                idempotencyKey, cached.Number);
            return Ok(cached);
        }

        // Walidacja totalsum vs Σ(line.qty * line.price_gross + shipping).
        var error = ValidateTotals(request);
        if (error is not null)
        {
            return UnprocessableEntity(error);
        }

        try
        {
            var response = await _sfera.CreateInvoiceAsync(request, ct);
            await _idempotency.SaveAsync(idempotencyKey, response, ct);
            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch (NotImplementedException ex)
        {
            // Stan przejściowy: RealSferaSession (Faza 2.5) jeszcze nie skończony.
            // Pokazujemy 501 zamiast 500 żeby klient widział że to brak feature, nie crash.
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
    }

    [HttpPost("{id}/corrections")]
    public async Task<ActionResult<InvoiceResponseDto>> CreateCorrection(
        string id,
        [FromBody] InvoiceCorrectionRequestDto request,
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
            return Ok(cached);
        }

        // Resolve source FV id w SQLite (idempotency keeps mapping bridge_id -> subiekt_id).
        // TODO Faza 2.5: prawdziwy lookup; teraz używamy parsed id dla fake'a.
        long sourceSubiektId = ParseSubiektIdFromBridgeId(id);

        try
        {
            var response = await _sfera.CreateCorrectionAsync(sourceSubiektId, request, ct);
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

    private static ErrorResponseDto? ValidateTotals(InvoiceRequestDto request)
    {
        decimal expected = 0m;
        foreach (var line in request.Lines)
        {
            expected += line.UnitPriceGross * line.Quantity;
        }
        if (request.Shipping.Include)
        {
            expected += request.Shipping.UnitPriceGross;
        }

        const decimal epsilon = 0.01m;
        var actual = request.Totals.Gross;
        if (Math.Abs(expected - actual) > epsilon)
        {
            return new ErrorResponseDto(
                Code: "TOTAL_MISMATCH",
                Message: $"Suma pozycji ({expected:F2}) nie zgadza się z totals.gross ({actual:F2}).",
                Details: new { expected, actual, epsilon });
        }

        return null;
    }

    private static long ParseSubiektIdFromBridgeId(string bridgeId)
    {
        // Fake bridge_id format: "fake_inv_NNNNNN" -> 1_000_000 + NNNNNN.
        if (bridgeId.StartsWith("fake_inv_") &&
            int.TryParse(bridgeId.AsSpan("fake_inv_".Length), out var counter))
        {
            return 1_000_000 + counter;
        }
        return 0;
    }
}
