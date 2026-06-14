using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Idempotency;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;
using MissingProductException = SubiektBridge.Api.Sfera.MissingProductException;
using DuplicateInvoiceException = SubiektBridge.Api.Sfera.DuplicateInvoiceException;

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

    /// <summary>
    /// Otwarte naleznosci (rozrachunki sprzedazy z otwartym saldem) w oknie kwoty - kandydaci do
    /// dopasowania z przychodzacym przelewem. Read-only (X-Bridge-Token, bez Idempotency-Key, jak
    /// /invoices, /bank-operations). Most NIE matchuje - zwraca okno (klient liczy [kwota, kwota/0.97]
    /// na prowizje procesora i dopasowuje po swojej stronie). Odczyt przez COM Sfery (nzf_Typ=39 +
    /// atrybuty FinDokument), NIE raw SQL. Trasa literalna 'open-receivables' ma pierwszenstwo nad {id}.
    /// </summary>
    [HttpGet("open-receivables")]
    public async Task<ActionResult<IReadOnlyList<OpenReceivableDto>>> OpenReceivables(
        [FromQuery(Name = "min_amount")] decimal? minAmount,
        [FromQuery(Name = "max_amount")] decimal? maxAmount,
        [FromQuery] string? currency,
        [FromQuery(Name = "contractor_id")] long? contractorId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        // Tylko PLN (jak /settlements). WartoscBiezaca jest ZAWSZE w PLN (CHM), wiec wiersz walutowy
        // mialby liczbe PLN pod etykieta obcej waluty (mylace), a i tak nie da sie go rozliczyc przez most.
        // Odrzucamy currency != PLN zamiast zwracac niespojne dane (spojnie z UNSUPPORTED_CURRENCY w FS).
        if (!string.IsNullOrWhiteSpace(currency)
            && !string.Equals(currency.Trim(), "PLN", StringComparison.OrdinalIgnoreCase))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "UNSUPPORTED_CURRENCY",
                Message: $"open-receivables zwraca wyłącznie należności PLN (most rozlicza tylko PLN) - otrzymano '{currency}'."));
        }

        var request = new OpenReceivablesQueryRequestDto(
            MinAmount: minAmount,
            MaxAmount: maxAmount,
            Currency: "PLN",
            ContractorId: contractorId,
            From: from,
            To: to,
            Limit: limit > 0 ? limit : 50);

        try
        {
            var items = await _sfera.QueryOpenReceivablesAsync(request, ct);
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryOpenReceivables failed");
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
        // ALE: weryfikujemy ze cached subiekt_id wciaz istnieje. Jesli user anulowal/usunal
        // FV w Subiekcie (a nasz Idempotency-Key zostal ten sam, np. ponowny klik 'Wystaw FV'
        // w UI), nie chcemy zwracac danych ducha - traktujemy request jako nowy.
        var cached = await _idempotency.TryGetAsync<InvoiceResponseDto>(idempotencyKey, ct);
        if (cached is not null)
        {
            var stillExists = await _sfera.FindInvoiceByIdAsync(cached.SubiektId, ct);
            if (stillExists is not null)
            {
                _logger.LogInformation("Idempotent replay for key {Key} -> invoice {Number}",
                    idempotencyKey, cached.Number);
                return Ok(cached);
            }
            _logger.LogWarning("Idempotent cache invalidated: subiekt_id={Id} ({Number}) nie istnieje juz w Subiekcie - traktujemy jako nowy request",
                cached.SubiektId, cached.Number);
            await _idempotency.DeleteAsync(idempotencyKey, ct);
        }

        // Walidacja totalsum vs Σ(line.qty * line.price_gross + shipping).
        var error = ValidateTotals(request) ?? ValidateBusinessRules(request);
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
        catch (DuplicateInvoiceException ex)
        {
            _logger.LogWarning("Duplicate invoice blocked: ref={Ref}, existing={Number} (subiekt_id={Id})",
                ex.ExternalReference, ex.ExistingNumber, ex.ExistingSubiektId);
            return Conflict(new ErrorResponseDto(
                Code: "DUPLICATE_INVOICE",
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

        // Idempotency replay z weryfikacja istnienia dokumentu (jak CreateInvoice).
        // Bez tego ponowne klikniecie 'Wystaw KFS' w UI po recznym usunieciu KFS-a z
        // Subiekta zwraca starego ducha (HTTP 200 + cached subiekt_id) zamiast wystawic
        // nowy dokument. Bug fix v0.7.48 (regresja: v0.7.23 dodal weryfikacje tylko dla
        // CreateInvoice, pominal CreateCorrection).
        var cached = await _idempotency.TryGetAsync<InvoiceResponseDto>(idempotencyKey, ct);
        if (cached is not null)
        {
            var stillExists = await _sfera.FindInvoiceByIdAsync(cached.SubiektId, ct);
            if (stillExists is not null)
            {
                _logger.LogInformation("Idempotent replay for key {Key} -> correction {Number}",
                    idempotencyKey, cached.Number);
                return Ok(cached);
            }
            _logger.LogWarning("Idempotent cache invalidated: KFS subiekt_id={Id} ({Number}) nie istnieje juz w Subiekcie - traktujemy jako nowy request",
                cached.SubiektId, cached.Number);
            await _idempotency.DeleteAsync(idempotencyKey, ct);
        }

        if (!TryParseSubiektIdFromBridgeId(id, out long sourceSubiektId))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_BRIDGE_ID",
                Message: $"Bridge ID '{id}' ma nieznany format. Oczekiwane: 'sub_<id>' (real Sfera) lub 'fake_inv_<id>' (dev mock)."));
        }

        if (!IsIsoDateOrEmpty(request.IssueDate) || !IsIsoDateOrEmpty(request.SourceInvoiceDate))
        {
            return UnprocessableEntity(new ErrorResponseDto(
                Code: "INVALID_DATE",
                Message: "issue_date i source_invoice_date muszą być w formacie YYYY-MM-DD."));
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
        catch (DuplicateInvoiceException ex)
        {
            _logger.LogWarning("Duplicate invoice blocked: ref={Ref}, existing={Number} (subiekt_id={Id})",
                ex.ExternalReference, ex.ExistingNumber, ex.ExistingSubiektId);
            return Conflict(new ErrorResponseDto(
                Code: "DUPLICATE_INVOICE",
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

    /// <summary>
    /// Jawnie odrzuca wartości, które Bridge wcześniej PO CICHU ignorował (audyt 2026-06-10
    /// pkt 4) - klient dostawał dokument inny niż zamówił, bez żadnego sygnału:
    /// - currency ≠ PLN → FV i tak wychodziła w PLN (Bridge nie obsługuje WalutaSymbol/Kurs),
    /// - vat_rate ≠ 23 na pozycji usługowej (EAN=null) lub wysyłce → usługa jednorazowa
    ///   dostaje stawkę domyślną Subiekta; Sfera pozwala zmienić stawkę tylko przez
    ///   SuPozycja.VatId (id z sl_StawkaVAT, różne per baza), czego Bridge nie mapuje,
    /// - zły format dat → daty były ignorowane (teraz idą do Sfery, patrz RealSferaSession).
    /// </summary>
    private static ErrorResponseDto? ValidateBusinessRules(InvoiceRequestDto request)
    {
        if (!string.IsNullOrEmpty(request.Currency)
            && !string.Equals(request.Currency, "PLN", StringComparison.OrdinalIgnoreCase))
        {
            return new ErrorResponseDto(
                Code: "UNSUPPORTED_CURRENCY",
                Message: $"Bridge wystawia dokumenty wyłącznie w PLN - otrzymano '{request.Currency}'.");
        }

        if (!IsIsoDateOrEmpty(request.IssueDate) || !IsIsoDateOrEmpty(request.SaleDate))
        {
            return new ErrorResponseDto(
                Code: "INVALID_DATE",
                Message: "issue_date i sale_date muszą być w formacie YYYY-MM-DD.");
        }

        const decimal supportedServiceVat = 23m;
        if (request.Shipping.Include && request.Shipping.VatRate != supportedServiceVat)
        {
            return new ErrorResponseDto(
                Code: "UNSUPPORTED_VAT_RATE",
                Message: $"Wysyłka: vat_rate={request.Shipping.VatRate} nieobsługiwane - usługa " +
                         "jednorazowa w Subiekcie dostaje stawkę domyślną 23%. Bridge nie mapuje " +
                         "vat_rate na sl_StawkaVAT.VatId.");
        }

        var badServiceLine = request.Lines.FirstOrDefault(
            l => string.IsNullOrEmpty(l.Ean) && l.VatRate != supportedServiceVat);
        if (badServiceLine is not null)
        {
            return new ErrorResponseDto(
                Code: "UNSUPPORTED_VAT_RATE",
                Message: $"Pozycja usługowa '{badServiceLine.NameFallback}': vat_rate=" +
                         $"{badServiceLine.VatRate} nieobsługiwane (jak wysyłka - tylko 23%). " +
                         "Pozycje towarowe (z EAN) biorą VAT z kartoteki towaru.");
        }

        return null;
    }

    // TryParseExact, NIE regex: kształt YYYY-MM-DD przepuszczałby daty niemożliwe
    // kalendarzowo (2026-02-31) -> 500 z TryParseExact w RealSferaSession zamiast 422,
    // a 5xx wg kontraktu klient retry'uje (w nieskończoność, bo data się nie naprawi).
    internal static bool IsIsoDateOrEmpty(string? date) =>
        string.IsNullOrWhiteSpace(date)
        || DateTime.TryParseExact(date, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

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
