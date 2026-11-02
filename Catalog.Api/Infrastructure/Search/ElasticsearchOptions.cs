namespace Catalog.Api.Infrastructure.Search;

/// <summary>
/// Configuracao do motor de busca (Elasticsearch 8.x ou OpenSearch 2.x — a API REST usada
/// aqui e compativel com os dois).
///
/// Nenhuma credencial tem valor padrao: usuario, senha ou API key chegam por variavel de
/// ambiente, injetada a partir de um Secret do Kubernetes.
/// </summary>
public class ElasticsearchOptions
{
    public const string SectionName = "Elasticsearch";

    /// <summary>
    /// Quando false, a busca cai para o indice de texto do MongoDB e nenhuma indexacao e
    /// feita. Util para rodar a API sem o cluster de busca (testes, pipeline de CI).
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string Url { get; set; } = "http://localhost:9200";

    public string IndexName { get; set; } = "fcg-games";

    /// <summary>Autenticacao basica (usuario elastic ou um usuario de servico).</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>API key no formato base64 (Elastic Cloud). Tem precedencia sobre usuario/senha.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Timeout de cada chamada ao cluster de busca.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// No start, se o indice estiver vazio, reindexa o catalogo inteiro a partir do SQLite
    /// e do MongoDB. Garante que um indice recem-criado ja nasce com os jogos existentes.
    /// </summary>
    public bool ReindexOnStartupIfEmpty { get; set; } = true;
}
