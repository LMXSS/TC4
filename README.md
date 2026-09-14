# FCG Catalog API

Catálogo de jogos, biblioteca do usuário e início do fluxo de compra da **FIAP Cloud Games**.

Na Fase 3 este serviço ganhou **persistência poliglota** (MongoDB para o catálogo expandido e as avaliações), **cache distribuído** (Redis) e **instrumentação de métricas** no formato Prometheus.

## Responsabilidades

| Área | O quê |
|---|---|
| Catálogo relacional (SQLite) | CRUD de jogos, biblioteca do usuário — fonte da verdade transacional |
| Catálogo expandido (MongoDB) | Descrição, mídias, tags, plataformas e requisitos por perfil — esquema flexível |
| Avaliações (MongoDB) | Notas e comentários, com selo de compra verificada e resumo agregado |
| Cache (Redis) | Listagem, documento expandido, páginas de avaliações e busca |
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
| `GET` | `/api/games/search?q=&tag=&genero=&precoMaximo=&pagina=&tamanho=` | — *(cache)* |

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
| `/health/ready` | SQLite **e** MongoDB respondendo |
| `/metrics` | Métricas no formato Prometheus |

## Como o MongoDB entra na arquitetura

O SQLite continua guardando o jogo e o vínculo com a biblioteca. O MongoDB guarda o que mudaria o schema relacional a cada jogo novo:

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
| `search:{versão}:{hash}` | 90 s |

**Invalidação por token de versão.** `IDistributedCache` não remove por prefixo, então cada escopo carrega um token que entra na composição da chave; gravar uma avaliação troca o token e derruba todas as páginas daquele jogo de uma vez.

**Degradação controlada.** Falha do Redis vira *miss* registrado em log — a requisição segue para a origem e responde normalmente.

## Variáveis de ambiente

| Variável | Padrão | Descrição |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `Data Source=catalog.db` | SQLite do catálogo e da biblioteca |
| `Jwt__Key` / `Jwt__Issuer` / `Jwt__Audience` | — | **Mesmos valores da Users API** |
| `RabbitMq__Host` / `__Username` / `__Password` | `localhost` / `guest` / `guest` | Broker |
| `Mongo__ConnectionString` | `mongodb://localhost:27017` | MongoDB |
| `Mongo__Database` | `fcg_catalog` | Base |
| `Mongo__GameDetailsCollection` | `game_details` | Coleção do catálogo expandido |
| `Mongo__ReviewsCollection` | `game_reviews` | Coleção de avaliações |
| `Cache__RedisEnabled` | `true` | `false` usa cache em memória, sem Redis |
| `Cache__RedisConnectionString` | `localhost:6379` | Redis |
| `Cache__InstanceName` | `fcg:catalog:` | Prefixo das chaves |
| `Cache__GamesListSeconds` | `60` | TTL da listagem |
| `Cache__GameDetailsSeconds` | `300` | TTL do documento expandido |
| `Cache__ReviewsSeconds` | `120` | TTL das avaliações |
| `Cache__SearchSeconds` | `90` | TTL da busca |

## Métricas expostas

Além de `http_server_request_duration_seconds` (latência, throughput e status code) e das métricas de runtime .NET:

| Métrica | Labels |
|---|---|
| `fcg_catalog_cache_requests_total` | `result` (hit/miss), `cache` |
| `fcg_catalog_purchases_started_total` | — |
| `fcg_catalog_purchases_confirmed_total` | — |
| `fcg_catalog_reviews_total` | — |
| `fcg_catalog_mongo_operation_duration_seconds` | `operation` |

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

cd Catalog.Api
dotnet run
```

Swagger em <http://localhost:5102/swagger>. Use **Authorize** com o token emitido pela Users API.

### Docker

```powershell
# contexto = raiz do repositório
docker build -t fcg/catalog-api:latest . -f Catalog.Api/Dockerfile
```

## Kubernetes

Manifestos em [`k8s/`](k8s): `Deployment`, `Service`, `ConfigMap` e `Secret`.

```powershell
kubectl apply -f k8s/
```

> A `jwt-key` do Secret precisa ser idêntica à da Users API e à do Kong.

O Deployment traz as anotações `prometheus.io/scrape` para entrar na coleta automática, e probes de `readiness`/`liveness` apontando para `/health/ready` e `/health/live`.

## Repositórios relacionados

| Repositório | Papel |
|---|---|
| [`fcg-platform`](https://github.com/VHMoschini/fcg-platform) | Orquestração — comece por aqui |
| [`fcg-users-api`](https://github.com/VHMoschini/fcg-users-api) | Emite o JWT que este serviço valida |
| [`fcg-payments-api`](https://github.com/VHMoschini/fcg-payments-api) | Processa o pagamento do pedido |
| [`fcg-notifications-function`](https://github.com/VHMoschini/fcg-notifications-function) | Notifica o usuário por e-mail |
