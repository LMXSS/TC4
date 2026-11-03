using System.Text.Json.Nodes;
using Catalog.Api.Contracts;

namespace Catalog.Api.Infrastructure.Search;

/// <summary>
/// Monta o corpo JSON das chamadas ao Elasticsearch. Fica separado do cliente HTTP para
/// que a regra de busca (campos, pesos, fuzzy, filtros, ordenacao) seja testada sem rede.
/// </summary>
public static class ElasticQueryBuilder
{
    /// <summary>
    /// Campos pesquisados e seus pesos. O titulo pesa mais que tudo; tags e estudio vem
    /// em seguida; a descricao, por ser longa, entra com peso base.
    /// </summary>
    public static readonly string[] CamposPesquisados =
    [
        "titulo^4",
        "tags^2",
        "genero^1.5",
        "desenvolvedora^1.5",
        "publicadora",
        "plataformas",
        "descricao"
    ];

    /// <summary>
    /// Settings e mapeamento do indice.
    ///
    /// O analisador <c>fcg_text</c> aplica lowercase e asciifolding: "Acao", "acao" e "ação"
    /// viram o mesmo token, o que importa para um catalogo em portugues digitado sem acento.
    /// Os subcampos <c>.keyword</c> (com normalizer equivalente) servem para filtro exato e
    /// ordenacao.
    /// </summary>
    public static JsonObject IndexDefinition() => new()
    {
        ["settings"] = new JsonObject
        {
            ["number_of_shards"] = 1,
            ["number_of_replicas"] = 0,
            ["analysis"] = new JsonObject
            {
                ["analyzer"] = new JsonObject
                {
                    ["fcg_text"] = new JsonObject
                    {
                        ["type"] = "custom",
                        ["tokenizer"] = "standard",
                        ["filter"] = new JsonArray("lowercase", "asciifolding")
                    }
                },
                ["normalizer"] = new JsonObject
                {
                    ["fcg_lower"] = new JsonObject
                    {
                        ["type"] = "custom",
                        ["filter"] = new JsonArray("lowercase", "asciifolding")
                    }
                }
            }
        },
        ["mappings"] = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "keyword" },
                ["titulo"] = TextoComKeyword(),
                ["genero"] = TextoComKeyword(),
                ["preco"] = new JsonObject { ["type"] = "scaled_float", ["scaling_factor"] = 100 },
                ["ativo"] = new JsonObject { ["type"] = "boolean" },
                ["descricao"] = new JsonObject { ["type"] = "text", ["analyzer"] = "fcg_text" },
                ["desenvolvedora"] = TextoComKeyword(),
                ["publicadora"] = TextoComKeyword(),
                ["tags"] = TextoComKeyword(),
                ["plataformas"] = TextoComKeyword(),
                ["capaUrl"] = new JsonObject { ["type"] = "keyword", ["index"] = false },
                ["atualizadoEmUtc"] = new JsonObject { ["type"] = "date" }
            }
        }
    };

    /// <summary>
    /// Corpo do <c>_search</c>.
    ///
    /// Com termo, tres clausulas somam pontos (bool/should) e o resultado sai ordenado por
    /// relevancia (<c>_score</c>):
    /// 1. <c>multi_match</c> com <c>fuzziness: AUTO</c> — tolera erro de digitacao
    ///    ("eldn ring", "minecrfat"): 1 edicao para termos de 3 a 5 letras, 2 acima disso;
    /// 2. <c>match_phrase</c> no titulo — a frase exata sobe para o topo;
    /// 3. <c>match_phrase_prefix</c> no titulo — busca enquanto o usuario digita ("eld").
    ///
    /// Os filtros (ativo, genero, tag, preco) vao em <c>filter</c>: restringem sem alterar
    /// a nota e sao cacheados pelo proprio Elasticsearch.
    /// </summary>
    public static JsonObject SearchBody(GameSearchQuery query)
    {
        var filtros = new JsonArray
        {
            new JsonObject { ["term"] = new JsonObject { ["ativo"] = true } }
        };

        if (query.Genero is not null)
            filtros.Add(new JsonObject { ["term"] = new JsonObject { ["genero.keyword"] = query.Genero } });

        if (query.Tag is not null)
            filtros.Add(new JsonObject { ["term"] = new JsonObject { ["tags.keyword"] = query.Tag } });

        if (query.PrecoMaximo is not null)
            filtros.Add(new JsonObject
            {
                ["range"] = new JsonObject
                {
                    ["preco"] = new JsonObject { ["lte"] = JsonValue.Create(query.PrecoMaximo.Value) }
                }
            });

        var boolQuery = new JsonObject { ["filter"] = filtros };

        if (query.Termo is not null)
        {
            boolQuery["should"] = new JsonArray
            {
                new JsonObject
                {
                    ["multi_match"] = new JsonObject
                    {
                        ["query"] = query.Termo,
                        ["fields"] = new JsonArray(CamposPesquisados.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                        ["type"] = "best_fields",
                        ["fuzziness"] = "AUTO",
                        ["prefix_length"] = 0,
                        ["max_expansions"] = 50,
                        ["operator"] = "or"
                    }
                },
                new JsonObject
                {
                    ["match_phrase"] = new JsonObject
                    {
                        ["titulo"] = new JsonObject { ["query"] = query.Termo, ["boost"] = 6 }
                    }
                },
                new JsonObject
                {
                    ["match_phrase_prefix"] = new JsonObject
                    {
                        ["titulo"] = new JsonObject { ["query"] = query.Termo, ["boost"] = 3 }
                    }
                }
            };
            boolQuery["minimum_should_match"] = 1;
        }

        var sort = query.Termo is not null
            ? new JsonArray(
                new JsonObject { ["_score"] = new JsonObject { ["order"] = "desc" } },
                new JsonObject { ["titulo.keyword"] = new JsonObject { ["order"] = "asc" } })
            : new JsonArray(
                new JsonObject { ["titulo.keyword"] = new JsonObject { ["order"] = "asc" } });

        return new JsonObject
        {
            ["from"] = query.Deslocamento,
            ["size"] = query.Tamanho,
            ["track_total_hits"] = true,
            ["query"] = new JsonObject { ["bool"] = boolQuery },
            ["sort"] = sort,
            ["highlight"] = new JsonObject
            {
                ["pre_tags"] = new JsonArray("<em>"),
                ["post_tags"] = new JsonArray("</em>"),
                ["fields"] = new JsonObject
                {
                    ["titulo"] = new JsonObject(),
                    ["tags"] = new JsonObject(),
                    ["descricao"] = new JsonObject
                    {
                        ["fragment_size"] = 120,
                        ["number_of_fragments"] = 2
                    }
                }
            }
        };
    }

    private static JsonObject TextoComKeyword() => new()
    {
        ["type"] = "text",
        ["analyzer"] = "fcg_text",
        ["fields"] = new JsonObject
        {
            ["keyword"] = new JsonObject
            {
                ["type"] = "keyword",
                ["normalizer"] = "fcg_lower",
                ["ignore_above"] = 256
            }
        }
    };
}
