using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using MissingProductException = SubiektBridge.Api.Sfera.MissingProductException;

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

    /// <summary>
    /// Listing istniejących PZ z Subiekta. Filtry analogiczne do GET /invoices,
    /// ale type wymuszone na PZ. Sfera dzieli WczytajDokument/DrukujDoPliku po
    /// SubiektId niezależnie od typu - reuse generic ISferaSession metod.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InvoiceQueryItemDto>>> Query(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery(Name = "notes_contains")] string? notesContains,
        [FromQuery] string? nip,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        var request = new InvoiceQueryRequestDto(
            From: from,
            To: to,
            Type: "PZ",
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
            _logger.LogError(ex, "QueryReceipts failed");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto(
                Code: "SUBIEKT_QUERY_FAILED",
                Message: ex.Message));
        }
    }

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
                Code: "RECEIPT_NOT_FOUND",
                Message: $"PZ o subiekt_id={subiektId} nie istnieje w Subiekcie."));
        }
        return Ok(item);
    }

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
                Message: $"PDF dla subiekt_id={subiektId} niedostępny."));
        }
        return File(bytes, "application/pdf", $"receipt_{subiektId}.pdf");
    }

    private static bool TryParseSubiektIdFromBridgeId(string bridgeId, out long subiektId)
    {
        subiektId = 0;
        if (string.IsNullOrEmpty(bridgeId)) return false;
        if (bridgeId.StartsWith("sub_") && long.TryParse(bridgeId.AsSpan(4), out subiektId))
        {
            return true;
        }
        return false;
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
        catch (MissingProductException ex)
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "MISSING_PRODUCT",
                Message: ex.Message,
                Details: new { missing_eans = new[] { ex.MissingEan } }));
        }
        catch (NotImplementedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
    }
}
