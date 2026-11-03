namespace Catalog.Api.Contracts;

// ---- Catalogo expandido (MongoDB) ----

public record GameMediaDto(
    string CapaUrl,
    string TrailerUrl,
    IReadOnlyList<string> Screenshots);

public record GameDetailsResponse(
    Guid Id,
    string Titulo,
    string Genero,
    decimal Preco,
    bool Ativo,
    string Descricao,
    string Desenvolvedora,
    string Publicadora,
    DateTime? DataLancamento,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Plataformas,
    IReadOnlyList<string> Idiomas,
    GameMediaDto Midias,
    IReadOnlyDictionary<string, Dictionary<string, string>> Requisitos,
    ReviewSummaryResponse Avaliacoes,
    DateTime AtualizadoEmUtc);

public record UpsertGameDetailsRequest(
    string? Descricao,
    string? Desenvolvedora,
    string? Publicadora,
    DateTime? DataLancamento,
    List<string>? Tags,
    List<string>? Plataformas,
    List<string>? Idiomas,
    GameMediaDto? Midias,
    Dictionary<string, Dictionary<string, string>>? Requisitos);

public record GameSearchItem(
    Guid Id,
    string Titulo,
    string Genero,
    decimal Preco,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Plataformas,
    string CapaUrl);

// ---- Avaliacoes (MongoDB) ----

public record CreateReviewRequest(int Nota, string? Titulo, string? Comentario);

public record ReviewResponse(
    string Id,
    Guid GameId,
    Guid UsuarioId,
    string UsuarioNome,
    int Nota,
    string Titulo,
    string Comentario,
    bool CompraVerificada,
    DateTime CriadoEmUtc,
    DateTime AtualizadoEmUtc);

/// <summary>Resumo agregado das avaliacoes de um jogo (pipeline de agregacao do MongoDB).</summary>
public record ReviewSummaryResponse(
    long Total,
    double NotaMedia,
    IReadOnlyDictionary<int, long> Distribuicao)
{
    public static ReviewSummaryResponse Vazio() =>
        new(0, 0, new Dictionary<int, long> { [1] = 0, [2] = 0, [3] = 0, [4] = 0, [5] = 0 });
}

// ---- Paginacao ----

public record PagedResponse<T>(IReadOnlyList<T> Itens, int Pagina, int Tamanho, long Total)
{
    public int TotalPaginas => Tamanho <= 0 ? 0 : (int)Math.Ceiling(Total / (double)Tamanho);
}
