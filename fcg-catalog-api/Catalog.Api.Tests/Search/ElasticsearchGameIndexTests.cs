using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Catalog.Api.Contracts;
using Catalog.Api.Infrastructure.Search;
using Catalog.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Catalog.Api.Tests.Search;

public class ElasticsearchGameIndexTests
{
    private static readonly ElasticsearchOptions Opcoes = new()
    {
        Url = "http://elastic.test:9200",
        IndexName = "fcg-games"
    };

    private static (ElasticsearchGameIndex Index, FakeHttpHandler Http) Criar(ElasticsearchOptions? opcoes = null)
    {
        opcoes ??= Opcoes;
        var handler = new FakeHttpHandler();
        var http = new HttpClient(handler);
        ElasticsearchGameIndex.Configure(http, opcoes);
        return (new ElasticsearchGameIndex(http, Options.Create(opcoes), NullLogger<ElasticsearchGameIndex>.Instance), handler);
    }

    private static GameSearchDocument Documento(Guid? id = null) => new()
    {
        Id = (id ?? Guid.NewGuid()).ToString(),
        Titulo = "Elden Ring",
        Genero = "RPG de acao",
        Preco = 249.90m,
        Descricao = "Mundo aberto",
        Tags = ["souls-like", "rpg"]
    };

    [Fact]
    public async Task IndexAsync_faz_PUT_no_documento_com_refresh_wait_for()
    {
        var (index, http) = Criar();
        http.Responder(HttpStatusCode.Created);
        var doc = Documento();

        await index.IndexAsync(doc);

        var req = Assert.Single(http.Requisicoes);
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal($"/fcg-games/_doc/{doc.Id}?refresh=wait_for", req.Path);

        var corpo = JsonNode.Parse(req.Body!)!;
        Assert.Equal("Elden Ring", corpo["titulo"]!.GetValue<string>());
        Assert.Equal(249.90m, corpo["preco"]!.GetValue<decimal>());
        Assert.True(corpo["ativo"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EnsureIndexAsync_cria_o_indice_quando_ele_nao_existe()
    {
        var (index, http) = Criar();
        http.Responder(HttpStatusCode.NotFound).Responder(HttpStatusCode.OK, """{"acknowledged":true}""");

        await index.EnsureIndexAsync();

        Assert.Equal(2, http.Requisicoes.Count);
        Assert.Equal(HttpMethod.Head, http.Requisicoes[0].Method);
        Assert.Equal(HttpMethod.Put, http.Requisicoes[1].Method);
        Assert.Equal("/fcg-games", http.Requisicoes[1].Path);
        Assert.Contains("asciifolding", http.Requisicoes[1].Body);
    }

    [Fact]
    public async Task EnsureIndexAsync_nao_recria_indice_existente()
    {
        var (index, http) = Criar();
        http.Responder(HttpStatusCode.OK);

        await index.EnsureIndexAsync();

        Assert.Single(http.Requisicoes);
    }

    [Fact]
    public async Task EnsureIndexAsync_tolera_corrida_entre_replicas()
    {
        var (index, http) = Criar();
        http.Responder(HttpStatusCode.NotFound)
            .Responder(HttpStatusCode.BadRequest, """{"error":{"type":"resource_already_exists_exception"}}""");

        await index.EnsureIndexAsync();
    }

    [Fact]
    public async Task SearchAsync_converte_hits_relevancia_e_destaques()
    {
        var id = Guid.NewGuid();
        var (index, http) = Criar();
        http.Responder(HttpStatusCode.OK, $$"""
            {
              "hits": {
                "total": { "value": 1, "relation": "eq" },
                "hits": [
                  {
                    "_id": "{{id}}",
                    "_score": 7.123456,
                    "_source": {
                      "id": "{{id}}", "titulo": "Elden Ring", "genero": "RPG de acao", "preco": 249.9,
                      "ativo": true, "tags": ["rpg"], "plataformas": ["PC"], "capaUrl": "https://cdn/capa.jpg"
                    },
                    "highlight": { "titulo": ["<em>Elden</em> Ring"] }
                  }
                ]
              }
            }
            """);

        var resultado = await index.SearchAsync(GameSearchQuery.Criar("eldn", null, null, null, 1, 20));

        Assert.Equal("/fcg-games/_search", http.Requisicoes[0].Path);
        Assert.Equal(1, resultado.Total);
        Assert.Equal(ElasticsearchGameIndex.Motor, resultado.Motor);
        var hit = Assert.Single(resultado.Itens);
        Assert.Equal(id, hit.Id);
        Assert.Equal("Elden Ring", hit.Titulo);
        Assert.Equal(7.1235, hit.Relevancia);
        Assert.Equal("<em>Elden</em> Ring", Assert.Single(hit.Destaques["titulo"]));
    }

    [Fact]
    public async Task BulkIndexAsync_envia_ndjson_e_conta_falhas()
    {
        var (index, http) = Criar();
        http.Responder(HttpStatusCode.OK, """
            { "errors": true, "items": [
              { "index": { "status": 201 } },
              { "index": { "status": 400, "error": { "type": "mapper_parsing_exception" } } }
            ] }
            """);

        var resultado = await index.BulkIndexAsync([Documento(), Documento()]);

        Assert.Equal(new BulkIndexResult(1, 1), resultado);
        var req = Assert.Single(http.Requisicoes);
        Assert.Equal("/_bulk?refresh=true", req.Path);
        Assert.Equal("application/x-ndjson", req.ContentType);
        Assert.Equal(4, req.Body!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Erro_HTTP_vira_SearchUnavailableException()
    {
        var (index, http) = Criar();
        http.Responder(HttpStatusCode.ServiceUnavailable, """{"error":"cluster_block"}""");

        await Assert.ThrowsAsync<SearchUnavailableException>(() =>
            index.SearchAsync(GameSearchQuery.Criar("x", null, null, null, 1, 10)));
    }

    [Fact]
    public async Task Falha_de_rede_vira_SearchUnavailableException()
    {
        var (index, http) = Criar();
        http.Falhar(new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<SearchUnavailableException>(() => index.CountAsync());
    }

    [Fact]
    public void Configure_usa_ApiKey_quando_informada()
    {
        var http = new HttpClient();
        ElasticsearchGameIndex.Configure(http, new ElasticsearchOptions
        {
            Url = "https://cloud.elastic.test",
            ApiKey = "abc123",
            Username = "ignorado",
            Password = "ignorado"
        });

        Assert.Equal("ApiKey", http.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("abc123", http.DefaultRequestHeaders.Authorization.Parameter);
        Assert.Equal("https://cloud.elastic.test/", http.BaseAddress!.ToString());
    }

    [Fact]
    public void Configure_usa_basic_auth_com_usuario_e_senha()
    {
        var http = new HttpClient();
        ElasticsearchGameIndex.Configure(http, new ElasticsearchOptions { Username = "elastic", Password = "segredo" });

        Assert.Equal("Basic", http.DefaultRequestHeaders.Authorization!.Scheme);
        var decodificado = Encoding.UTF8.GetString(Convert.FromBase64String(http.DefaultRequestHeaders.Authorization.Parameter!));
        Assert.Equal("elastic:segredo", decodificado);
    }

    [Fact]
    public void Configure_sem_credenciais_nao_envia_Authorization()
    {
        var http = new HttpClient();
        ElasticsearchGameIndex.Configure(http, new ElasticsearchOptions());

        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }
}
