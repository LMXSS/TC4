using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Catalog.Api.Contracts;
using Catalog.Api.Infrastructure.Caching;
using Catalog.Api.Observability;
using Microsoft.Extensions.Options;

namespace Catalog.Api.Infrastructure.Search;

/// <summary>
/// Busca avancada do catalogo.
///
/// Caminho normal: Redis → Elasticsearch (fuzzy + relevancia). O resultado vai para o cache
/// com o mesmo token de versao das outras buscas, entao qualquer escrita no catalogo
/// invalida as buscas em cache de uma vez.
///
/// Degradacao: se o Elasticsearch estiver desligado ou fora do ar, a busca cai para o
/// indice de texto do MongoDB (sem fuzzy) e o campo <c>motor</c> da resposta mostra isso.
/// Esse resultado degradado nao e cacheado, para que a busca volte ao Elasticsearch assim
/// que ele se recuperar.
/// </summary>
public class GameSearchService
{
    public const string MotorFallback = "mongodb";

    private readonly IGameSearchIndex _index;
    private readonly ICatalogTextSearch _mongoSearch;
    private readonly CatalogCache _cache;
    private readonly CatalogMetrics _metrics;
    private readonly ElasticsearchOptions _options;
    private readonly ILogger<GameSearchService> _logger;

    public GameSearchService(
        IGameSearchIndex index,
        ICatalogTextSearch mongoSearch,
        CatalogCache cache,
        CatalogMetrics metrics,
        IOptions<ElasticsearchOptions> options,
        ILogger<GameSearchService> logger)
    {
        _index = index;
        _mongoSearch = mongoSearch;
        _cache = cache;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GameSearchResponse> SearchAsync(GameSearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return await BuscarNoMongoAsync(query, cancellationToken);

        var versao = await _cache.GetVersionAsync(CatalogCache.SearchScope, cancellationToken);
        var chave = CatalogCache.SearchKey(versao, Fingerprint(query));

        var emCache = await _cache.TryGetAsync<GameSearchResponse>(chave, cancellationToken);
        if (emCache.Found && emCache.Value is not null)
        {
            _metrics.CacheHit("search-elastic");
            return emCache.Value with { DoCache = true };
        }

        _metrics.CacheMiss("search-elastic");

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var resultado = await _index.SearchAsync(query, cancellationToken);
            _metrics.Busca(ElasticsearchGameIndex.Motor, stopwatch.Elapsed);

            await _cache.SetAsync(chave, resultado, TimeSpan.FromSeconds(_cache.Options.SearchSeconds), cancellationToken);
            return resultado;
        }
        catch (SearchUnavailableException ex)
        {
            _logger.LogWarning(ex, "Elasticsearch indisponivel. Respondendo a busca pelo MongoDB.");
            return await BuscarNoMongoAsync(query, cancellationToken);
        }
    }

    /// <summary>Busca simples no indice de texto do MongoDB — sem fuzzy, ordenada por titulo.</summary>
    public async Task<GameSearchResponse> BuscarNoMongoAsync(GameSearchQuery query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var pagina = await _mongoSearch.SearchAsync(
            query.Termo, query.Tag, query.Genero, query.PrecoMaximo, query.Pagina, query.Tamanho, cancellationToken);
        _metrics.Busca(MotorFallback, stopwatch.Elapsed);

        var itens = pagina.Itens
            .Select(i => new GameSearchHit(
                i.Id, i.Titulo, i.Genero, i.Preco, i.Tags, i.Plataformas, i.CapaUrl,
                Relevancia: null,
                Destaques: new Dictionary<string, IReadOnlyList<string>>()))
            .ToList();

        return new GameSearchResponse(itens, pagina.Pagina, pagina.Tamanho, pagina.Total, MotorFallback, stopwatch.ElapsedMilliseconds);
    }

    public static string Fingerprint(GameSearchQuery query)
    {
        var bruto = $"es|{query.Termo}|{query.Genero}|{query.Tag}|{query.PrecoMaximo}|{query.Pagina}|{query.Tamanho}"
            .ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(bruto));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
