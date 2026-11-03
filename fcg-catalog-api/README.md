# FCG Catalog API

Catálogo de jogos, biblioteca do usuário e início do fluxo de compra da **FIAP Cloud Games**.

Na Fase 3 este serviço ganhou **persistência poliglota** (MongoDB para o catálogo expandido e as avaliações), **cache distribuído** (Redis) e **instrumentação de métricas** no formato Prometheus.

Na **Fase 4** ganhou **busca avançada com Elasticsearch** (fuzzy e relevância), **PostgreSQL** como banco relacional em produção, **testes unitários** e **pipeline de CI/CD** com deploy no AKS.

## Responsabilidades

| Área | O quê |
|---|---|
| Catálogo relacional (PostgreSQL em produção, SQLite local) | CRUD de jogos, biblioteca do usuário — fonte da verdade transacional |
| Busca avançada (Elasticsearch) | Índice `fcg-games`, sincronizado a cada escrita; busca fuzzy ordenada por relevância |
| Catálogo expandido (MongoDB) | Descrição, mídias, tags, plataformas e requisitos por perfil — esquema flexível |
| Avaliações (MongoDB) | Notas e comentários, com selo de compra verificada e resumo agregado |
| Cache (Redis) | Listagem, documento expandido, páginas de avaliações e resultados de busca |
| Mensageria | Publica `OrderPlacedEvent`; consome `PaymentProcessedEvent` |
| Observabilidade | Expõe `/metrics` (OpenTelemetry) |

## Endpoints

### Catálogo relacional

| Método | Rota | Auth |
|---|---|---|
| `GET` | `/api/games` | — *(cache)* |
| `POST` | `/api/games` | Admin |
| `PUT` | `/api/games/{id}` | Admin |
| `DELETE` | `/api/games/{id}` | Admin |
| `POST` | `/api/games/{id}/reactivate` | Admin |

### Catálogo expandido (MongoDB)

| Método | Rota | Auth |
|---|---|---|
| `GET` | `/api/games/{id}/details` | — *(cache)* |
| `PUT` | `/api/games/{id}/details` | Admin |

### Busca (Elasticsearch)

| Método | Rota | Auth |
|---|---|---|
| `GET` | `/api/games/search?q=&genero=&tag=&precoMaximo=&pagina=&tamanho=` | — *(cache)* |
| `GET` | `/api/games/search/basic?q=...` — busca simples no MongoDB, para comparação | — |
| `POST` | `/api/games/search/reindex` | Admin |

### Operação do deploy

| Método | Rota | Auth |
|---|---|---|
| `GET` | `/api/games/info` — versão do código (`InfoController.Versao`) e nome do pod; usado para acompanhar o rolling update | — |

### Avaliações (MongoDB)

| Método | Rota | Auth |
|---|---|---|
| `GET` | `/api/games/{id}/reviews` | — *(cache)* |
| `GET` | `/api/games/{id}/reviews/summary` | — *(cache)* |
| `POST` | `/api/games/{id}/reviews` | ✅ |
| `DELETE` | `/api/games/{id}/reviews/me` | ✅ |

### Biblioteca

| Método | Rota | Auth |
|---|---|---|
| `GET` | `/api/users/me/library` | ✅ |
| `POST` | `/api/users/me/library/games/{gameId}` | ✅ — responde **202** |

### Operação

| Rota | Descrição |
|---|---|
| `/health/live` | Processo de pé |
| `/health/ready` | Banco relacional **e** MongoDB respondendo |
| `/metrics` | Métricas no formato Prometheus |

## Busca avançada (Elasticsearch)

O motor de busca é independente da consulta ao banco relacional: o `GET /api/games/search` nunca toca o PostgreSQL.

### Exemplo

```http
GET /api/games/search?q=eldn rign&precoMaximo=300
```

```json
{
  "itens": [
    {
      "id": "6f1c...",
      "titulo": "Elden Ring",
      "genero": "RPG de acao",
      "preco": 249.9,
      "tags": ["souls-like", "mundo-aberto", "rpg"],
      "plataformas": ["pc", "ps5"],
      "capaUrl": "https://cdn.fcg.local/elden/capa.jpg",
      "relevancia": 3.4127,
      "destaques": { "titulo": ["<em>Elden</em> <em>Ring</em>"] }
    }
  ],
  "pagina": 1, "tamanho": 20, "total": 1, "totalPaginas": 1,
  "motor": "elasticsearch",
  "tempoMs": 14,
  "doCache": false
}
```

### Como a consulta é montada

[`ElasticQueryBuilder.SearchBody`](Catalog.Api/Infrastructure/Search/ElasticQueryBuilder.cs):

| Cláusula | Papel |
|---|---|
| `multi_match` com `fuzziness: AUTO` em `titulo^4`, `tags^2`, `genero^1.5`, `desenvolvedora^1.5`, `publicadora`, `plataformas`, `descricao` | **Tolerância a erro de digitação**: 1 edição em termos de 3–5 letras, 2 acima disso |
| `match_phrase` no título (boost 6) | A frase exata sobe para o topo |
| `match_phrase_prefix` no título (boost 3) | Busca enquanto o usuário digita (`eld` → *Elden Ring*) |
| `filter`: `ativo`, `genero.keyword`, `tags.keyword`, `range preco` | Restringem sem alterar a nota (e o Elasticsearch cacheia) |
| `sort`: `_score desc`, `titulo.keyword asc` | **Ordenação por relevância**, desempate alfabético |
| `highlight` | Trechos que casaram, em `destaques` |

O mapeamento do índice usa um analisador com `lowercase` + `asciifolding`, então "acao", "Ação" e "ação" são o mesmo termo.

### Sincronização com o banco principal

```
POST/PUT/DELETE /api/games  ──▶ PostgreSQL ──▶ MongoDB (campos denormalizados) ──▶ Elasticsearch (PUT _doc, refresh=wait_for)
PUT /api/games/{id}/details ──────────────────▶ MongoDB ─────────────────────────▶ Elasticsearch
```

* [`GameSearchIndexer`](Catalog.Api/Infrastructure/Search/GameSearchIndexer.cs) monta o documento juntando o jogo relacional com o documento expandido do MongoDB;
* `refresh=wait_for` faz a resposta do cadastro só voltar quando o jogo já aparece na busca;
* jogo inativado continua no índice com `ativo: false` e é filtrado na consulta;
* falha do Elasticsearch **não desfaz** a escrita no banco principal: vai para o log e para a métrica `fcg_catalog_search_index_operations_total{result="fail"}`;
* [`SearchIndexInitializer`](Catalog.Api/Infrastructure/Search/SearchIndexInitializer.cs) cria o índice no start (tentando de novo enquanto o cluster sobe) e, se ele estiver vazio, reindexa o catálogo inteiro via `_bulk`. `POST /api/games/search/reindex` faz o mesmo sob demanda.

### Cache e degradação

[`GameSearchService`](Catalog.Api/Infrastructure/Search/GameSearchService.cs):

1. procura o resultado no Redis (chave com o token de versão das buscas) — se achar, responde com `doCache: true`;
2. senão consulta o Elasticsearch e grava o resultado no Redis;
3. se o Elasticsearch estiver fora, responde pela busca de texto do MongoDB com `motor: "mongodb"` — e **não** cacheia esse resultado, para voltar ao Elasticsearch assim que ele se recuperar.

### Por que HTTP puro e não o client oficial

O serviço fala com o Elasticsearch pela API REST (`HttpClient` tipado). Os endpoints usados (`_doc`, `_bulk`, `_search`, `_count`) são os mesmos no Elasticsearch 8, no Elastic Cloud e no Amazon OpenSearch, então trocar de provedor é só mudar `Elasticsearch__Url` e a credencial. Também deixa o cliente testável com um `HttpMessageHandler` falso.

## Como o MongoDB entra na arquitetura

O banco relacional (PostgreSQL em produção) continua guardando o jogo e o vínculo com a biblioteca. O MongoDB guarda o que mudaria o schema relacional a cada jogo novo:

| Coleção | Conteúdo | Índices |
|---|---|---|
| `game_details` | Documento expandido do jogo | Texto (título, descrição, tags) · tags · (ativo, gênero) |
| `game_reviews` | Avaliações dos usuários | Único (gameId, usuarioId) · (gameId, criadoEm desc) |

Título, gênero, preço e situação ficam **denormalizados** no documento, de modo que a busca roda inteiramente no Mongo, sem join entre bancos. Toda escrita no catálogo relacional sincroniza esses campos (`GameDetailsService.SyncFromRelationalAsync`).

O resumo das avaliações — total, nota média e distribuição — sai de um pipeline de agregação.

## Como o cache funciona

`IDistributedCache` sobre Redis, encapsulado em `CatalogCache`:

| Chave | TTL padrão |
|---|---|
| `games:list` | 60 s |
| `game:{id}:details` | 300 s |
| `game:{id}:reviews:{versão}:p{n}:s{n}` | 120 s |
| `game:{id}:reviews:{versão}:summary` | 120 s |
| `search:{versão}:{hash}` | 90 s — resultados do Elasticsearch |

**Invalidação por token de versão.** `IDistributedCache` não remove por prefixo, então cada escopo carrega um token que entra na composição da chave; gravar uma avaliação troca o token e derruba todas as páginas daquele jogo de uma vez.

**Degradação controlada.** Falha do Redis vira *miss* registrado em log — a requisição segue para a origem e responde normalmente.

## Variáveis de ambiente

Nenhuma credencial tem valor padrão no código: em produção chegam do Secret `catalog-api-secret`; localmente, do `.env` do compose ou de `dotnet user-secrets`.

| Variável | Padrão | Descrição |
|---|---|---|
| `Database__Provider` | `Sqlite` | `Postgres` em produção |
| `ConnectionStrings__DefaultConnection` | `Data Source=catalog.db` | Banco relacional |
| `Jwt__Key` | — *(obrigatória)* | **Mesmo valor da Users API e do Kong** |
| `Jwt__Issuer` / `Jwt__Audience` | `FCG` / `FCG` | |
| `RabbitMq__Host` | `localhost` | Broker |
| `RabbitMq__Username` / `__Password` | — | Sem elas, usa o padrão do broker local |
| `Mongo__ConnectionString` | `mongodb://localhost:27017` | MongoDB (com usuário e senha em produção) |
| `Mongo__Database` | `fcg_catalog` | Base |
| `Mongo__GameDetailsCollection` | `game_details` | Coleção do catálogo expandido |
| `Mongo__ReviewsCollection` | `game_reviews` | Coleção de avaliações |
| `Cache__RedisEnabled` | `true` | `false` usa cache em memória, sem Redis |
| `Cache__RedisConnectionString` | `localhost:6379` | Redis (`,password=...` em produção) |
| `Cache__InstanceName` | `fcg:catalog:` | Prefixo das chaves |
| `Cache__GamesListSeconds` / `__GameDetailsSeconds` / `__ReviewsSeconds` / `__SearchSeconds` | `60` / `300` / `120` / `90` | TTLs |
| `Elasticsearch__Enabled` | `true` | `false` faz a busca usar só o MongoDB |
| `Elasticsearch__Url` | `http://localhost:9200` | Cluster de busca |
| `Elasticsearch__IndexName` | `fcg-games` | Índice |
| `Elasticsearch__Username` / `__Password` | — | Autenticação básica |
| `Elasticsearch__ApiKey` | — | Alternativa para Elastic Cloud (tem precedência) |
| `Elasticsearch__ReindexOnStartupIfEmpty` | `true` | Reindexa no start se o índice estiver vazio |

## Métricas expostas

Além de `http_server_request_duration_seconds` (latência, throughput e status code) e das métricas de runtime .NET:

| Métrica | Labels |
|---|---|
| `fcg_catalog_cache_requests_total` | `result` (hit/miss), `cache` |
| `fcg_catalog_purchases_started_total` | — |
| `fcg_catalog_purchases_confirmed_total` | — |
| `fcg_catalog_reviews_total` | — |
| `fcg_catalog_mongo_operation_duration_seconds` | `operation` |
| `fcg_catalog_search_requests_total` | `engine` (elasticsearch/mongodb) |
| `fcg_catalog_search_duration_seconds` | `engine` |
| `fcg_catalog_search_index_operations_total` | `operation` (index/bulk), `result` (ok/fail) |

## Executando

### Stack completa (recomendado)

Sobe este serviço junto com gateway, MongoDB, Redis, RabbitMQ e observabilidade:

```powershell
cd ..\fcg-platform
docker compose up -d --build
```

Acesso pelo gateway: <http://localhost:8000/api/games>.

### Somente este serviço

```powershell
# Dependências
docker run -d --name fcg-rabbit -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management-alpine
docker run -d --name fcg-mongo  -p 27017:27017 mongo:7.0
docker run -d --name fcg-redis  -p 6379:6379  redis:7.4-alpine
docker run -d --name fcg-elastic -p 9200:9200 -e discovery.type=single-node -e xpack.security.enabled=false `
  -e ES_JAVA_OPTS="-Xms512m -Xmx512m" docker.elastic.co/elasticsearch/elasticsearch:8.15.3

cd Catalog.Api
dotnet user-secrets set "Jwt:Key" "<a mesma chave da Users API, 32+ caracteres>"
dotnet run
```

Swagger em <http://localhost:5102/swagger>. Use **Authorize** com o token emitido pela Users API.

### Docker

```powershell
# contexto = raiz do repositório
docker build -t fcg/catalog-api:latest . -f Catalog.Api/Dockerfile
```

## Testes

```powershell
dotnet test
```

[`Catalog.Api.Tests`](Catalog.Api.Tests) (xUnit + NSubstitute), sem MongoDB, Redis nem Elasticsearch de verdade:

| Classe | O que garante |
|---|---|
| `ElasticQueryBuilderTests` | Fuzzy `AUTO`, pesos dos campos, frase exata e prefixo, filtros em `filter`, ordenação por relevância, paginação, mapeamento com `asciifolding` |
| `ElasticsearchGameIndexTests` | Chamadas HTTP certas (`PUT _doc?refresh=wait_for`, `_bulk` em NDJSON, criação idempotente do índice), conversão de hits/relevância/destaques, erros viram `SearchUnavailableException`, autenticação por API key ou usuário/senha |
| `GameSearchServiceTests` | Resposta pelo Elasticsearch, segunda busca pelo Redis, fallback para o MongoDB sem cachear o resultado degradado |
| `GameSearchIndexerTests` | Documento indexado junta PostgreSQL + MongoDB; falha do Elasticsearch não quebra a escrita |
| `CatalogCacheTests` | Hit/miss, Redis fora vira miss, invalidação por token de versão |
| `JogoTests`, `GameSearchModelTests` | Regras de domínio e normalização da consulta |

## CI/CD

[`.github/workflows/catalog-api.yml`](../.github/workflows/catalog-api.yml): **Build & Test → imagem `<run>-<sha7>` → Trivy → push no ACR → `kubectl apply -k k8s/` com rolling update → rollback automático se o rollout falhar.** Detalhes no [README do fcg-platform](../fcg-platform/README.md#cicd-com-github-actions).

## Kubernetes

Manifestos em [`k8s/`](k8s) com Kustomize: `Deployment` (rolling update `maxSurge 1` / `maxUnavailable 0`, `preStop`), `Service`, `ConfigMap`, `HorizontalPodAutoscaler` e `kustomization.yaml`.

```powershell
kubectl apply -k k8s/
```

Não há `Secret` neste repositório: o `catalog-api-secret` é criado pela pipeline do `fcg-platform` a partir dos GitHub Secrets.

## Serviços relacionados (pastas deste repositório)

| Repositório | Papel |
|---|---|
| [`fcg-platform`](../fcg-platform/) | Orquestração — comece por aqui |
| [`fcg-users-api`](../fcg-users-api/) | Emite o JWT que este serviço valida |
| [`fcg-payments-api`](../fcg-payments-api/) | Processa o pagamento do pedido |
| [`fcg-notifications-function`](../fcg-notifications-function/) | Notifica o usuário por e-mail |
