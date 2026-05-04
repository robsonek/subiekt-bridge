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

        if (!TryParseSubiektIdFromBridgeId(id, out long sourceSubiektId))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_BRIDGE_ID",
                Message: $"Bridge ID '{id}' ma nieznany format. Oczekiwane: 'sub_<id>' (real Sfera) lub 'fake_inv_<id>' (dev mock)."));
        }

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

    /// <summary>
    /// Parsuje bridge_id na subiekt_id. Dwa formaty:
    /// - "sub_{N}" - prawdziwy dokument w Subiekcie (RealSferaSession)
    /// - "fake_inv_{NNNNNN}" - mock (FakeSferaSession, dev tylko)
    /// Zwraca false dla nieznanych formatów, żeby controller mógł zwrócić 422
    /// zamiast cicho użyć 0 i wystawić korektę do nieistniejącego dokumentu.
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
