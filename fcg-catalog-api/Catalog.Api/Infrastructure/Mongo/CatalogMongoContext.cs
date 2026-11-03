using Catalog.Api.Domain.Documents;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Catalog.Api.Infrastructure.Mongo;

/// <summary>Acesso as colecoes do MongoDB usando o driver oficial (MongoDB.Driver).</summary>
public class CatalogMongoContext
{
    public CatalogMongoContext(IMongoClient client, IOptions<MongoOptions> options)
    {
        var settings = options.Value;
        Database = client.GetDatabase(settings.Database);
        GameDetails = Database.GetCollection<GameDetailsDocument>(settings.GameDetailsCollection);
        Reviews = Database.GetCollection<GameReviewDocument>(settings.ReviewsCollection);
    }

    public IMongoDatabase Database { get; }

    public IMongoCollection<GameDetailsDocument> GameDetails { get; }

    public IMongoCollection<GameReviewDocument> Reviews { get; }
}
