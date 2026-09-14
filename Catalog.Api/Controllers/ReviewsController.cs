using Catalog.Api.Contracts;
using Catalog.Api.Extensions;
using Catalog.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Api.Controllers;

/// <summary>Avaliacoes de jogos persistidas no MongoDB e servidas via cache distribuido.</summary>
[ApiController]
[Route("api/games/{gameId:guid}/reviews")]
public class ReviewsController : ControllerBase
{
    private readonly ReviewService _reviews;

    public ReviewsController(ReviewService reviews) => _reviews = reviews;

    /// <summary>Lista paginada das avaliacoes mais recentes do jogo.</summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResponse<ReviewResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<ReviewResponse>>> List(
        Guid gameId,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanho = 20,
        CancellationToken cancellationToken = default)
    {
        var resultado = await _reviews.ListAsync(gameId, pagina, tamanho, cancellationToken);
        return Ok(resultado);
    }

    /// <summary>Resumo agregado: total, nota media e distribuicao das notas.</summary>
    [HttpGet("summary")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ReviewSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReviewSummaryResponse>> Summary(Guid gameId, CancellationToken cancellationToken)
    {
        var resumo = await _reviews.GetSummaryAsync(gameId, cancellationToken);
        return Ok(resumo);
    }

    /// <summary>Cria ou atualiza a avaliacao do usuario autenticado para o jogo.</summary>
    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReviewResponse>> Upsert(
        Guid gameId,
        [FromBody] CreateReviewRequest request,
        CancellationToken cancellationToken)
    {
        var avaliacao = await _reviews.UpsertAsync(
            gameId,
            User.GetUserId(),
            User.GetUserName(),
            request,
            cancellationToken);
        return Ok(avaliacao);
    }

    /// <summary>Remove a avaliacao do usuario autenticado.</summary>
    [HttpDelete("me")]
    [Authorize]
    [ProducesResponseType(typeof(ApiMessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiMessageResponse>> Delete(Guid gameId, CancellationToken cancellationToken)
    {
        var removida = await _reviews.DeleteAsync(gameId, User.GetUserId(), cancellationToken);
        if (!removida)
            return NotFound(new ApiMessageResponse("Avaliacao nao encontrada."));

        return Ok(new ApiMessageResponse("Avaliacao removida."));
    }
}
