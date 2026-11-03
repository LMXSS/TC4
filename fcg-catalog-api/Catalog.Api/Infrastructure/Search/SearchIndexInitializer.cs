using Microsoft.Extensions.Options;

namespace Catalog.Api.Infrastructure.Search;

/// <summary>
/// Prepara o indice do Elasticsearch em segundo plano, sem atrasar o start da API.
///
/// 1. Cria o indice com o mapeamento, tentando de novo com espera crescente enquanto o
///    cluster de busca ainda sobe (comum logo apos um deploy).
/// 2. Se o indice estiver vazio, reindexa o catalogo a partir do SQLite e do MongoDB —
///    assim um indice novo (ou recriado) ja nasce com todos os jogos.
/// </summary>
public class SearchIndexInitializer : BackgroundService
{
    private const int MaxTentativas = 10;

    private readonly IServiceProvider _services;
    private readonly ElasticsearchOptions _options;
    private readonly ILogger<SearchIndexInitializer> _logger;

    public SearchIndexInitializer(
        IServiceProvider services,
        IOptions<ElasticsearchOptions> options,
        ILogger<SearchIndexInitializer> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Elasticsearch desabilitado: a busca usara o indice de texto do MongoDB.");
            return;
        }

        for (var tentativa = 1; tentativa <= MaxTentativas && !stoppingToken.IsCancellationRequested; tentativa++)
        {
            try
            {
                using var scope = _services.CreateScope();
                var index = scope.ServiceProvider.GetRequiredService<IGameSearchIndex>();
                await index.EnsureIndexAsync(stoppingToken);

                if (_options.ReindexOnStartupIfEmpty && await index.CountAsync(stoppingToken) == 0)
                {
                    var indexer = scope.ServiceProvider.GetRequiredService<GameSearchIndexer>();
                    await indexer.ReindexarTudoAsync(stoppingToken);
                }

                _logger.LogInformation("Indice {Index} pronto no Elasticsearch.", _options.IndexName);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                var espera = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, tentativa)));
                _logger.LogWarning(ex,
                    "Elasticsearch ainda indisponivel (tentativa {Tentativa}/{Max}). Nova tentativa em {Espera}s.",
                    tentativa, MaxTentativas, espera.TotalSeconds);
                await Task.Delay(espera, stoppingToken);
            }
        }

        _logger.LogError(
            "Nao foi possivel preparar o indice {Index}. A busca segue pelo MongoDB ate o Elasticsearch responder; " +
            "depois rode POST /api/games/search/reindex.", _options.IndexName);
    }
}
