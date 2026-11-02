using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Catalog.Api.Contracts;
using Microsoft.Extensions.Options;

namespace Catalog.Api.Infrastructure.Search;

/// <summary>
/// Cliente do Elasticsearch sobre a API REST, via <see cref="HttpClient"/> tipado.
///
/// A API REST (indices, _doc, _bulk, _search, _count) e a mesma no Elasticsearch 8.x e no
/// OpenSearch 2.x, entao o servico funciona com Elastic Cloud, Amazon OpenSearch ou uma
/// instancia propria no cluster, sem trocar codigo.
/// </summary>
public class ElasticsearchGameIndex : IGameSearchIndex
{
    public const string Motor = "elasticsearch";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ElasticsearchOptions _options;
    private readonly ILogger<ElasticsearchGameIndex> _logger;

    public ElasticsearchGameIndex(
        HttpClient http,
        IOptions<ElasticsearchOptions> options,
        ILogger<ElasticsearchGameIndex> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Aplica URL base, timeout e credenciais vindas da configuracao.</summary>
    public static void Configure(HttpClient http, ElasticsearchOptions options)
    {
        http.BaseAddress = new Uri(options.Url.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", options.ApiKey);
        }
        else if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }
    }

    private string Index => Uri.EscapeDataString(_options.IndexName);

    public async Task EnsureIndexAsync(CancellationToken cancellationToken = default)
    {
        using var head = await SendAsync(new HttpRequestMessage(HttpMethod.Head, Index), cancellationToken);
        if (head.IsSuccessStatusCode)
            return;
        if (head.StatusCode != HttpStatusCode.NotFound)
            throw new SearchUnavailableException($"Elasticsearch respondeu {(int)head.StatusCode} ao verificar o indice.");

        using var create = await SendAsync(
            new HttpRequestMessage(HttpMethod.Put, Index) { Content = CorpoJson(ElasticQueryBuilder.IndexDefinition()) },
            cancellationToken);

        if (create.IsSuccessStatusCode)
        {
            _logger.LogInformation("Indice {Index} criado no Elasticsearch.", _options.IndexName);
            return;
        }

        // Duas replicas subindo juntas: a segunda recebe resource_already_exists — tudo certo.
        var body = await create.Content.ReadAsStringAsync(cancellationToken);
        if (create.StatusCode == HttpStatusCode.BadRequest && body.Contains("resource_already_exists_exception"))
            return;

        throw new SearchUnavailableException($"Falha ao criar o indice {_options.IndexName}: {(int)create.StatusCode} {body}");
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{Index}/_count"), cancellationToken);
        await EnsureSuccessAsync(response, "contar documentos", cancellationToken);

        var json = await ReadJsonAsync(response, cancellationToken);
        return json?["count"]?.GetValue<long>() ?? 0;
    }

    public async Task IndexAsync(GameSearchDocument document, CancellationToken cancellationToken = default)
    {
        // refresh=wait_for: a resposta so volta quando o documento ja aparece na busca,
        // entao quem cadastra um jogo encontra o jogo na consulta seguinte.
        var path = $"{Index}/_doc/{Uri.EscapeDataString(document.Id)}?refresh=wait_for";
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Put, path)
            {
                Content = new StringContent(JsonSerializer.Serialize(document, Json), Encoding.UTF8, "application/json")
            },
            cancellationToken);

        await EnsureSuccessAsync(response, $"indexar o jogo {document.Id}", cancellationToken);
    }

    public async Task<BulkIndexResult> BulkIndexAsync(
        IReadOnlyCollection<GameSearchDocument> documents,
        CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
            return new BulkIndexResult(0, 0);

        var ndjson = new StringBuilder();
        foreach (var doc in documents)
        {
            var action = new JsonObject
            {
                ["index"] = new JsonObject { ["_index"] = _options.IndexName, ["_id"] = doc.Id }
            };
            ndjson.Append(action.ToJsonString()).Append('\n');
            ndjson.Append(JsonSerializer.Serialize(doc, Json)).Append('\n');
        }

        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "_bulk?refresh=true")
            {
                Content = new StringContent(ndjson.ToString(), Encoding.UTF8, "application/x-ndjson")
            },
            cancellationToken);

        await EnsureSuccessAsync(response, "indexar em lote", cancellationToken);

        var json = await ReadJsonAsync(response, cancellationToken);
        var falhas = json?["items"]?.AsArray()
            .Count(item => item?["index"]?["error"] is not null) ?? 0;

        return new BulkIndexResult(documents.Count - falhas, falhas);
    }

    public async Task<GameSearchResponse> SearchAsync(GameSearchQuery query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{Index}/_search")
            {
                Content = CorpoJson(ElasticQueryBuilder.SearchBody(query))
            },
            cancellationToken);

        await EnsureSuccessAsync(response, "buscar", cancellationToken);

        var json = await ReadJsonAsync(response, cancellationToken)
            ?? throw new SearchUnavailableException("Resposta vazia do Elasticsearch.");

        return ParseSearchResponse(json, query, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Converte a resposta do <c>_search</c> no contrato da API.</summary>
    public static GameSearchResponse ParseSearchResponse(JsonNode json, GameSearchQuery query, long tempoMs)
    {
        var hits = json["hits"];
        var total = hits?["total"]?["value"]?.GetValue<long>() ?? 0;

        var itens = new List<GameSearchHit>();
        foreach (var hit in hits?["hits"]?.AsArray() ?? new JsonArray())
        {
            if (hit is null)
                continue;

            var source = hit["_source"]?.Deserialize<GameSearchDocument>(Json);
            if (source is null || !Guid.TryParse(source.Id, out var id))
                continue;

            double? relevancia = hit["_score"] is JsonValue score && score.TryGetValue<double>(out var valor)
                ? Math.Round(valor, 4)
                : null;

            var destaques = new Dictionary<string, IReadOnlyList<string>>();
            if (hit["highlight"] is JsonObject highlight)
            {
                foreach (var (campo, fragmentos) in highlight)
                {
                    destaques[campo] = fragmentos?.AsArray()
                        .Select(f => f?.GetValue<string>() ?? string.Empty)
                        .Where(f => f.Length > 0)
                        .ToList() ?? [];
                }
            }

            itens.Add(new GameSearchHit(
                id,
                source.Titulo,
                source.Genero,
                source.Preco,
                source.Tags,
                source.Plataformas,
                source.CapaUrl,
                relevancia,
                destaques));
        }

        return new GameSearchResponse(itens, query.Pagina, query.Tamanho, total, Motor, tempoMs);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SearchUnavailableException($"Elasticsearch inacessivel em {_options.Url}.", ex);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operacao, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 500)
            body = body[..500];
        throw new SearchUnavailableException($"Elasticsearch falhou ao {operacao}: {(int)response.StatusCode} {body}");
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static StringContent CorpoJson(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
