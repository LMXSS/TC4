using System.Text.Json;
using Catalog.Api.Observability;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Catalog.Api.Infrastructure.Caching;

/// <summary>
/// Camada de cache distribuido sobre IDistributedCache (Redis em producao).
///
/// Duas responsabilidades alem do get/set:
/// 1. Instrumentacao: cada consulta vira hit/miss na metrica fcg_catalog_cache_requests_total,
///    o que alimenta o painel de taxa de acerto no Grafana.
/// 2. Resiliencia: uma falha do Redis nunca derruba a requisicao — e tratada como miss
///    e a origem (SQLite/MongoDB) responde normalmente.
/// </summary>
public class CatalogCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDistributedCache _cache;
    private readonly CatalogMetrics _metrics;
    private readonly CacheOptions _options;
    private readonly ILogger<CatalogCache> _logger;

    public CatalogCache(
        IDistributedCache cache,
        CatalogMetrics metrics,
        IOptions<CacheOptions> options,
        ILogger<CatalogCache> logger)
    {
        _cache = cache;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    public CacheOptions Options => _options;

    /// <summary>
    /// Le do cache; em caso de miss executa <paramref name="factory"/> e grava o resultado.
    /// </summary>
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        string cacheKind,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        var cached = await TryGetAsync<T>(key, cancellationToken);
        if (cached.Found)
        {
            _metrics.CacheHit(cacheKind);
            return cached.Value!;
        }

        _metrics.CacheMiss(cacheKind);
        var value = await factory(cancellationToken);
        await SetAsync(key, value, ttl, cancellationToken);
        return value;
    }

    public async Task<(bool Found, T? Value)> TryGetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await _cache.GetAsync(key, cancellationToken);
            if (bytes is null || bytes.Length == 0)
                return (false, default);

            return (true, JsonSerializer.Deserialize<T>(bytes, SerializerOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler a chave {CacheKey} do cache distribuido. Seguindo para a origem.", key);
            return (false, default);
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
            await _cache.SetAsync(
                key,
                bytes,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao gravar a chave {CacheKey} no cache distribuido.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao remover a chave {CacheKey} do cache distribuido.", key);
        }
    }

    /// <summary>
    /// Token de versao de um escopo (ex.: as avaliacoes de um jogo). O token entra na
    /// composicao das chaves; trocar o token invalida de uma vez todas as paginas
    /// daquele escopo, algo que IDistributedCache nao permite fazer por prefixo.
    /// </summary>
    public async Task<string> GetVersionAsync(string scope, CancellationToken cancellationToken = default)
    {
        var versionKey = $"{scope}:version";
        var current = await TryGetAsync<string>(versionKey, cancellationToken);
        if (current.Found && !string.IsNullOrEmpty(current.Value))
            return current.Value;

        var token = NewToken();
        await SetAsync(versionKey, token, TimeSpan.FromDays(7), cancellationToken);
        return token;
    }

    public Task BumpVersionAsync(string scope, CancellationToken cancellationToken = default) =>
        SetAsync($"{scope}:version", NewToken(), TimeSpan.FromDays(7), cancellationToken);

    private static string NewToken() => Guid.NewGuid().ToString("N")[..8];

    // ---- Chaves logicas do catalogo ----

    public static string GamesListKey() => "games:list";

    public static string GameDetailsKey(Guid gameId) => $"game:{gameId:N}:details";

    public static string ReviewsScope(Guid gameId) => $"game:{gameId:N}:reviews";

    public static string ReviewsPageKey(Guid gameId, string version, int page, int size) =>
        $"{ReviewsScope(gameId)}:{version}:p{page}:s{size}";

    public static string ReviewsSummaryKey(Guid gameId, string version) =>
        $"{ReviewsScope(gameId)}:{version}:summary";

    public static string SearchKey(string version, string fingerprint) =>
        $"search:{version}:{fingerprint}";

    public const string SearchScope = "search";
}
