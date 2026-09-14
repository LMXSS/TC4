using System.Diagnostics;
using Catalog.Api.Contracts;
using Catalog.Api.Domain.Documents;
using Catalog.Api.Infrastructure.Caching;
using Catalog.Api.Infrastructure.Mongo;
using Catalog.Api.Infrastructure.Persistence;
using Catalog.Api.Observability;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace Catalog.Api.Infrastructure.Services;

/// <summary>
/// Sistema de avaliacoes de jogos no MongoDB.
///
/// Leitura muito mais frequente que escrita e agregacao cara (media e distribuicao de
/// notas), entao tanto as paginas quanto o resumo passam pelo Redis. A invalidacao usa
/// um token de versao por jogo: gravar uma avaliacao troca o token e derruba de uma vez
/// todas as paginas em cache daquele jogo.
/// </summary>
public class ReviewService
{
    private readonly CatalogDbContext _db;
    private readonly CatalogMongoContext _mongo;
    private readonly CatalogCache _cache;
    private readonly CatalogMetrics _metrics;

    public ReviewService(
        CatalogDbContext db,
        CatalogMongoContext mongo,
        CatalogCache cache,
        CatalogMetrics metrics)
    {
        _db = db;
        _mongo = mongo;
        _cache = cache;
        _metrics = metrics;
    }

    public async Task<PagedResponse<ReviewResponse>> ListAsync(
        Guid gameId,
        int pagina,
        int tamanho,
        CancellationToken cancellationToken = default)
    {
        pagina = Math.Max(1, pagina);
        tamanho = Math.Clamp(tamanho, 1, 100);

        var versao = await _cache.GetVersionAsync(CatalogCache.ReviewsScope(gameId), cancellationToken);

        return await _cache.GetOrCreateAsync(
            CatalogCache.ReviewsPageKey(gameId, versao, pagina, tamanho),
            "reviews",
            TimeSpan.FromSeconds(_cache.Options.ReviewsSeconds),
            ct => CarregarPaginaAsync(gameId, pagina, tamanho, ct),
            cancellationToken);
    }

    private async Task<PagedResponse<ReviewResponse>> CarregarPaginaAsync(
        Guid gameId,
        int pagina,
        int tamanho,
        CancellationToken cancellationToken)
    {
        var chave = gameId.ToString();

        var stopwatch = Stopwatch.StartNew();
        var total = await _mongo.Reviews.CountDocumentsAsync(
            r => r.GameId == chave, cancellationToken: cancellationToken);
        var documentos = await _mongo.Reviews
            .Find(r => r.GameId == chave)
            .SortByDescending(r => r.CriadoEmUtc)
            .Skip((pagina - 1) * tamanho)
            .Limit(tamanho)
            .ToListAsync(cancellationToken);
        _metrics.MongoOperacao("reviews_page", stopwatch.Elapsed);

        var itens = documentos.Select(Map).ToList();
        return new PagedResponse<ReviewResponse>(itens, pagina, tamanho, total);
    }

    /// <summary>Resumo agregado (total, nota media e distribuicao) via pipeline do MongoDB.</summary>
    public async Task<ReviewSummaryResponse> GetSummaryAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        var versao = await _cache.GetVersionAsync(CatalogCache.ReviewsScope(gameId), cancellationToken);

        return await _cache.GetOrCreateAsync(
            CatalogCache.ReviewsSummaryKey(gameId, versao),
            "reviews-summary",
            TimeSpan.FromSeconds(_cache.Options.ReviewsSeconds),
            ct => CalcularResumoAsync(gameId, ct),
            cancellationToken);
    }

    private async Task<ReviewSummaryResponse> CalcularResumoAsync(Guid gameId, CancellationToken cancellationToken)
    {
        var chave = gameId.ToString();

        var stopwatch = Stopwatch.StartNew();
        var grupos = await _mongo.Reviews.Aggregate()
            .Match(r => r.GameId == chave)
            .Group(r => r.Nota, g => new NotaAgrupada { Nota = g.Key, Quantidade = g.Count() })
            .ToListAsync(cancellationToken);
        _metrics.MongoOperacao("reviews_summary", stopwatch.Elapsed);

        if (grupos.Count == 0)
            return ReviewSummaryResponse.Vazio();

        var distribuicao = new Dictionary<int, long> { [1] = 0, [2] = 0, [3] = 0, [4] = 0, [5] = 0 };
        long total = 0;
        long soma = 0;

        foreach (var grupo in grupos)
        {
            distribuicao[grupo.Nota] = grupo.Quantidade;
            total += grupo.Quantidade;
            soma += (long)grupo.Nota * grupo.Quantidade;
        }

        var media = total == 0 ? 0 : Math.Round(soma / (double)total, 2);
        return new ReviewSummaryResponse(total, media, distribuicao);
    }

    /// <summary>
    /// Cria ou atualiza a avaliacao do usuario para o jogo (upsert pela chave GameId+UsuarioId).
    /// Marca CompraVerificada quando o jogo ja consta na biblioteca do usuario, ou seja,
    /// quando o fluxo de pagamento foi aprovado.
    /// </summary>
    public async Task<ReviewResponse> UpsertAsync(
        Guid gameId,
        Guid usuarioId,
        string usuarioNome,
        CreateReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Nota < 1 || request.Nota > 5)
            throw new InvalidOperationException("A nota deve estar entre 1 e 5.");

        var jogoExiste = await _db.Jogos.IgnoreQueryFilters()
            .AnyAsync(g => g.Id == gameId, cancellationToken);
        if (!jogoExiste)
            throw new KeyNotFoundException("Jogo nao encontrado.");

        var compraVerificada = await _db.UsuarioJogos
            .AnyAsync(uj => uj.UsuarioId == usuarioId && uj.JogoId == gameId, cancellationToken);

        var chaveJogo = gameId.ToString();
        var chaveUsuario = usuarioId.ToString();
        var agora = DateTime.UtcNow;

        var update = Builders<GameReviewDocument>.Update
            .Set(r => r.Nota, request.Nota)
            .Set(r => r.Titulo, request.Titulo?.Trim() ?? string.Empty)
            .Set(r => r.Comentario, request.Comentario?.Trim() ?? string.Empty)
            .Set(r => r.UsuarioNome, usuarioNome)
            .Set(r => r.CompraVerificada, compraVerificada)
            .Set(r => r.AtualizadoEmUtc, agora)
            .SetOnInsert(r => r.GameId, chaveJogo)
            .SetOnInsert(r => r.UsuarioId, chaveUsuario)
            .SetOnInsert(r => r.CriadoEmUtc, agora);

        var stopwatch = Stopwatch.StartNew();
        var documento = await _mongo.Reviews.FindOneAndUpdateAsync(
            r => r.GameId == chaveJogo && r.UsuarioId == chaveUsuario,
            update,
            new FindOneAndUpdateOptions<GameReviewDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
        _metrics.MongoOperacao("reviews_upsert", stopwatch.Elapsed);

        _metrics.AvaliacaoRegistrada();
        await InvalidarAsync(gameId, cancellationToken);

        return Map(documento);
    }

    public async Task<bool> DeleteAsync(Guid gameId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var chaveJogo = gameId.ToString();
        var chaveUsuario = usuarioId.ToString();

        var stopwatch = Stopwatch.StartNew();
        var resultado = await _mongo.Reviews.DeleteOneAsync(
            r => r.GameId == chaveJogo && r.UsuarioId == chaveUsuario,
            cancellationToken);
        _metrics.MongoOperacao("reviews_delete", stopwatch.Elapsed);

        if (resultado.DeletedCount == 0)
            return false;

        await InvalidarAsync(gameId, cancellationToken);
        return true;
    }

    private async Task InvalidarAsync(Guid gameId, CancellationToken cancellationToken)
    {
        // Troca o token de versao: todas as paginas e o resumo em cache deixam de ser alcancados.
        await _cache.BumpVersionAsync(CatalogCache.ReviewsScope(gameId), cancellationToken);
        // O documento expandido carrega o resumo das avaliacoes, entao tambem sai do cache.
        await _cache.RemoveAsync(CatalogCache.GameDetailsKey(gameId), cancellationToken);
    }

    private static ReviewResponse Map(GameReviewDocument doc) =>
        new(
            doc.Id ?? string.Empty,
            Guid.Parse(doc.GameId),
            Guid.Parse(doc.UsuarioId),
            doc.UsuarioNome,
            doc.Nota,
            doc.Titulo,
            doc.Comentario,
            doc.CompraVerificada,
            doc.CriadoEmUtc,
            doc.AtualizadoEmUtc);

    private class NotaAgrupada
    {
        public int Nota { get; set; }
        public int Quantidade { get; set; }
    }
}
