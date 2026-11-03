using Catalog.Api.Domain.Documents;
using Catalog.Api.Domain.Entities;

namespace Catalog.Api.Infrastructure.Search;

/// <summary>
/// Documento indexado no Elasticsearch. Junta os dados transacionais do SQLite (titulo,
/// genero, preco, situacao) com o texto rico do catalogo expandido do MongoDB (descricao,
/// tags, plataformas, estudio) — tudo o que faz sentido pesquisar por texto.
/// </summary>
public class GameSearchDocument
{
    public string Id { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public string Genero { get; set; } = string.Empty;
    public decimal Preco { get; set; }
    public bool Ativo { get; set; } = true;
    public string Descricao { get; set; } = string.Empty;
    public string Desenvolvedora { get; set; } = string.Empty;
    public string Publicadora { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public List<string> Plataformas { get; set; } = [];
    public string CapaUrl { get; set; } = string.Empty;
    public DateTime AtualizadoEmUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Monta o documento a partir do jogo relacional e, se existir, do documento expandido.
    /// O SQLite e a fonte da verdade para titulo, genero, preco e situacao.
    /// </summary>
    public static GameSearchDocument From(Jogo jogo, GameDetailsDocument? detalhes) => new()
    {
        Id = jogo.Id.ToString(),
        Titulo = jogo.Titulo,
        Genero = jogo.Genero,
        Preco = jogo.Preco,
        Ativo = jogo.Ativo,
        Descricao = detalhes?.Descricao ?? string.Empty,
        Desenvolvedora = detalhes?.Desenvolvedora ?? string.Empty,
        Publicadora = detalhes?.Publicadora ?? string.Empty,
        Tags = detalhes?.Tags.ToList() ?? [],
        Plataformas = detalhes?.Plataformas.ToList() ?? [],
        CapaUrl = detalhes?.Midias?.CapaUrl ?? string.Empty,
        AtualizadoEmUtc = DateTime.UtcNow
    };
}
