using Catalog.Api.Domain.Entities;

namespace Catalog.Api.Tests;

public class JogoTests
{
    [Fact]
    public void Novo_jogo_nasce_ativo()
    {
        var jogo = new Jogo("Hades", "Roguelike", 73.99m);

        Assert.True(jogo.Ativo);
        Assert.NotEqual(Guid.Empty, jogo.Id);
    }

    [Fact]
    public void Inativar_e_reativar_alternam_a_situacao()
    {
        var jogo = new Jogo("Hades", "Roguelike", 73.99m);

        jogo.Inativar();
        Assert.False(jogo.Ativo);

        jogo.Reativar();
        Assert.True(jogo.Ativo);
    }

    [Fact]
    public void Atualizar_aplica_trim_nos_textos()
    {
        var jogo = new Jogo("Hades", "Roguelike", 73.99m);

        jogo.Atualizar("  Hades II ", "  Acao ", 99.90m);

        Assert.Equal("Hades II", jogo.Titulo);
        Assert.Equal("Acao", jogo.Genero);
        Assert.Equal(99.90m, jogo.Preco);
    }

    [Theory]
    [InlineData("", 10)]
    [InlineData("   ", 10)]
    [InlineData("Hades", -0.01)]
    public void Atualizar_rejeita_titulo_vazio_ou_preco_negativo(string titulo, double preco)
    {
        var jogo = new Jogo("Hades", "Roguelike", 73.99m);

        Assert.Throws<ArgumentException>(() => jogo.Atualizar(titulo, "Acao", (decimal)preco));
        Assert.Equal("Hades", jogo.Titulo);
    }
}
