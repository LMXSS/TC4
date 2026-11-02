using Catalog.Api.Infrastructure.Caching;
using Catalog.Api.Tests.Support;
using Microsoft.Extensions.Caching.Distributed;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Catalog.Api.Tests;

public class CatalogCacheTests
{
    [Fact]
    public async Task GetOrCreateAsync_executa_a_origem_so_no_primeiro_acesso()
    {
        var cache = TestSupport.Cache();
        var chamadas = 0;

        Task<string> Origem(CancellationToken _)
        {
            chamadas++;
            return Task.FromResult("valor");
        }

        var primeira = await cache.GetOrCreateAsync("chave", "teste", TimeSpan.FromMinutes(1), Origem);
        var segunda = await cache.GetOrCreateAsync("chave", "teste", TimeSpan.FromMinutes(1), Origem);

        Assert.Equal("valor", primeira);
        Assert.Equal("valor", segunda);
        Assert.Equal(1, chamadas);
    }

    [Fact]
    public async Task Falha_do_Redis_vira_miss_e_a_origem_responde()
    {
        var redisFora = Substitute.For<IDistributedCache>();
        redisFora.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("redis"));
        redisFora.SetAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(new TimeoutException("redis"));
        var cache = TestSupport.Cache(redisFora);

        var valor = await cache.GetOrCreateAsync("chave", "teste", TimeSpan.FromMinutes(1), _ => Task.FromResult(42));

        Assert.Equal(42, valor);
    }

    [Fact]
    public async Task BumpVersionAsync_troca_o_token_e_invalida_as_chaves_do_escopo()
    {
        var cache = TestSupport.Cache();

        var antes = await cache.GetVersionAsync("search");
        Assert.Equal(antes, await cache.GetVersionAsync("search"));

        await cache.BumpVersionAsync("search");
        var depois = await cache.GetVersionAsync("search");

        Assert.NotEqual(antes, depois);
        Assert.NotEqual(CatalogCache.SearchKey(antes, "abc"), CatalogCache.SearchKey(depois, "abc"));
    }

    [Fact]
    public async Task RemoveAsync_apaga_a_chave()
    {
        var cache = TestSupport.Cache();
        await cache.SetAsync("chave", "valor", TimeSpan.FromMinutes(1));

        await cache.RemoveAsync("chave");

        Assert.False((await cache.TryGetAsync<string>("chave")).Found);
    }

    [Fact]
    public void Chaves_logicas_sao_estaveis()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal("games:list", CatalogCache.GamesListKey());
        Assert.Equal("game:11111111222233334444555555555555:details", CatalogCache.GameDetailsKey(id));
        Assert.Equal("game:11111111222233334444555555555555:reviews:v1:p2:s10", CatalogCache.ReviewsPageKey(id, "v1", 2, 10));
    }
}
