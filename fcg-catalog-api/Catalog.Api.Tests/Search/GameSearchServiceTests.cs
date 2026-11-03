using Catalog.Api.Contracts;
using Catalog.Api.Infrastructure.Search;
using Catalog.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Catalog.Api.Tests.Search;

public class GameSearchServiceTests
{
    private readonly IGameSearchIndex _index = Substitute.For<IGameSearchIndex>();
    private readonly ICatalogTextSearch _mongo = Substitute.For<ICatalogTextSearch>();

    private GameSearchService Criar(bool habilitado = true) => new(
        _index,
        _mongo,
        TestSupport.Cache(),
        TestSupport.Metrics(),
        Options.Create(new ElasticsearchOptions { Enabled = habilitado }),
        NullLogger<GameSearchService>.Instance);

    private static readonly GameSearchQuery Consulta = GameSearchQuery.Criar("eldn ring", null, null, null, 1, 20);

    private static GameSearchResponse RespostaElastic() => new(
        [
            new GameSearchHit(Guid.NewGuid(), "Elden Ring", "RPG", 249.9m, ["rpg"], ["PC"], "", 8.5,
                new Dictionary<string, IReadOnlyList<string>>())
        ],
        1, 20, 1, ElasticsearchGameIndex.Motor, 12);

    private void MongoResponde() =>
        _mongo.SearchAsync(default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PagedResponse<GameSearchItem>(
                [new GameSearchItem(Guid.NewGuid(), "Elden Ring", "RPG", 249.9m, ["rpg"], ["PC"], "")], 1, 20, 1));

    [Fact]
    public async Task Responde_pelo_Elasticsearch_quando_disponivel()
    {
        _index.SearchAsync(Arg.Is(Consulta), Arg.Any<CancellationToken>()).Returns(RespostaElastic());

        var resultado = await Criar().SearchAsync(Consulta);

        Assert.Equal(ElasticsearchGameIndex.Motor, resultado.Motor);
        Assert.False(resultado.DoCache);
        Assert.Equal(8.5, Assert.Single(resultado.Itens).Relevancia);
        await _mongo.DidNotReceiveWithAnyArgs().SearchAsync(default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task Segunda_busca_igual_sai_do_cache_sem_tocar_o_Elasticsearch()
    {
        _index.SearchAsync(Arg.Is(Consulta), Arg.Any<CancellationToken>()).Returns(RespostaElastic());
        var servico = Criar();

        await servico.SearchAsync(Consulta);
        var segunda = await servico.SearchAsync(Consulta);

        Assert.True(segunda.DoCache);
        Assert.Equal("Elden Ring", Assert.Single(segunda.Itens).Titulo);
        await _index.Received(1).SearchAsync(Arg.Any<GameSearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cai_para_o_MongoDB_quando_o_Elasticsearch_esta_fora()
    {
        _index.SearchAsync(Arg.Any<GameSearchQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SearchUnavailableException("fora do ar"));
        MongoResponde();

        var resultado = await Criar().SearchAsync(Consulta);

        Assert.Equal(GameSearchService.MotorFallback, resultado.Motor);
        Assert.Null(Assert.Single(resultado.Itens).Relevancia);
    }

    [Fact]
    public async Task Resultado_degradado_nao_e_cacheado()
    {
        _index.SearchAsync(Arg.Any<GameSearchQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SearchUnavailableException("fora do ar"));
        MongoResponde();
        var servico = Criar();

        await servico.SearchAsync(Consulta);
        await servico.SearchAsync(Consulta);

        // As duas chamadas tentaram o Elasticsearch: nada do fallback ficou no cache.
        await _index.Received(2).SearchAsync(Arg.Any<GameSearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Com_Elasticsearch_desabilitado_usa_direto_o_MongoDB()
    {
        MongoResponde();

        var resultado = await Criar(habilitado: false).SearchAsync(Consulta);

        Assert.Equal(GameSearchService.MotorFallback, resultado.Motor);
        await _index.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
    }

    [Theory]
    [InlineData("Elden Ring", "elden ring")]
    [InlineData("  elden ring ", "elden ring")]
    public void Fingerprint_ignora_caixa_e_espacos_nas_pontas(string a, string b) =>
        Assert.Equal(
            GameSearchService.Fingerprint(GameSearchQuery.Criar(a, null, null, null, 1, 20)),
            GameSearchService.Fingerprint(GameSearchQuery.Criar(b, null, null, null, 1, 20)));

    [Fact]
    public void Fingerprint_muda_com_a_pagina()
    {
        var p1 = GameSearchService.Fingerprint(GameSearchQuery.Criar("rpg", null, null, null, 1, 20));
        var p2 = GameSearchService.Fingerprint(GameSearchQuery.Criar("rpg", null, null, null, 2, 20));

        Assert.NotEqual(p1, p2);
    }
}
