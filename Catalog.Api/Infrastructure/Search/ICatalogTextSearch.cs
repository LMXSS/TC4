using Catalog.Api.Contracts;

namespace Catalog.Api.Infrastructure.Search;

/// <summary>
/// Busca simples por texto no catalogo expandido (indice de texto do MongoDB).
/// Usada como fallback quando o Elasticsearch esta desligado ou indisponivel.
/// </summary>
public interface ICatalogTextSearch
{
    Task<PagedResponse<GameSearchItem>> SearchAsync(
        string? termo,
        string? tag,
        string? genero,
        decimal? precoMaximo,
        int pagina,
        int tamanho,
        CancellationToken cancellationToken = default);
}
