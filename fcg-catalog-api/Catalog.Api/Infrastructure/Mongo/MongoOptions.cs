namespace Catalog.Api.Infrastructure.Mongo;

public class MongoOptions
{
    public const string SectionName = "Mongo";

    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string Database { get; set; } = "fcg_catalog";
    public string GameDetailsCollection { get; set; } = "game_details";
    public string ReviewsCollection { get; set; } = "game_reviews";
}
