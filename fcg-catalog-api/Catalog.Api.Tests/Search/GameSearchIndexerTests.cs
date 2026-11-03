using Catalog.Api.Domain.Documents;
using Catalog.Api.Domain.Entities;
using Catalog.Api.Infrastructure.Mongo;
using Catalog.Api.Infrastructure.Persistence;
using Catalog.Api.Infrastructure.Search;
using Catalog.Api.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Catalog.Api.Tests.Search;

/// <summary>
/// Sincronizacao do indice. Os testes passam o documento expandido pronto, entao o
/// MongoDB nunca e consultado (o cliente do driver so conecta no primeiro uso).
/// </summary>
public sealed class GameSearchIndexerTests : IDisposable
{
    private readonly SqliteConnection _conexao = new("Data Source=:memory:");
    private readonly CatalogDbContext _db;
    private readonly CatalogMongoContext _mongo;
    private readonly IGameSearchIndex _index = Substitute.For<IGameSearchIndex>();

    public GameSearchIndexerTests()
    {
        _conexao.Open();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(_conexao).Options);
        _mongo = new CatalogMongoContext(
            new MongoClient("mongodb://localhost:27999"),
            Options.Create(new MongoOptions()));
    }

    private GameSearchIndexer Criar(bool habilitado = true) => new(
        _index,
        Options.Create(new ElasticsearchOptions { Enabled = habilitado }),
        _db,
        _mongo,
        TestSupport.Metrics(),
        NullLogger<GameSearchIndexer>.Instance);

    private static GameDetailsDocument Detalhes(Jogo jogo) => new()
    {
        GameId = jogo.Id.ToString(),
        Descricao = "RPG de acao em mundo aberto",
        Desenvolvedora = "FromSoftware",
        Tags = ["souls-like", "rpg"],
        Plataformas = ["PC", "PS5"],
        Midias = new GameMedia { CapaUrl = "https://cdn/capa.jpg" }
    };

    [Fact]
    public async Task IndexarAsync_envia_dados_do_SQLite_combinados_com_o_documento_do_Mongo()
    {
        var jogo = new Jogo("Elden Ring", "RPG de acao", 249.90m);

        await Criar().IndexarAsync(jogo, Detalhes(jogo));

        await _index.Received(1).IndexAsync(
            Arg.Is<GameSearchDocument>(d =>
                d.Id == jogo.Id.ToString() &&
                d.Titulo == "Elden Ring" &&
                d.Preco == 249.90m &&
                d.Ativo &&
                d.Desenvolvedora == "FromSoftware" &&
                d.Tags.Contains("souls-like") &&
                d.CapaUrl == "https://cdn/capa.jpg"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IndexarAsync_jogo_inativado_vai_para_o_indice_com_ativo_false()
    {
        var jogo = new Jogo("Elden Ring", "RPG", 100m);
        jogo.Inativar();

        await Criar().IndexarAsync(jogo, Detalhes(jogo));

        await _index.Received(1).IndexAsync(Arg.Is<GameSearchDocument>(d => !d.Ativo), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IndexarAsync_nao_propaga_falha_do_Elasticsearch()
    {
        var jogo = new Jogo("Elden Ring", "RPG", 100m);
        _index.IndexAsync(Arg.Any<GameSearchDocument>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SearchUnavailableException("fora do ar"));

        // A escrita no banco principal ja aconteceu; a indexacao falhar nao pode virar erro 500.
        var erro = await Record.ExceptionAsync(() => Criar().IndexarAsync(jogo, Detalhes(jogo)));

        Assert.Null(erro);
    }

    [Fact]
    public async Task IndexarAsync_com_Elasticsearch_desabilitado_nao_faz_nada()
    {
        var jogo = new Jogo("Elden Ring", "RPG", 100m);

        await Criar(habilitado: false).IndexarAsync(jogo, Detalhes(jogo));

        await _index.DidNotReceiveWithAnyArgs().IndexAsync(default!, default);
    }

    [Fact]
    public async Task ReindexarTudoAsync_recusa_quando_desabilitado()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(habilitado: false).ReindexarTudoAsync());
    }

    public void Dispose()
    {
        _db.Dispose();
        _conexao.Dispose();
    }
}
