using Catalog.Api.Contracts;
using Catalog.Api.Domain.Constants;
using Catalog.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Api.Controllers;

/// <summary>
/// Catalogo expandido armazenado no MongoDB, servido atraves do cache distribuido (Redis).
/// </summary>
[ApiController]
[Route("api/games")]
public class GameDetailsController : ControllerBase
{
    private readonly GameDetailsService _details;

    public GameDetailsController(GameDetailsService details) => _details = details;

    /// <summary>Documento expandido do jogo, incluindo o resumo das avaliacoes.</summary>
    [HttpGet("{id:guid}/details")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GameDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GameDetailsResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var details = await _details.GetAsync(id, cancellationToken);
        return Ok(details);
    }

    /// <summary>Cria ou substitui o documento expandido do jogo.</summary>
    [HttpPut("{id:guid}/details")]
    [Authorize(Roles = Roles.Administrador)]
    [ProducesResponseType(typeof(GameDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GameDetailsResponse>> Upsert(
        Guid id,
        [FromBody] UpsertGameDetailsRequest request,
        CancellationToken cancellationToken)
    {
        var details = await _details.UpsertAsync(id, request, cancellationToken);
        return Ok(details);
    }
}
