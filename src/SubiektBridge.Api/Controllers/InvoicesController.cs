using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using MissingProductException = SubiektBridge.Api.Sfera.MissingProductException;

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

    /// <summary>
    /// Listing istniejących FV/KFS w Subiekcie (read-only). Używamy do
    /// dopasowania zamówień zafakturowanych przez inny system - po imporcie
    /// metadata FV trafia do tabeli invoices i nie próbujemy fakturować ponownie.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InvoiceQueryItemDto>>> Query(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? type,
        [FromQuery(Name = "notes_contains")] string? notesContains,
        [FromQuery] string? nip,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        var request = new InvoiceQueryRequestDto(
            From: from,
            To: to,
            Type: type,
            NotesContains: notesContains,
            Nip: nip,
            Limit: limit > 0 ? limit : 200);

        try
        {
            var items = await _sfera.QueryInvoicesAsync(request, ct);
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryInvoices failed");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto(
                Code: "SUBIEKT_QUERY_FAILED",
                Message: ex.Message));
        }
    }

    /// <summary>Metadata pojedynczej FV po Bridge ID (sub_&lt;subiektId&gt;).</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<InvoiceQueryItemDto>> Get(string id, CancellationToken ct)
    {
        if (!TryParseSubiektIdFromBridgeId(id, out long subiektId))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_BRIDGE_ID",
                Message: $"Bridge ID '{id}' ma nieznany format. Oczekiwane: 'sub_<id>'."));
        }

        var item = await _sfera.FindInvoiceByIdAsync(subiektId, ct);
        if (item == null)
        {
            return NotFound(new ErrorResponseDto(
                Code: "INVOICE_NOT_FOUND",
                Message: $"FV o subiekt_id={subiektId} nie istnieje w Subiekcie."));
        }
        return Ok(item);
    }

    /// <summary>Retroaktywny PDF download. Generuje świeży wydruk przez Sferę.</summary>
    [HttpGet("{id}/pdf")]
    public async Task<IActionResult> GetPdf(string id, CancellationToken ct)
    {
        if (!TryParseSubiektIdFromBridgeId(id, out long subiektId))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_BRIDGE_ID",
                Message: $"Bridge ID '{id}' ma nieznany format. Oczekiwane: 'sub_<id>'."));
        }

        var bytes = await _sfera.GetInvoicePdfAsync(subiektId, ct);
        if (bytes == null)
        {
            return NotFound(new ErrorResponseDto(
                Code: "PDF_UNAVAILABLE",
                Message: $"PDF dla subiekt_id={subiektId} niedostępny (FV nie istnieje albo generowanie padło)."));
        }
        return File(bytes, "application/pdf", $"invoice_{subiektId}.pdf");
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
        catch (MissingProductException ex)
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "MISSING_PRODUCT",
                Message: ex.Message,
                Details: new { missing_eans = new[] { ex.MissingEan } }));
        }
        catch (NotImplementedException ex)
        {
            _logger.LogError(ex, "Invoice operation NotImplemented: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invoice operation failed unexpectedly");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "INTERNAL_ERROR",
                Message: ex.GetType().Name + ": " + ex.Message,
                Details: new { stack = ex.StackTrace?.Split('\n').Take(10).ToArray() }));
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
        catch (MissingProductException ex)
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "MISSING_PRODUCT",
                Message: ex.Message,
                Details: new { missing_eans = new[] { ex.MissingEan } }));
        }
        catch (NotImplementedException ex)
        {
            _logger.LogError(ex, "Invoice operation NotImplemented: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invoice operation failed unexpectedly");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "INTERNAL_ERROR",
                Message: ex.GetType().Name + ": " + ex.Message,
                Details: new { stack = ex.StackTrace?.Split('\n').Take(10).ToArray() }));
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
