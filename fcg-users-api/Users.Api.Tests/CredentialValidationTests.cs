using Users.Api.Domain.Services;

namespace Users.Api.Tests;

public class CredentialValidationTests
{
    [Theory]
    [InlineData("ana@fcg.local")]
    [InlineData("  ana.souza@empresa.com.br  ")]
    [InlineData("a+tag@b.io")]
    public void IsValidEmail_aceita_emails_bem_formados(string email) =>
        Assert.True(CredentialValidation.IsValidEmail(email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sem-arroba.com")]
    [InlineData("ana@")]
    [InlineData("ana@dominio")]
    [InlineData("ana souza@fcg.local")]
    public void IsValidEmail_rejeita_emails_invalidos(string? email) =>
        Assert.False(CredentialValidation.IsValidEmail(email));

    [Theory]
    [InlineData("Senha@123")]
    [InlineData("çãoAbc#9")]
    [InlineData("muito-longa-mas-valida-1")]
    public void IsStrongPassword_aceita_senha_com_letra_numero_e_especial(string senha)
    {
        Assert.True(CredentialValidation.IsStrongPassword(senha, out var erro));
        Assert.Null(erro);
    }

    [Theory]
    [InlineData(null, "obrigatoria")]
    [InlineData("", "obrigatoria")]
    [InlineData("Ab@1", "8 caracteres")]
    [InlineData("12345678@", "letra")]
    [InlineData("Senha@abc", "numero")]
    [InlineData("Senha1234", "especial")]
    public void IsStrongPassword_explica_o_motivo_da_recusa(string? senha, string trechoEsperado)
    {
        Assert.False(CredentialValidation.IsStrongPassword(senha, out var erro));
        Assert.NotNull(erro);
        Assert.Contains(trechoEsperado, erro, StringComparison.OrdinalIgnoreCase);
    }
}
