using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Catalog.Api.Domain.Documents;

/// <summary>
/// Avaliacao de um jogo por um usuario. Volumetria alta e leitura muito mais
/// frequente que escrita — cenario tipico de NoSQL com cache distribuido na frente.
/// Indice unico (GameId, UsuarioId): uma avaliacao por usuario por jogo.
/// </summary>
public class GameReviewDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string GameId { get; set; } = string.Empty;
    public string UsuarioId { get; set; } = string.Empty;
    public string UsuarioNome { get; set; } = string.Empty;

    /// <summary>Nota de 1 a 5.</summary>
    public int Nota { get; set; }

    public string Titulo { get; set; } = string.Empty;
    public string Comentario { get; set; } = string.Empty;

    /// <summary>
    /// True quando o jogo ja estava na biblioteca do usuario no momento da avaliacao,
    /// ou seja, a compra passou pelo fluxo de pagamento aprovado.
    /// </summary>
    public bool CompraVerificada { get; set; }

    public DateTime CriadoEmUtc { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEmUtc { get; set; } = DateTime.UtcNow;
}
