using System.Text.Json.Nodes;
using Catalog.Api.Contracts;
using Catalog.Api.Infrastructure.Search;

namespace Catalog.Api.Tests.Search;

public class ElasticQueryBuilderTests
{
    private static GameSearchQuery Query(
        string? termo = null, string? genero = null, string? tag = null, decimal? preco = null,
        int pagina = 1, int tamanho = 20) =>
        GameSearchQuery.Criar(termo, genero, tag, preco, pagina, tamanho);

    private static JsonArray Filtros(JsonObject body) => body["query"]!["bool"]!["filter"]!.AsArray();

    [Fact]
    public void Busca_com_termo_usa_fuzzy_AUTO_nos_campos_com_peso()
    {
        var body = ElasticQueryBuilder.SearchBody(Query("eldn ring"));

        var should = body["query"]!["bool"]!["should"]!.AsArray();
        var multiMatch = should[0]!["multi_match"]!;

        Assert.Equal("eldn ring", multiMatch["query"]!.GetValue<string>());
        Assert.Equal("AUTO", multiMatch["fuzziness"]!.GetValue<string>());
        var campos = multiMatch["fields"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Contains("titulo^4", campos);
        Assert.Contains("descricao", campos);
        Assert.Contains("tags^2", campos);
        Assert.Equal(1, body["query"]!["bool"]!["minimum_should_match"]!.GetValue<int>());
    }

    [Fact]
    public void Busca_com_termo_premia_frase_exata_e_prefixo_no_titulo()
    {
        var should = ElasticQueryBuilder.SearchBody(Query("elden"))["query"]!["bool"]!["should"]!.AsArray();

        Assert.NotNull(should[1]!["match_phrase"]?["titulo"]);
        Assert.NotNull(should[2]!["match_phrase_prefix"]?["titulo"]);
        Assert.True(should[1]!["match_phrase"]!["titulo"]!["boost"]!.GetValue<int>() >
                    should[2]!["match_phrase_prefix"]!["titulo"]!["boost"]!.GetValue<int>());
    }

    [Fact]
    public void Busca_com_termo_ordena_por_relevancia_e_desempata_por_titulo()
    {
        var sort = ElasticQueryBuilder.SearchBody(Query("rpg"))["sort"]!.AsArray();

        Assert.Equal("desc", sort[0]!["_score"]!["order"]!.GetValue<string>());
        Assert.Equal("asc", sort[1]!["titulo.keyword"]!["order"]!.GetValue<string>());
    }

    [Fact]
    public void Busca_sem_termo_nao_pontua_e_ordena_por_titulo()
    {
        var body = ElasticQueryBuilder.SearchBody(Query());

        Assert.Null(body["query"]!["bool"]!["should"]);
        var sort = body["sort"]!.AsArray();
        Assert.Single(sort);
        Assert.NotNull(sort[0]!["titulo.keyword"]);
    }

    [Fact]
    public void Sempre_filtra_apenas_jogos_ativos()
    {
        var filtros = Filtros(ElasticQueryBuilder.SearchBody(Query("qualquer")));

        Assert.Contains(filtros, f => f?["term"]?["ativo"]?.GetValue<bool>() == true);
    }

    [Fact]
    public void Filtros_de_genero_tag_e_preco_vao_em_filter_sem_afetar_a_nota()
    {
        var filtros = Filtros(ElasticQueryBuilder.SearchBody(Query("souls", genero: "RPG", tag: "Dificil", preco: 199.90m)));

        Assert.Contains(filtros, f => f?["term"]?["genero.keyword"]?.GetValue<string>() == "RPG");
        Assert.Contains(filtros, f => f?["term"]?["tags.keyword"]?.GetValue<string>() == "dificil");
        Assert.Contains(filtros, f => f?["range"]?["preco"]?["lte"]?.GetValue<decimal>() == 199.90m);
        Assert.Equal(4, filtros.Count);
    }

    [Theory]
    [InlineData(1, 20, 0)]
    [InlineData(3, 10, 20)]
    [InlineData(0, 10, 0)]
    public void Paginacao_vira_from_e_size(int pagina, int tamanho, int fromEsperado)
    {
        var body = ElasticQueryBuilder.SearchBody(Query("x", pagina: pagina, tamanho: tamanho));

        Assert.Equal(fromEsperado, body["from"]!.GetValue<int>());
        Assert.Equal(tamanho, body["size"]!.GetValue<int>());
        Assert.True(body["track_total_hits"]!.GetValue<bool>());
    }

    [Fact]
    public void Pede_destaques_do_titulo_e_da_descricao()
    {
        var campos = ElasticQueryBuilder.SearchBody(Query("x"))["highlight"]!["fields"]!.AsObject();

        Assert.True(campos.ContainsKey("titulo"));
        Assert.True(campos.ContainsKey("descricao"));
    }

    [Fact]
    public void Mapeamento_usa_analisador_sem_acento_e_subcampo_keyword()
    {
        var definicao = ElasticQueryBuilder.IndexDefinition();

        var filtrosAnalisador = definicao["settings"]!["analysis"]!["analyzer"]!["fcg_text"]!["filter"]!.AsArray()
            .Select(f => f!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "lowercase", "asciifolding" }, filtrosAnalisador);

        var titulo = definicao["mappings"]!["properties"]!["titulo"]!;
        Assert.Equal("fcg_text", titulo["analyzer"]!.GetValue<string>());
        Assert.Equal("keyword", titulo["fields"]!["keyword"]!["type"]!.GetValue<string>());
        Assert.Equal("scaled_float", definicao["mappings"]!["properties"]!["preco"]!["type"]!.GetValue<string>());
    }
}
