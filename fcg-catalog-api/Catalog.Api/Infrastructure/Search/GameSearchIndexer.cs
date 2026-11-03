using System.Diagnostics;
using Catalog.Api.Contracts;
using Catalog.Api.Domain.Documents;
using Catalog.Api.Domain.Entities;
using Catalog.Api.Infrastructure.Mongo;
using Catalog.Api.Infrastructure.Persistence;
using Catalog.Api.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Catalog.Api.Infrastructure.Search;

/// <summary>
/// Sincroniza o catalogo com o indice do Elasticsearch.
///
/// Chamado pelo <c>GameDetailsService</c> sempre que um jogo e criado, editado, inativado
/// ou reativado no SQLite, e quando o documento expandido e gravado no MongoDB.
///
/// Uma falha do Elasticsearch nao desfaz a escrita no banco principal: o erro vai para o
/// log e para a metrica <c>fcg_catalog_search_index_operations_total{result="fail"}</c>,
/// e o reindex (no start ou via <c>POST /api/games/search/reindex</c>) recompoe o indice.
/// </summary>
public class GameSearchIndexer
{
    private const int TamanhoLote = 500;

    private readonly IGameSearchIndex _index;
    private readonly ElasticsearchOptions _options;
    private readonly CatalogDbContext _db;
    private readonly CatalogMongoContext _mongo;
    private readonly CatalogMetrics _metrics;
    private readonly ILogger<GameSearchIndexer> _logger;

    public GameSearchIndexer(
        IGameSearchIndex index,
        IOptions<ElasticsearchOptions> options,
        CatalogDbContext db,
        CatalogMongoContext mongo,
        CatalogMetrics metrics,
        ILogger<GameSearchIndexer> logger)
    {
        _index = index;
        _options = options.Value;
        _db = db;
        _mongo = mongo;
        _metrics = metrics;
        _logger = logger;
    }

    public bool Habilitado => _options.Enabled;

    /// <summary>
    /// Indexa um jogo. Sem <paramref name="detalhes"/>, busca o documento expandido no MongoDB
    /// para que o indice sempre carregue descricao, tags e plataformas atualizadas.
    /// </summary>
    public async Task IndexarAsync(Jogo jogo, GameDetailsDocument? detalhes = null, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return;

        try
        {
            detalhes ??= await _mongo.GameDetails
                .Find(d => d.GameId == jogo.Id.ToString())
                .FirstOrDefaultAsync(cancellationToken);

            await _index.IndexAsync(GameSearchDocument.From(jogo, detalhes), cancellationToken);
            _metrics.Indexacao("index", sucesso: true);
        }
        catch (Exception ex) when (ex is SearchUnavailableException or MongoException or TimeoutException)
        {
            _metrics.Indexacao("index", sucesso: false);
            _logger.LogWarning(ex,
                "Nao foi possivel indexar o jogo {GameId} no Elasticsearch. O banco principal ja foi atualizado; " +
                "o indice sera recomposto no proximo reindex.", jogo.Id);
        }
    }

    /// <summary>
    /// Reindexa o catalogo inteiro: jogos do SQLite (inclusive inativos, que ficam no indice
    /// com ativo=false e sao filtrados na busca) combinados com os documentos do MongoDB.
    /// </summary>
    public async Task<ReindexResponse> ReindexarTudoAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Elasticsearch desabilitado (Elasticsearch:Enabled=false).");

        var stopwatch = Stopwatch.StartNew();
        await _index.EnsureIndexAsync(cancellationToken);

        var jogos = await _db.Jogos.IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        var detalhes = await _mongo.GameDetails
            .Find(FilterDefinition<GameDetailsDocument>.Empty)
            .ToListAsync(cancellationToken);
        var detalhesPorId = detalhes.ToDictionary(d => d.GameId);

        int indexados = 0, falhas = 0;
        foreach (var lote in jogos.Chunk(TamanhoLote))
        {
            var documentos = lote
                .Select(j => GameSearchDocument.From(j, detalhesPorId.GetValueOrDefault(j.Id.ToString())))
                .ToList();

            var resultado = await _index.BulkIndexAsync(documentos, cancellationToken);
            indexados += resultado.Indexados;
            falhas += resultado.Falhas;
        }

        _metrics.Indexacao("bulk", sucesso: true, indexados);
        if (falhas > 0)
            _metrics.Indexacao("bulk", sucesso: false, falhas);

        _logger.LogInformation("Reindex concluido: {Indexados} jogos indexados, {Falhas} falhas, {Tempo} ms.",
            indexados, falhas, stopwatch.ElapsedMilliseconds);

        return new ReindexResponse(indexados, falhas, stopwatch.ElapsedMilliseconds);
    }
}
