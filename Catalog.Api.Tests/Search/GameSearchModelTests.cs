using Catalog.Api.Contracts;
using Catalog.Api.Domain.Entities;
using Catalog.Api.Infrastructure.Search;

namespace Catalog.Api.Tests.Search;

public class GameSearchModelTests
{
    [Fact]
    public void Criar_normaliza_termo_tag_pagina_e_tamanho()
    {
        var query = GameSearchQuery.Criar("  elden ring ", "  RPG ", " Souls-Like ", 100m, pagina: -3, tamanho: 500);

        Assert.Equal("elden ring", query.Termo);
        Assert.Equal("RPG", query.Genero);
        Assert.Equal("souls-like", query.Tag);
        Assert.Equal(1, query.Pagina);
        Assert.Equal(GameSearchQuery.TamanhoMaximo, query.Tamanho);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Criar_trata_texto_em_branco_como_ausente(string? valor)
    {
        var query = GameSearchQuery.Criar(valor, valor, valor, null, 1, 10);

        Assert.Null(query.Termo);
        Assert.Null(query.Genero);
        Assert.Null(query.Tag);
    }

    [Theory]
    [InlineData(1, 20, 0)]
    [InlineData(2, 20, 20)]
    [InlineData(5, 7, 28)]
    public void Deslocamento_e_calculado_pela_pagina(int pagina, int tamanho, int esperado) =>
        Assert.Equal(esperado, GameSearchQuery.Criar(null, null, null, null, pagina, tamanho).Deslocamento);

    [Fact]
    public void Documento_sem_catalogo_expandido_carrega_so_os_dados_relacionais()
    {
        var jogo = new Jogo("Hades", "Roguelike", 73.99m);

        var doc = GameSearchDocument.From(jogo, null);

        Assert.Equal(jogo.Id.ToString(), doc.Id);
        Assert.Equal("Hades", doc.Titulo);
        Assert.Equal(73.99m, doc.Preco);
        Assert.Empty(doc.Tags);
        Assert.Equal(string.Empty, doc.Descricao);
    }

    [Fact]
    public void TotalPaginas_arredonda_para_cima()
    {
        var resposta = new GameSearchResponse([], 1, 20, 41, "elasticsearch", 3);

        Assert.Equal(3, resposta.TotalPaginas);
    }
}
