using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubiektBridge.Api.Models;
using SubiektBridge.Api.Sfera;

namespace SubiektBridge.Api.Controllers;

[ApiController]
[Route("api/v1/products")]
[Authorize(AuthenticationSchemes = Auth.BridgeTokenAuthOptions.Scheme)]
public sealed class ProductsController : ControllerBase
{
    private readonly ISferaSession _sfera;

    public ProductsController(ISferaSession sfera) => _sfera = sfera;

    [HttpGet]
    public async Task<ActionResult<ProductDto>> FindByEan([FromQuery] string ean, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ean))
        {
            return BadRequest(new ErrorResponseDto(
                Code: "MISSING_EAN",
                Message: "Parametr 'ean' jest wymagany."));
        }

        try
        {
            var product = await _sfera.FindProductByEanAsync(ean, ct);
            return product is null ? NotFound() : Ok(product);
        }
        catch (NotImplementedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new ErrorResponseDto(
                Code: "NOT_IMPLEMENTED",
                Message: ex.Message));
        }
    }
}
