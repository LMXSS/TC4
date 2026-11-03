using Catalog.Api.Contracts;
using Catalog.Api.Domain.Entities;
using Catalog.Api.Infrastructure.Caching;
using Catalog.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Infrastructure.Services;

public class GameService
{
    private readonly CatalogDbContext _db;
    private readonly CatalogCache _cache;
    private readonly GameDetailsService _details;

    public GameService(CatalogDbContext db, CatalogCache cache, GameDetailsService details)
    {
        _db = db;
        _cache = cache;
        _details = details;
    }

    public async Task<GameResponse> CreateAsync(CreateGameRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Titulo))
            throw new InvalidOperationException("Titulo e obrigatorio.");
        if (request.Preco < 0)
            throw new InvalidOperationException("Preco invalido.");

        var genero = request.Genero?.Trim() ?? string.Empty;
        var jogo = new Jogo(request.Titulo.Trim(), genero, request.Preco);
        _db.Jogos.Add(jogo);
        await _db.SaveChangesAsync(cancellationToken);
        await _details.SyncFromRelationalAsync(jogo, cancellationToken);
        return new GameResponse(jogo.Id, jogo.Titulo, jogo.Genero, jogo.Preco);
    }

    public async Task<GameResponse> UpdateAsync(Guid id, UpdateGameRequest request, CancellationToken cancellationToken = default)
    {
        var jogo = await _db.Jogos.FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (jogo is null)
            throw new KeyNotFoundException("Jogo nao encontrado.");

        jogo.Atualizar(request.Titulo, request.Genero, request.Preco);
        await _db.SaveChangesAsync(cancellationToken);
        await _details.SyncFromRelationalAsync(jogo, cancellationToken);
        return new GameResponse(jogo.Id, jogo.Titulo, jogo.Genero, jogo.Preco);
    }

    /// <summary>
    /// Listagem publica do catalogo — o endpoint mais chamado da API e o primeiro a
    /// sofrer com carga, entao responde pelo cache distribuido antes de tocar o SQLite.
    /// </summary>
    public async Task<IReadOnlyList<GameResponse>> ListAsync(CancellationToken cancellationToken = default) =>
        await _cache.GetOrCreateAsync<IReadOnlyList<GameResponse>>(
            CatalogCache.GamesListKey(),
            "games-list",
            TimeSpan.FromSeconds(_cache.Options.GamesListSeconds),
            async ct => await _db.Jogos.AsNoTracking()
                .OrderBy(g => g.Titulo)
                .Select(g => new GameResponse(g.Id, g.Titulo, g.Genero, g.Preco))
                .ToListAsync(ct),
            cancellationToken);

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var jogo = await _db.Jogos.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (jogo is null)
            throw new KeyNotFoundException("Jogo nao encontrado.");
        jogo.Inativar();
        await _db.SaveChangesAsync(cancellationToken);
        await _details.SyncFromRelationalAsync(jogo, cancellationToken);
    }

    public async Task<GameResponse> ReactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var jogo = await _db.Jogos.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (jogo is null)
            throw new KeyNotFoundException("Jogo nao encontrado.");
        jogo.Reativar();
        await _db.SaveChangesAsync(cancellationToken);
        await _details.SyncFromRelationalAsync(jogo, cancellationToken);
        return new GameResponse(jogo.Id, jogo.Titulo, jogo.Genero, jogo.Preco);
    }
}
