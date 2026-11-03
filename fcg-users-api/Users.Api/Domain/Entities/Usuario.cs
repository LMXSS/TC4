using Users.Api.Domain.Constants;

namespace Users.Api.Domain.Entities;

public class Usuario
{
    private Usuario() { }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Nome { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string SenhaHash { get; private set; } = string.Empty;
    public string Perfil { get; private set; } = Roles.Usuario;
    public int CredencialVersao { get; private set; } = 1;
    public bool Ativo { get; private set; } = true;

    public Usuario(string nome, string email, string senhaHash, string perfil = Roles.Usuario)
    {
        Nome = nome;
        Email = email;
        SenhaHash = senhaHash;
        Perfil = perfil;
        CredencialVersao = 1;
        Ativo = true;
    }

    public void DefinirSenhaHash(string senhaHash)
    {
        if (string.IsNullOrWhiteSpace(senhaHash))
            throw new ArgumentException("Hash de senha invalido.", nameof(senhaHash));
        SenhaHash = senhaHash;
    }

    public void RotacionarCredencialJwt() =>
        CredencialVersao = CredencialVersao == int.MaxValue ? 1 : CredencialVersao + 1;
}
