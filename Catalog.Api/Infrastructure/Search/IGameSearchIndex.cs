using Catalog.Api.Contracts;

namespace Catalog.Api.Infrastructure.Search;

/// <summary>Indice de busca do catalogo (Elasticsearch/OpenSearch).</summary>
public interface IGameSearchIndex
{
    /// <summary>Cria o indice com settings e mapeamento, se ainda nao existir.</summary>
    Task EnsureIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>Quantidade de documentos no indice.</summary>
    Task<long> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Cria ou substitui o documento de um jogo.</summary>
    Task IndexAsync(GameSearchDocument document, CancellationToken cancellationToken = default);

    /// <summary>Indexa varios jogos em uma unica chamada <c>_bulk</c>.</summary>
    Task<BulkIndexResult> BulkIndexAsync(
        IReadOnlyCollection<GameSearchDocument> documents,
        CancellationToken cancellationToken = default);

    Task<GameSearchResponse> SearchAsync(GameSearchQuery query, CancellationToken cancellationToken = default);
}

public record BulkIndexResult(int Indexados, int Falhas);

/// <summary>
/// O cluster de busca nao respondeu ou respondeu com erro. Quem chama decide se degrada
/// (busca no MongoDB) ou propaga.
/// </summary>
public class SearchUnavailableException : Exception
{
    public SearchUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
