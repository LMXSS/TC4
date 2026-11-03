namespace Catalog.Api.Infrastructure.Caching;

public class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Quando false, o IDistributedCache usa a implementacao em memoria do processo.
    /// Util para rodar a API sem Redis (testes locais, pipeline de CI).
    /// </summary>
    public bool RedisEnabled { get; set; } = true;

    public string RedisConnectionString { get; set; } = "localhost:6379";

    /// <summary>Prefixo das chaves no Redis, permitindo compartilhar a instancia entre servicos.</summary>
    public string InstanceName { get; set; } = "fcg:catalog:";

    /// <summary>TTL das listagens do catalogo relacional.</summary>
    public int GamesListSeconds { get; set; } = 60;

    /// <summary>TTL do documento expandido de um jogo (MongoDB).</summary>
    public int GameDetailsSeconds { get; set; } = 300;

    /// <summary>TTL das paginas de avaliacoes e do resumo agregado.</summary>
    public int ReviewsSeconds { get; set; } = 120;

    /// <summary>TTL dos resultados de busca no MongoDB.</summary>
    public int SearchSeconds { get; set; } = 90;
}
