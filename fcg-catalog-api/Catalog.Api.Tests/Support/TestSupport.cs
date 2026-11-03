using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Catalog.Api.Infrastructure.Caching;
using Catalog.Api.Observability;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Catalog.Api.Tests.Support;

public static class TestSupport
{
    private static readonly ServiceProvider Servicos = new ServiceCollection().AddMetrics().BuildServiceProvider();

    public static CatalogMetrics Metrics() => new(Servicos.GetRequiredService<IMeterFactory>());

    public static IDistributedCache MemoryCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    public static CatalogCache Cache(IDistributedCache? cache = null, CacheOptions? options = null) =>
        new(cache ?? MemoryCache(), Metrics(), Options.Create(options ?? new CacheOptions()), NullLogger<CatalogCache>.Instance);
}

/// <summary>
/// Handler HTTP falso: registra cada requisicao recebida e responde com o que o teste
/// programou, sem abrir conexao de rede.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _respostas = new();

    public List<(HttpMethod Method, string Path, string? Body, string? ContentType, string? Auth)> Requisicoes { get; } = [];

    public FakeHttpHandler Responder(HttpStatusCode status, string body = "{}")
    {
        _respostas.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        return this;
    }

    public FakeHttpHandler Falhar(Exception erro)
    {
        _respostas.Enqueue(_ => throw erro);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requisicoes.Add((
            request.Method,
            request.RequestUri!.PathAndQuery,
            body,
            request.Content?.Headers.ContentType?.MediaType,
            request.Headers.Authorization?.ToString()));

        if (_respostas.Count == 0)
            throw new InvalidOperationException($"Requisicao inesperada: {request.Method} {request.RequestUri}");

        return _respostas.Dequeue()(request);
    }
}
