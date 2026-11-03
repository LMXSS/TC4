using Catalog.Api.Contracts;
using Catalog.Api.Domain.Constants;
using Catalog.Api.Infrastructure.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Api.Controllers;

/// <summary>
/// Busca avancada do catalogo (Fase 4), servida pelo Elasticsearch e independente da
/// consulta ao banco relacional.
/// </summary>
[ApiController]
[Route("api/games/search")]
public class SearchController : ControllerBase
{
    private readonly GameSearchService _search;
    private readonly GameSearchIndexer _indexer;

    public SearchController(GameSearchService search, GameSearchIndexer indexer)
    {
        _search = search;
        _indexer = indexer;
    }

    /// <summary>
    /// Busca com tolerancia a erros de digitacao (fuzzy) e ordenacao por relevancia.
    /// </summary>
    /// <remarks>
    /// Exemplos: <c>?q=eldn ring</c>, <c>?q=mundo aberto&amp;precoMaximo=200</c>,
    /// <c>?q=souls&amp;tag=rpg</c>. Sem <c>q</c>, lista os jogos ativos que passam nos filtros
    /// em ordem alfabetica. A resposta traz a relevancia (<c>_score</c>) de cada jogo, os
    /// trechos que casaram com o termo e qual motor respondeu.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GameSearchResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<GameSearchResponse>> Search(
        [FromQuery] string? q,
        [FromQuery] string? genero,
        [FromQuery] string? tag,
        [FromQuery] decimal? precoMaximo,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanho = 20,
        CancellationToken cancellationToken = default)
    {
        var query = GameSearchQuery.Criar(q, genero, tag, precoMaximo, pagina, tamanho);
        return Ok(await _search.SearchAsync(query, cancellationToken));
    }

    /// <summary>
    /// Busca simples no indice de texto do MongoDB (a busca da Fase 3), sem fuzzy.
    /// Mantida para comparar com a busca avancada.
    /// </summary>
    [HttpGet("basic")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GameSearchResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<GameSearchResponse>> SearchBasic(
        [FromQuery] string? q,
        [FromQuery] string? genero,
        [FromQuery] string? tag,
        [FromQuery] decimal? precoMaximo,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanho = 20,
        CancellationToken cancellationToken = default)
    {
        var query = GameSearchQuery.Criar(q, genero, tag, precoMaximo, pagina, tamanho);
        return Ok(await _search.BuscarNoMongoAsync(query, cancellationToken));
    }

    /// <summary>Reconstroi o indice do Elasticsearch a partir do SQLite e do MongoDB.</summary>
    [HttpPost("reindex")]
    [Authorize(Roles = Roles.Administrador)]
    [ProducesResponseType(typeof(ReindexResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReindexResponse>> Reindex(CancellationToken cancellationToken) =>
        Ok(await _indexer.ReindexarTudoAsync(cancellationToken));
}
