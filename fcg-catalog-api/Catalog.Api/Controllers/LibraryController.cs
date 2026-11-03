using System.IdentityModel.Tokens.Jwt;
using Catalog.Api.Contracts;
using Catalog.Api.Extensions;
using Catalog.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Api.Controllers;

[ApiController]
[Route("api/users/me")]
[Authorize]
public class LibraryController : ControllerBase
{
    private readonly LibraryService _library;

    public LibraryController(LibraryService library) => _library = library;

    [HttpGet("library")]
    [ProducesResponseType(typeof(IReadOnlyList<GameResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<GameResponse>>> GetLibrary(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var list = await _library.GetMyLibraryAsync(userId, cancellationToken);
        return Ok(list);
    }

    [HttpPost("library/games/{gameId:guid}")]
    [ProducesResponseType(typeof(StartPurchaseResponse), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<StartPurchaseResponse>> StartPurchase(Guid gameId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var email = User.FindFirst(JwtRegisteredClaimNames.Email)?.Value ?? string.Empty;
        var result = await _library.StartPurchaseAsync(userId, gameId, email, cancellationToken);
        return Accepted(result);
    }
}
