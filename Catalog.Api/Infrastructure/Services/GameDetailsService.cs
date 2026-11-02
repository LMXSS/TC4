using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Catalog.Api.Contracts;
using Catalog.Api.Domain.Documents;
using Catalog.Api.Domain.Entities;
using Catalog.Api.Infrastructure.Caching;
using Catalog.Api.Infrastructure.Mongo;
using Catalog.Api.Infrastructure.Persistence;
using Catalog.Api.Infrastructure.Search;
using Catalog.Api.Observability;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace Catalog.Api.Infrastructure.Services;

/// <summary>
/// Catalogo expandido no MongoDB.
///
/// O SQLite continua sendo a fonte da verdade transacional do jogo (titulo, genero, preco,
/// e o vinculo com a biblioteca do usuario). O MongoDB guarda a parte de esquema flexivel —
/// descricao, midias, tags, requisitos por plataforma — que mudaria o schema relacional a
/// cada jogo novo. Titulo, genero, preco e situacao ficam denormalizados no documento para
/// que a busca aconteca inteiramente no Mongo, sem join entre bancos.
///
/// Fase 4: toda escrita aqui tambem atualiza o indice do Elasticsearch (GameSearchIndexer),
/// que passa a ser o motor da busca avancada. A busca no Mongo continua como fallback.
/// </summary>
public class GameDetailsService : ICatalogTextSearch
{
    private readonly CatalogDbContext _db;
    private readonly CatalogMongoContext _mongo;
    private readonly CatalogCache _cache;
    private readonly ReviewService _reviews;
    private readonly CatalogMetrics _metrics;
    private readonly GameSearchIndexer _indexer;

    public GameDetailsService(
        CatalogDbContext db,
        CatalogMongoContext mongo,
        CatalogCache cache,
        ReviewService reviews,
        CatalogMetrics metrics,
        GameSearchIndexer indexer)
    {
        _db = db;
        _mongo = mongo;
        _cache = cache;
        _reviews = reviews;
        _metrics = metrics;
        _indexer = indexer;
    }

    public Task<GameDetailsResponse> GetAsync(Guid gameId, CancellationToken cancellationToken = default) =>
        _cache.GetOrCreateAsync(
            CatalogCache.GameDetailsKey(gameId),
            "game-details",
            TimeSpan.FromSeconds(_cache.Options.GameDetailsSeconds),
            ct => LoadDetailsAsync(gameId, ct),
            cancellationToken);

    private async Task<GameDetailsResponse> LoadDetailsAsync(Guid gameId, CancellationToken cancellationToken)
    {
        var jogo = await _db.Jogos.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken);
        if (jogo is null)
            throw new KeyNotFoundException("Jogo nao encontrado.");

        var stopwatch = Stopwatch.StartNew();
        var documento = await _mongo.GameDetails
            .Find(d => d.GameId == gameId.ToString())
            .FirstOrDefaultAsync(cancellationToken);
        _metrics.MongoOperacao("game_details_find", stopwatch.Elapsed);

        // Sem documento no Mongo o jogo ainda responde, so que apenas com os dados relacionais.
        documento ??= NovoDocumento(jogo);

        var avaliacoes = await _reviews.GetSummaryAsync(gameId, cancellationToken);
        return Map(jogo, documento, avaliacoes);
    }

    public async Task<GameDetailsResponse> UpsertAsync(
        Guid gameId,
        UpsertGameDetailsRequest request,
        CancellationToken cancellationToken = default)
    {
        var jogo = await _db.Jogos.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken);
        if (jogo is null)
            throw new KeyNotFoundException("Jogo nao encontrado.");

        var documento = new GameDetailsDocument
        {
            GameId = gameId.ToString(),
            Titulo = jogo.Titulo,
            Genero = jogo.Genero,
            Preco = jogo.Preco,
            Ativo = jogo.Ativo,
            Descricao = request.Descricao?.Trim() ?? string.Empty,
            Desenvolvedora = request.Desenvolvedora?.Trim() ?? string.Empty,
            Publicadora = request.Publicadora?.Trim() ?? string.Empty,
            DataLancamento = request.DataLancamento,
            Tags = Normalizar(request.Tags),
            Plataformas = Normalizar(request.Plataformas),
            Idiomas = Normalizar(request.Idiomas),
            Midias = new GameMedia
            {
                CapaUrl = request.Midias?.CapaUrl ?? string.Empty,
                TrailerUrl = request.Midias?.TrailerUrl ?? string.Empty,
                Screenshots = request.Midias?.Screenshots?.ToList() ?? []
            },
            Requisitos = request.Requisitos ?? [],
            AtualizadoEmUtc = DateTime.UtcNow
        };

        var stopwatch = Stopwatch.StartNew();
        await _mongo.GameDetails.ReplaceOneAsync(
            d => d.GameId == documento.GameId,
            documento,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
        _metrics.MongoOperacao("game_details_upsert", stopwatch.Elapsed);

        await _indexer.IndexarAsync(jogo, documento, cancellationToken);
        await InvalidarAsync(gameId, cancellationToken);

        var avaliacoes = await _reviews.GetSummaryAsync(gameId, cancellationToken);
        return Map(jogo, documento, avaliacoes);
    }

    /// <summary>
    /// Mantem os campos denormalizados do documento alinhados com o SQLite depois de
    /// criar, atualizar, inativar ou reativar um jogo — e reindexa o jogo no Elasticsearch.
    /// </summary>
    public async Task SyncFromRelationalAsync(Jogo jogo, CancellationToken cancellationToken = default)
    {
        var update = Builders<GameDetailsDocument>.Update
            .Set(d => d.Titulo, jogo.Titulo)
            .Set(d => d.Genero, jogo.Genero)
            .Set(d => d.Preco, jogo.Preco)
            .Set(d => d.Ativo, jogo.Ativo)
            .Set(d => d.AtualizadoEmUtc, DateTime.UtcNow)
            .SetOnInsert(d => d.GameId, jogo.Id.ToString());

        var stopwatch = Stopwatch.StartNew();
        await _mongo.GameDetails.UpdateOneAsync(
            d => d.GameId == jogo.Id.ToString(),
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
        _metrics.MongoOperacao("game_details_sync", stopwatch.Elapsed);

        await _indexer.IndexarAsync(jogo, cancellationToken: cancellationToken);
        await InvalidarAsync(jogo.Id, cancellationToken);
    }

    /// <summary>Busca no catalogo expandido: texto livre, tags, genero e faixa de preco.</summary>
    public async Task<PagedResponse<GameSearchItem>> SearchAsync(
        string? termo,
        string? tag,
        string? genero,
        decimal? precoMaximo,
        int pagina,
        int tamanho,
        CancellationToken cancellationToken = default)
    {
        pagina = Math.Max(1, pagina);
        tamanho = Math.Clamp(tamanho, 1, 100);

        var versao = await _cache.GetVersionAsync(CatalogCache.SearchScope, cancellationToken);
        var chave = CatalogCache.SearchKey(versao, Fingerprint(termo, tag, genero, precoMaximo, pagina, tamanho));

        return await _cache.GetOrCreateAsync(
            chave,
            "search",
            TimeSpan.FromSeconds(_cache.Options.SearchSeconds),
            ct => ExecutarBuscaAsync(termo, tag, genero, precoMaximo, pagina, tamanho, ct),
            cancellationToken);
    }

    private async Task<PagedResponse<GameSearchItem>> ExecutarBuscaAsync(
        string? termo,
        string? tag,
        string? genero,
        decimal? precoMaximo,
        int pagina,
        int tamanho,
        CancellationToken cancellationToken)
    {
        var builder = Builders<GameDetailsDocument>.Filter;
        var filtro = builder.Eq(d => d.Ativo, true);

        if (!string.IsNullOrWhiteSpace(termo))
            filtro &= builder.Text(termo.Trim());

        if (!string.IsNullOrWhiteSpace(tag))
            filtro &= builder.AnyEq(d => d.Tags, tag.Trim().ToLowerInvariant());

        if (!string.IsNullOrWhiteSpace(genero))
            filtro &= builder.Eq(d => d.Genero, genero.Trim());

        if (precoMaximo is not null)
            filtro &= builder.Lte(d => d.Preco, precoMaximo.Value);

        var stopwatch = Stopwatch.StartNew();
        var total = await _mongo.GameDetails.CountDocumentsAsync(filtro, cancellationToken: cancellationToken);
        var documentos = await _mongo.GameDetails
            .Find(filtro)
            .SortBy(d => d.Titulo)
            .Skip((pagina - 1) * tamanho)
            .Limit(tamanho)
            .ToListAsync(cancellationToken);
        _metrics.MongoOperacao("game_details_search", stopwatch.Elapsed);

        var itens = documentos
            .Select(d => new GameSearchItem(
                Guid.Parse(d.GameId),
                d.Titulo,
                d.Genero,
                d.Preco,
                d.Tags,
                d.Plataformas,
                d.Midias.CapaUrl))
            .ToList();

        return new PagedResponse<GameSearchItem>(itens, pagina, tamanho, total);
    }

    /// <summary>Invalida o documento em cache e os resultados de busca.</summary>
    public async Task InvalidarAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        await _cache.RemoveAsync(CatalogCache.GameDetailsKey(gameId), cancellationToken);
        await _cache.RemoveAsync(CatalogCache.GamesListKey(), cancellationToken);
        await _cache.BumpVersionAsync(CatalogCache.SearchScope, cancellationToken);
    }

    private static GameDetailsDocument NovoDocumento(Jogo jogo) => new()
    {
        GameId = jogo.Id.ToString(),
        Titulo = jogo.Titulo,
        Genero = jogo.Genero,
        Preco = jogo.Preco,
        Ativo = jogo.Ativo
    };

    private static GameDetailsResponse Map(Jogo jogo, GameDetailsDocument doc, ReviewSummaryResponse avaliacoes) =>
        new(
            jogo.Id,
            jogo.Titulo,
            jogo.Genero,
            jogo.Preco,
            jogo.Ativo,
            doc.Descricao,
            doc.Desenvolvedora,
            doc.Publicadora,
            doc.DataLancamento,
            doc.Tags,
            doc.Plataformas,
            doc.Idiomas,
            new GameMediaDto(doc.Midias.CapaUrl, doc.Midias.TrailerUrl, doc.Midias.Screenshots),
            doc.Requisitos,
            avaliacoes,
            doc.AtualizadoEmUtc);

    private static List<string> Normalizar(List<string>? valores) =>
        valores is null
            ? []
            : valores
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();

    private static string Fingerprint(string? termo, string? tag, string? genero, decimal? preco, int pagina, int tamanho)
    {
        var bruto = $"{termo}|{tag}|{genero}|{preco}|{pagina}|{tamanho}".ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(bruto));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
