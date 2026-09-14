using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Catalog.Api.Domain.Documents;

/// <summary>
/// Catalogo expandido: dados ricos e de esquema flexivel que nao cabem bem no modelo
/// relacional (midias, requisitos por plataforma, atributos livres por jogo).
/// A chave e o mesmo Id do jogo no SQLite, mantendo os dois modelos correlacionados.
/// </summary>
public class GameDetailsDocument
{
    [BsonId]
    public string GameId { get; set; } = string.Empty;

    /// <summary>Campos denormalizados do SQLite para permitir busca sem join entre bancos.</summary>
    public string Titulo { get; set; } = string.Empty;

    public string Genero { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Preco { get; set; }

    public bool Ativo { get; set; } = true;

    public string Descricao { get; set; } = string.Empty;
    public string Desenvolvedora { get; set; } = string.Empty;
    public string Publicadora { get; set; } = string.Empty;

    [BsonIgnoreIfNull]
    public DateTime? DataLancamento { get; set; }

    public List<string> Tags { get; set; } = [];
    public List<string> Plataformas { get; set; } = [];
    public List<string> Idiomas { get; set; } = [];

    public GameMedia Midias { get; set; } = new();

    /// <summary>Requisitos de hardware por perfil (minimo/recomendado) — esquema livre.</summary>
    public Dictionary<string, Dictionary<string, string>> Requisitos { get; set; } = [];

    public DateTime AtualizadoEmUtc { get; set; } = DateTime.UtcNow;
}

public class GameMedia
{
    public string CapaUrl { get; set; } = string.Empty;
    public string TrailerUrl { get; set; } = string.Empty;
    public List<string> Screenshots { get; set; } = [];
}
