using Users.Api.Domain.Constants;
using Users.Api.Domain.Entities;

namespace Users.Api.Tests;

public class UsuarioTests
{
    [Fact]
    public void Novo_usuario_nasce_ativo_com_perfil_padrao_e_versao_1()
    {
        var usuario = new Usuario("Ana", "ana@fcg.local", "hash");

        Assert.True(usuario.Ativo);
        Assert.Equal(Roles.Usuario, usuario.Perfil);
        Assert.Equal(1, usuario.CredencialVersao);
        Assert.NotEqual(Guid.Empty, usuario.Id);
    }

    [Fact]
    public void RotacionarCredencialJwt_incrementa_a_versao()
    {
        var usuario = new Usuario("Ana", "ana@fcg.local", "hash");

        usuario.RotacionarCredencialJwt();
        usuario.RotacionarCredencialJwt();

        Assert.Equal(3, usuario.CredencialVersao);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DefinirSenhaHash_rejeita_hash_vazio(string hash)
    {
        var usuario = new Usuario("Ana", "ana@fcg.local", "hash");

        Assert.Throws<ArgumentException>(() => usuario.DefinirSenhaHash(hash));
        Assert.Equal("hash", usuario.SenhaHash);
    }

    [Theory]
    [InlineData(Roles.Usuario, true)]
    [InlineData(Roles.Administrador, true)]
    [InlineData("Root", false)]
    [InlineData(null, false)]
    public void Roles_IsValid_aceita_apenas_perfis_conhecidos(string? perfil, bool esperado) =>
        Assert.Equal(esperado, Roles.IsValid(perfil));
}
