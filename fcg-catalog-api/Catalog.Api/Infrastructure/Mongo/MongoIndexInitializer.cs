using Catalog.Api.Domain.Documents;
using MongoDB.Driver;

namespace Catalog.Api.Infrastructure.Mongo;

/// <summary>
/// Cria os indices do MongoDB no start da aplicacao. CreateMany e idempotente:
/// indices ja existentes com a mesma definicao sao ignorados.
/// </summary>
public class MongoIndexInitializer : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MongoIndexInitializer> _logger;

    public MongoIndexInitializer(IServiceProvider services, ILogger<MongoIndexInitializer> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<CatalogMongoContext>();

        try
        {
            await mongo.GameDetails.Indexes.CreateManyAsync(
                [
                    // Busca textual por titulo, descricao e tags.
                    new CreateIndexModel<GameDetailsDocument>(
                        Builders<GameDetailsDocument>.IndexKeys
                            .Text(d => d.Titulo)
                            .Text(d => d.Descricao)
                            .Text(d => d.Tags),
                        new CreateIndexOptions { Name = "ix_game_details_text", DefaultLanguage = "portuguese" }),
                    new CreateIndexModel<GameDetailsDocument>(
                        Builders<GameDetailsDocument>.IndexKeys.Ascending(d => d.Tags),
                        new CreateIndexOptions { Name = "ix_game_details_tags" }),
                    new CreateIndexModel<GameDetailsDocument>(
                        Builders<GameDetailsDocument>.IndexKeys
                            .Ascending(d => d.Ativo)
                            .Ascending(d => d.Genero),
                        new CreateIndexOptions { Name = "ix_game_details_ativo_genero" })
                ],
                cancellationToken);

            await mongo.Reviews.Indexes.CreateManyAsync(
                [
                    // Uma avaliacao por usuario por jogo.
                    new CreateIndexModel<GameReviewDocument>(
                        Builders<GameReviewDocument>.IndexKeys
                            .Ascending(r => r.GameId)
                            .Ascending(r => r.UsuarioId),
                        new CreateIndexOptions { Name = "ux_reviews_game_user", Unique = true }),
                    // Listagem paginada das avaliacoes mais recentes de um jogo.
                    new CreateIndexModel<GameReviewDocument>(
                        Builders<GameReviewDocument>.IndexKeys
                            .Ascending(r => r.GameId)
                            .Descending(r => r.CriadoEmUtc),
                        new CreateIndexOptions { Name = "ix_reviews_game_recentes" })
                ],
                cancellationToken);

            _logger.LogInformation("Indices do MongoDB verificados/criados.");
        }
        catch (Exception ex)
        {
            // Nao derruba a API: os endpoints relacionais continuam respondendo sem o Mongo.
            _logger.LogError(ex, "Falha ao criar indices do MongoDB. Verifique Mongo:ConnectionString.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
