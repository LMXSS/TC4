namespace Catalog.Api.Contracts;

// ---- Busca avancada (Elasticsearch) ----

/// <summary>Parametros da busca, ja normalizados (pagina >= 1, tamanho entre 1 e 100).</summary>
public record GameSearchQuery(
    string? Termo,
    string? Genero,
    string? Tag,
    decimal? PrecoMaximo,
    int Pagina,
    int Tamanho)
{
    public const int TamanhoMaximo = 100;

    public static GameSearchQuery Criar(
        string? termo,
        string? genero,
        string? tag,
        decimal? precoMaximo,
        int pagina,
        int tamanho) =>
        new(
            string.IsNullOrWhiteSpace(termo) ? null : termo.Trim(),
            string.IsNullOrWhiteSpace(genero) ? null : genero.Trim(),
            string.IsNullOrWhiteSpace(tag) ? null : tag.Trim().ToLowerInvariant(),
            precoMaximo,
            Math.Max(1, pagina),
            Math.Clamp(tamanho, 1, TamanhoMaximo));

    public int Deslocamento => (Pagina - 1) * Tamanho;
}

/// <summary>Um jogo encontrado, com a nota de relevancia e os trechos que casaram.</summary>
public record GameSearchHit(
    Guid Id,
    string Titulo,
    string Genero,
    decimal Preco,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Plataformas,
    string CapaUrl,
    double? Relevancia,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Destaques);

/// <summary>
/// Resultado da busca. <see cref="Motor"/> diz quem respondeu: "elasticsearch" no caminho
/// normal ou "mongodb" quando o cluster de busca esta desligado ou indisponivel.
/// <see cref="TempoMs"/> e o tempo gasto no motor de busca na consulta original;
/// <see cref="DoCache"/> indica que a resposta saiu do Redis, sem tocar o Elasticsearch.
/// </summary>
public record GameSearchResponse(
    IReadOnlyList<GameSearchHit> Itens,
    int Pagina,
    int Tamanho,
    long Total,
    string Motor,
    long TempoMs,
    bool DoCache = false)
{
    public int TotalPaginas => Tamanho <= 0 ? 0 : (int)Math.Ceiling(Total / (double)Tamanho);
}

public record ReindexResponse(int Indexados, int Falhas, long TempoMs);
