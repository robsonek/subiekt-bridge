using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

[ApiController]
[Route("api/v1/contractors")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class ContractorsController : ControllerBase
{
    private readonly ISferaSession _sfera;

    public ContractorsController(ISferaSession sfera) => _sfera = sfera;

    [HttpGet]
    public async Task<ActionResult<ContractorDto>> FindByNip([FromQuery] string nip, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(nip))
        {
            return BadRequest(new ErrorResponseDto(
                Code: "MISSING_NIP",
                Message: "Parametr 'nip' jest wymagany."));
        }

        try
        {
            var contractor = await _sfera.FindContractorByNipAsync(nip, ct);
            return contractor is null ? NotFound() : Ok(contractor);
        }
        catch (NotImplementedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
    }
}
