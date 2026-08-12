using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

/// <summary>
/// Wysylka e-Faktur do KSeF przez Sfere (EFakturyKSeFManager). POST = idempotentny "advance"
/// maszyny stanow StatusKSeF (sprawdz -> generuj -> wyslij -> dociagnij numer); GET = czysty
/// odczyt (dokument w 'processing' aktualizuje sie POST-em, nie GET-em). Bez Idempotency-Key -
/// zrodlem prawdy jest StatusKSeF dokumentu w Subiekcie (powtorny POST bezpieczny z definicji).
///
/// Trzeci kontroler na [Route("api/v1/invoices")] (obok Invoices/Settlements) - ASP.NET Core
/// to dopuszcza, bo szablony akcji sa rozne ({id}/ksef vs {id} vs {id}/settlements).
/// </summary>
[ApiController]
[Route("api/v1/invoices")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class KsefController : ControllerBase
{
    private readonly ISferaSession _sfera;
    private readonly ILogger<KsefController> _logger;

    public KsefController(ISferaSession sfera, ILogger<KsefController> logger)
    {
        _sfera = sfera;
        _logger = logger;
    }

    [HttpPost("{id}/ksef")]
    public async Task<ActionResult<KsefStatusResponseDto>> Send(string id, CancellationToken ct)
    {
        if (!TryParseSubiektIdFromBridgeId(id, out long documentSubiektId))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_BRIDGE_ID",
                Message: $"Bridge ID '{id}' ma nieznany format. Oczekiwane: 'sub_<id>' (real Sfera) lub 'fake_inv_<id>' (dev mock)."));
        }

        try
        {
            var result = await _sfera.SendInvoiceToKsefAsync(documentSubiektId, ct);
            _logger.LogInformation("KSeF: doc={DocId} -> {Status}, numer={Numer}",
                documentSubiektId, result.KsefStatus, result.KsefNumber);

            // Mapowanie WYCZERPUJACE - 200 TYLKO dla registered. Stan nie-koncowy po zakonczonej
            // operacji (np. 'generated' po padnietej wysylce) NIE moze udawac sukcesu.
            return result.KsefStatus switch
            {
                KsefStatusMap.Registered => Ok(result),
                // Operacja w toku (timeout capu / rownolegly POST na gate) -> klient ponawia POST.
                KsefStatusMap.Sending or KsefStatusMap.Processing => Accepted(result),
                _ => StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto(
                    Code: "KSEF_SEND_INCOMPLETE",
                    Message: ($"Wysylka nie osiagnela stanu koncowego (ksef_status={result.KsefStatus}). " +
                              (result.Message ?? "")).TrimEnd())),
            };
        }
        catch (KsefException ex)
        {
            _logger.LogWarning(ex, "KSeF: doc={DocId} odrzucony flow: {Reason} - {Message}", documentSubiektId, ex.Reason, ex.Message);
            return MapKsefError(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KSeF: wysylka doc={DocId} padla nieoczekiwanie", documentSubiektId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto(
                Code: "SFERA_ERROR",
                Message: ex.GetType().Name + ": " + ex.Message));
        }
    }

    [HttpGet("{id}/ksef")]
    public async Task<ActionResult<KsefStatusResponseDto>> GetStatus(string id, CancellationToken ct)
    {
        if (!TryParseSubiektIdFromBridgeId(id, out long documentSubiektId))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_BRIDGE_ID",
                Message: $"Bridge ID '{id}' ma nieznany format. Oczekiwane: 'sub_<id>'."));
        }

        try
        {
            var result = await _sfera.GetKsefStatusAsync(documentSubiektId, ct);
            if (result is null)
            {
                return NotFound(new ErrorResponseDto(
                    Code: "INVOICE_NOT_FOUND",
                    Message: $"Dokument o subiekt_id={documentSubiektId} nie istnieje w Subiekcie."));
            }
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KSeF: odczyt stanu doc={DocId} padl", documentSubiektId);
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto(
                Code: "SUBIEKT_QUERY_FAILED",
                Message: ex.Message));
        }
    }

    private ActionResult MapKsefError(KsefException ex) => ex.Reason switch
    {
        KsefError.DocumentNotFound => NotFound(new ErrorResponseDto("INVOICE_NOT_FOUND", ex.Message)),
        KsefError.UnsupportedDocumentType => UnprocessableEntity(new ErrorResponseDto("UNSUPPORTED_DOCUMENT_TYPE", ex.Message)),
        KsefError.NotKsefInvoice => UnprocessableEntity(new ErrorResponseDto("NOT_KSEF_INVOICE", ex.Message)),
        KsefError.ValidationFailed => UnprocessableEntity(new ErrorResponseDto("KSEF_VALIDATION_FAILED", ex.Message)),
        KsefError.Rejected => UnprocessableEntity(new ErrorResponseDto("KSEF_REJECTED", ex.Message)),
        KsefError.CommunicationError => StatusCode(StatusCodes.Status502BadGateway, new ErrorResponseDto("KSEF_COMMUNICATION_ERROR", ex.Message)),
        _ => StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponseDto("SFERA_ERROR", ex.Message)),
    };

    /// <summary>
    /// Parsuje bridge_id na subiekt_id (kopia z InvoicesController - parser jest private static,
    /// nie wspoldzielony; wzorzec repo jak w SettlementsController). Akceptuje "sub_{N}" (real),
    /// "fake_inv_{NNNNNN}" (dev mock FS) oraz "fake_kfs_{NNNNNN}" (dev mock KFS - KSeF wspiera
    /// korekty, wiec parser MUSI znac ich fake id; settlements tego nie potrzebowaly, bo korekty
    /// odrzucaja).
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

        const string fakeKfsPrefix = "fake_kfs_";
        if (bridgeId.StartsWith(fakeKfsPrefix, StringComparison.Ordinal) &&
            int.TryParse(bridgeId.AsSpan(fakeKfsPrefix.Length), out var kfsCounter))
        {
            subiektId = 2_000_000 + kfsCounter;
            return true;
        }

        return false;
    }
}
