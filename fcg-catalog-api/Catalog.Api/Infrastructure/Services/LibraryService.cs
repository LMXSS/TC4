using Catalog.Api.Contracts;
using Catalog.Api.Domain.Entities;
using Catalog.Api.Infrastructure.Persistence;
using Catalog.Api.Observability;
using FCG.Messaging.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Infrastructure.Services;

public class LibraryService
{
    private readonly CatalogDbContext _db;
    private readonly IPublishEndpoint _publish;
    private readonly CatalogMetrics _metrics;

    public LibraryService(CatalogDbContext db, IPublishEndpoint publish, CatalogMetrics metrics)
    {
        _db = db;
        _publish = publish;
        _metrics = metrics;
    }

    public async Task<IReadOnlyList<GameResponse>> GetMyLibraryAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var list = await _db.UsuarioJogos.AsNoTracking()
            .Where(uj => uj.UsuarioId == userId)
            .Join(_db.Jogos.IgnoreQueryFilters(), uj => uj.JogoId, j => j.Id, (uj, j) => j)
            .OrderBy(j => j.Titulo)
            .Select(j => new GameResponse(j.Id, j.Titulo, j.Genero, j.Preco))
            .ToListAsync(cancellationToken);
        return list;
    }

    public async Task<StartPurchaseResponse> StartPurchaseAsync(
        Guid userId,
        Guid gameId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var game = await _db.Jogos.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken);
        if (game is null)
            throw new KeyNotFoundException("Jogo nao encontrado.");

        var already = await _db.UsuarioJogos.AnyAsync(
            uj => uj.UsuarioId == userId && uj.JogoId == gameId,
            cancellationToken);
        if (already)
            throw new InvalidOperationException("Jogo ja esta na sua biblioteca.");

        var orderId = Guid.NewGuid();
        await _publish.Publish(
            new OrderPlacedEvent(orderId, userId, gameId, game.Preco, userEmail),
            cancellationToken);

        _metrics.CompraIniciada();

        return new StartPurchaseResponse(orderId, "Pedido enviado para processamento de pagamento.");
    }

    public async Task ConfirmPurchaseAsync(Guid userId, Guid gameId, CancellationToken cancellationToken = default)
    {
        var gameExists = await _db.Jogos.IgnoreQueryFilters().AnyAsync(g => g.Id == gameId && g.Ativo, cancellationToken);
        if (!gameExists)
            return;

        var already = await _db.UsuarioJogos.AnyAsync(
            uj => uj.UsuarioId == userId && uj.JogoId == gameId,
            cancellationToken);
        if (already)
            return;

        _db.UsuarioJogos.Add(new UsuarioJogo(userId, gameId));
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _metrics.CompraConfirmada();
        }
        catch (DbUpdateException)
        {
            // Idempotencia: outro consumidor inseriu o mesmo par.
        }
    }
}
