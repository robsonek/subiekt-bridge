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

    /// <summary>
    /// Szczegóły PZ (v0.21.0): nadzbiór dawnego InvoiceQueryItemDto + pozycje read-only, powiązania (FZ/KFS/inny), fakty edytowalności,
    /// revision, fakty rozchodu. Bez guardów mutacji (nie-PZ i unieważnione → 200 z powodami). 404 tylko przy POZYTYWNYM braku dokumentu.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<ReceiptDetailDto>> Get(string id, CancellationToken ct)
    {
        if (!BridgeIds.TryParseSubiektId(id, out long subiektId))
        {
            return UnprocessableEntity(InvalidBridgeId(id));
        }

        try
        {
            var dto = await _sfera.GetReceiptDetailAsync(subiektId, ct);
            if (dto is null)
            {
                return NotFound(NotFoundError(subiektId));
            }
            return Ok(dto);
        }
        catch (SferaUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponseDto(
                Code: "SUBIEKT_UNAVAILABLE", Message: ex.Message));
        }
        catch (ReceiptEditException ex)
        {
            var (status, error) = ReceiptEditErrorMapper.ToResult(ex);
            return StatusCode(status, error);
        }
    }

    /// <summary>
    /// PATCH nagłówka PZ: `original_number` (numer dokumentu dostawcy, np. faktury z KSeF). Walidacja PRZED Sferą (422 bez dotykania
    /// dokumentu), Idempotency-Key (replay z weryfikacją istnienia; klucz NIE kasowany przy 503/502), opcjonalne `expected_revision`
    /// (409 RECEIPT_REVISION_MISMATCH). Odpowiedź 200 = stan po odczycie kontrolnym po Zapisz (spec §4.2/§4.4/§5.6).
    /// </summary>
    [HttpPatch("{id}")]
    public async Task<ActionResult<ReceiptDetailDto>> PatchHeader(
        string id,
        [FromBody] ReceiptHeaderPatchDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!BridgeIds.TryParseSubiektId(id, out long subiektId))
        {
            return UnprocessableEntity(InvalidBridgeId(id));
        }
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return BadRequest(new ErrorResponseDto(
                Code: "MISSING_IDEMPOTENCY_KEY",
                Message: "Nagłówek 'Idempotency-Key' jest wymagany."));
        }
        if (ReceiptEditFields.ValidateOriginalNumber(request.OriginalNumber) is { } validation)
        {
            var (vStatus, vError) = ReceiptEditErrorMapper.ToResult(validation);
            return StatusCode(vStatus, vError);
        }
        string value = ReceiptEditFields.NormalizeOriginalNumber(request.OriginalNumber.GetString()!);

        // Replay: cached 200 ma pierwszeństwo przed expected_revision (§4.4 przypadek 1). Dokument pozytywnie nieistniejący → klucz
        // kasowany i OD RAZU 404 (bez mutacji - Codex r4 #7); pad sesji / odczytu → kod 503/502 BEZ kasowania klucza (kasacja + ponowienie
        // = zapis bez replayu).
        var cached = await _idempotency.TryGetAsync<ReceiptDetailDto>(idempotencyKey, ct);
        if (cached is not null)
        {
            ReceiptDetailDto? stillExists;
            try { stillExists = await _sfera.GetReceiptDetailAsync(cached.SubiektId, ct); }
            catch (SferaUnavailableException ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponseDto(
                    Code: "SUBIEKT_UNAVAILABLE", Message: ex.Message));
            }
            catch (ReceiptEditException ex)
            {
                var (rStatus, rError) = ReceiptEditErrorMapper.ToResult(ex);
                return StatusCode(rStatus, rError);
            }
            if (stillExists is not null)
            {
                _logger.LogInformation("Idempotent replay for key {Key} -> PZ {Number} (PATCH original_number)", idempotencyKey, cached.Number);
                return Ok(cached);
            }
            _logger.LogWarning("Idempotent cache invalidated: subiekt_id={Id} ({Number}) nie istnieje juz w Subiekcie - klucz skasowany, 404 bez mutacji (spec 4.4)",
                cached.SubiektId, cached.Number);
            await _idempotency.DeleteAsync(idempotencyKey, ct);
            return NotFound(NotFoundError(cached.SubiektId));
        }

        ReceiptDetailDto response;
        try
        {
            response = await _sfera.UpdateReceiptHeaderAsync(subiektId, value, request.ExpectedRevision, ct);
        }
        catch (SferaUnavailableException ex)
        {
            // Preflight sesji: nic nie zapisano, klucz zostaje poza cache, retry tym samym kluczem.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponseDto(
                Code: "SUBIEKT_UNAVAILABLE", Message: ex.Message));
        }
        catch (ReceiptEditException ex)
        {
            var (status, error) = ReceiptEditErrorMapper.ToResult(ex);
            return StatusCode(status, error);
        }
        catch (OperationCanceledException ex)
        {
            // Anulowanie w trakcie pracy COM: STA konczy delegat (RunOnStaAsync), wynik dla wolajacego NIEZNANY. Mapowanie - przy
            // zerwanym HTTP odpowiedzi i tak nie da sie dostarczyc; nic nie trafia do cache.
            _logger.LogWarning(ex, "PATCH PZ {Id}: zadanie anulowane w trakcie pracy COM - stan nieznany (klient: GET)", subiektId);
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto(
                Code: "RECEIPT_UPDATE_UNVERIFIED",
                Message: "Zadanie anulowane w trakcie zapisu w Subiekcie - stan PZ nieznany, sprawdz GET.",
                Details: new { saved_probably = (bool?)null, hresult = (string?)null, sfera_message = (string?)null, phase = (string?)null }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PATCH PZ {Id} failed unexpectedly", subiektId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "INTERNAL_ERROR",
                Message: ex.GetType().Name + ": " + ex.Message,
                Details: new { stack = ex.StackTrace?.Split('\n').Take(10).ToArray() }));
        }

        try
        {
            await _idempotency.SaveAsync(idempotencyKey, response, ct);
        }
        catch (Exception ex)
        {
            // Zapis w Subiekcie JUZ WSZEDL (odczyt kontrolny potwierdzil) - tylko cache padl. Ponowienie = cache miss: trzy przypadki z kontraktu
            // §3.6b (stare expected_revision -> 409; bez pola -> no-op TYLKO gdy wartosc biezaca == zadana, inaczej zapis).
            _logger.LogError(ex, "PATCH PZ {Id}: zapis cache idempotencji padl PO udanym zapisie w Subiekcie (stan zapisany; ponowienie wg kontraktu 3.6b)", subiektId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "INTERNAL_ERROR",
                Message: "Zmiana zapisana w Subiekcie, ale zapis cache idempotencji padl: " + ex.Message,
                Details: new { state = "saved", subiekt_id = response.SubiektId, revision = response.Revision }));
        }
        return Ok(response);
    }

    private static ErrorResponseDto InvalidBridgeId(string id) => new(
        Code: "INVALID_BRIDGE_ID",
        Message: $"Bridge ID '{id}' ma nieznany format albo id poza zakresem. Oczekiwane: 'sub_<n>', 1 <= n <= 2147483647.");

    private static ErrorResponseDto NotFoundError(long subiektId) => new(
        Code: "RECEIPT_NOT_FOUND",
        Message: $"PZ o subiekt_id={subiektId} nie istnieje w Subiekcie.");

    [HttpGet("{id}/pdf")]
    public async Task<IActionResult> GetPdf(string id, CancellationToken ct)
    {
        if (!BridgeIds.TryParseSubiektId(id, out long subiektId))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_BRIDGE_ID",
                Message: $"Bridge ID '{id}' ma nieznany format albo id poza zakresem. Oczekiwane: 'sub_<n>', 1 <= n <= 2147483647."));
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

        // Idempotency: powtórny request z tym samym kluczem = ten sam response.
        // ALE: weryfikujemy ze cached subiekt_id wciaz istnieje. Jesli user anulowal/usunal
        // PZ w Subiekcie (a nasz Idempotency-Key zostal ten sam, np. ponowny klik 'Wystaw PZ'
        // w UI po manualnym usunieciu z Subiekta), nie chcemy zwracac danych ducha -
        // traktujemy request jako nowy. Wzorzec analogiczny do InvoicesController v0.7.23.
        var cached = await _idempotency.TryGetAsync<InvoiceResponseDto>(idempotencyKey, ct);
        if (cached is not null)
        {
            InvoiceQueryItemDto? stillExists;
            try { stillExists = await _sfera.FindInvoiceByIdAsync(cached.SubiektId, ct); }
            catch (SferaUnavailableException ex)
            {
                // Sesja Sfery padla - NIE kasujemy klucza idempotencji (kasacja + nowy request = duplikat).
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponseDto(
                    Code: "SUBIEKT_UNAVAILABLE", Message: ex.Message));
            }
            if (stillExists is not null)
            {
                _logger.LogInformation("Idempotent replay for key {Key} -> PZ {Number}",
                    idempotencyKey, cached.Number);
                return Ok(cached);
            }
            _logger.LogWarning("Idempotent cache invalidated: subiekt_id={Id} ({Number}) nie istnieje juz w Subiekcie - traktujemy jako nowy request",
                cached.SubiektId, cached.Number);
            await _idempotency.DeleteAsync(idempotencyKey, ct);
        }

        if (request.Lines.Count == 0)
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "EMPTY_LINES",
                Message: "PZ nie może być wystawione bez pozycji."));
        }

        if (!InvoicesController.IsIsoDateOrEmpty(request.IssueDate))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_DATE",
                Message: "issue_date musi być w formacie YYYY-MM-DD."));
        }

        // external_reference PRZED ValidateNotes: pusty = skan duplikatów bez sensu, > 495 = obcięty w Uwagach (skan nie trafi).
        if (UwagiFields.ValidateExternalReference(request.ExternalReference) is { } refError)
        {
            return UnprocessableEntity(InvoicesController.InvalidExternalReference(refError));
        }

        if (UwagiFields.ValidateNotes(request.Notes, request.ExternalReference) is { } notesError)
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "NOTES_TOO_LONG",
                Message: notesError,
                Details: new { max_length = UwagiFields.MaxNotesLength(request.ExternalReference), subiekt_limit = UwagiFields.MaxLength }));
        }

        try
        {
            var response = await _sfera.CreateReceiptAsync(request, ct);
            await _idempotency.SaveAsync(idempotencyKey, response, ct);
            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch (ContractorLookupUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponseDto(
                Code: "CONTRACTOR_LOOKUP_UNAVAILABLE",
                Message: ex.Message,
                Details: new { nip = ex.Nip }));
        }
        catch (DuplicateCheckUnavailableException ex)
        {
            // Skan duplikatów PZ padł - fail-closed: PZ NIE powstało, 503 z listy retry (ten sam klucz).
            return StatusCode(StatusCodes.Status503ServiceUnavailable, InvoicesController.DuplicateCheckUnavailable(ex));
        }
        catch (InvalidContractorSymbolException ex)
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_CONTRACTOR_SYMBOL",
                Message: ex.Message,
                Details: new { symbol = ex.Symbol, max_length = ContractorFields.SymbolMaxLength }));
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
            _logger.LogWarning("Duplicate PZ blocked: ref={Ref}, existing={Number} (subiekt_id={Id})",
                ex.ExternalReference, ex.ExistingNumber, ex.ExistingSubiektId);
            return Conflict(new ErrorResponseDto(
                Code: "DUPLICATE_RECEIPT",
                Message: ex.Message,
                Details: new
                {
                    existing_subiekt_id = ex.ExistingSubiektId,
                    existing_number = ex.ExistingNumber,
                    existing_bridge_id = $"sub_{ex.ExistingSubiektId}",
                    external_reference = ex.ExternalReference,
                }));
        }
        catch (SferaUnavailableException ex)
        {
            // Subiekt offline PRZED pierwszym zapisem (preflight sesji, spec W3) -> 503, klient ponawia tym samym
            // kluczem. Nic nie trafia do cache idempotencji (SaveAsync tylko po sukcesie).
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponseDto(
                Code: "SUBIEKT_UNAVAILABLE", Message: ex.Message));
        }
        catch (NotImplementedException ex)
        {
            _logger.LogError(ex, "Receipt operation NotImplemented: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receipt operation failed unexpectedly");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "INTERNAL_ERROR",
                Message: ex.GetType().Name + ": " + ex.Message,
                Details: new { stack = ex.StackTrace?.Split('\n').Take(10).ToArray() }));
        }
    }
}

/// <summary>Mapowanie ReceiptEditException -> (HTTP, ErrorResponseDto). Statyczne, testowalne bez kontrolera (spec §5.6).</summary>
internal static class ReceiptEditErrorMapper
{
    internal static string LockReasonCode(ReceiptLockReason? reason) => reason switch
    {
        ReceiptLockReason.LockedBySession => "LOCKED_BY_SESSION",
        ReceiptLockReason.ConcurrentModification => "CONCURRENT_MODIFICATION",
        ReceiptLockReason.PermissionDenied => "PERMISSION_DENIED",
        ReceiptLockReason.PeriodLocked => "PERIOD_LOCKED",
        ReceiptLockReason.Booked => "BOOKED",
        ReceiptLockReason.NotEditable => "NOT_EDITABLE",
        ReceiptLockReason.NotSaved => "NOT_SAVED",
        _ => "UNKNOWN",
    };

    internal static (int Status, ErrorResponseDto Error) ToResult(ReceiptEditException ex)
    {
        string? hresult = ReceiptEditException.HresultText(ex.Hresult);
        string? phase = ex.Phase?.ToString();
        switch (ex.Reason)
        {
            case ReceiptEditError.NotFound:
                return (StatusCodes.Status404NotFound, new ErrorResponseDto("RECEIPT_NOT_FOUND", ex.Message));
            case ReceiptEditError.NotAReceipt:
                return (StatusCodes.Status422UnprocessableEntity, new ErrorResponseDto("UNSUPPORTED_DOCUMENT_TYPE", ex.Message, ex.Details));
            case ReceiptEditError.Cancelled:
                return (StatusCodes.Status409Conflict, new ErrorResponseDto("RECEIPT_CANCELLED", ex.Message));
            case ReceiptEditError.RevisionMismatch:
                return (StatusCodes.Status409Conflict, new ErrorResponseDto("RECEIPT_REVISION_MISMATCH", ex.Message,
                    new { current_revision = ex.CurrentRevision }));
            case ReceiptEditError.Locked:
                bool transient = ex.LockReason is ReceiptLockReason.LockedBySession or ReceiptLockReason.ConcurrentModification;
                return (StatusCodes.Status409Conflict, new ErrorResponseDto("RECEIPT_LOCKED", ex.Message,
                    new
                    {
                        reason = LockReasonCode(ex.LockReason),
                        phase,
                        hresult,
                        sfera_message = ex.SferaMessage,
                        sfera_can_edit = ex.SferaCanEdit,
                        linked_documents = ex.LinkedDocuments ?? Array.Empty<LinkedDocumentDto>(),
                    },
                    RetryAfterSeconds: transient ? 30 : null));
            case ReceiptEditError.Validation:
                return (StatusCodes.Status422UnprocessableEntity, new ErrorResponseDto(ex.Code ?? "INVALID_REQUEST", ex.Message, ex.Details));
            case ReceiptEditError.UpdateFailed:
                return (StatusCodes.Status500InternalServerError, new ErrorResponseDto("RECEIPT_UPDATE_FAILED", ex.Message,
                    new { hresult, sfera_message = ex.SferaMessage, phase }));
            case ReceiptEditError.UpdateUnverified:
                return (StatusCodes.Status502BadGateway, new ErrorResponseDto("RECEIPT_UPDATE_UNVERIFIED", ex.Message,
                    new { saved_probably = ex.SavedProbably, hresult, sfera_message = ex.SferaMessage, phase }));
            case ReceiptEditError.ReadFailed:
                return (StatusCodes.Status502BadGateway, new ErrorResponseDto("SUBIEKT_QUERY_FAILED", ex.Message));
            default:
                return (StatusCodes.Status500InternalServerError, new ErrorResponseDto("INTERNAL_ERROR", ex.Message));
        }
    }
}
