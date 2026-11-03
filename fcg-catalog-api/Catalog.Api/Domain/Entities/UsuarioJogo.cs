namespace Catalog.Api.Domain.Entities;

public class UsuarioJogo
{
    private UsuarioJogo() { }

    public Guid UsuarioId { get; private set; }
    public Guid JogoId { get; private set; }
    public DateTime AdquiridoEmUtc { get; private set; }

    public UsuarioJogo(Guid usuarioId, Guid jogoId)
    {
        UsuarioId = usuarioId;
        JogoId = jogoId;
        AdquiridoEmUtc = DateTime.UtcNow;
    }
}
