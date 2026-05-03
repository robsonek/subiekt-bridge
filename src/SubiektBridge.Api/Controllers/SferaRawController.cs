using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SubiektBridge.Api.Configuration;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

/// <summary>
/// Escape hatch: dowolne wywołanie metody Sfery z whitelistą w Bridge:AllowedRawSferaMethods.
/// Domyślnie whitelist jest pusty -> endpoint zwraca 403 dla wszystkiego.
/// Używać tylko do troubleshootingu/eksploracji, nie w produkcyjnym flow.
/// </summary>
[ApiController]
[Route("api/v1/sfera/raw")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class SferaRawController : ControllerBase
{
    private readonly ISferaSession _sfera;
    private readonly BridgeOptions _options;

    public SferaRawController(ISferaSession sfera, IOptions<BridgeOptions> options)
    {
        _sfera = sfera;
        _options = options.Value;
    }

    [HttpPost]
    public async Task<ActionResult<object?>> Invoke([FromBody] SferaRawRequestDto request, CancellationToken ct)
    {
        if (!_options.AllowedRawSferaMethods.Contains(request.Method, StringComparer.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponseDto(
                Code: "METHOD_NOT_WHITELISTED",
                Message: $"Metoda '{request.Method}' nie jest na whiteliście w Bridge:AllowedRawSferaMethods."));
        }

        try
        {
            var result = await _sfera.InvokeRawAsync(request.Method, request.Args, ct);
            return Ok(result);
        }
        catch (NotImplementedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
    }
}
