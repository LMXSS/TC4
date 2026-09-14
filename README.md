# FCG Platform — orquestração da FIAP Cloud Games

Repositório central da plataforma. Reúne o `docker-compose`, os manifestos Kubernetes, a configuração do API Gateway e a stack de observabilidade dos microsserviços da FCG.

> **Fase 3 — Microsserviços e Clean Architecture.** Esta fase adiciona API Gateway, função serverless, observabilidade, persistência poliglota (MongoDB) e cache distribuído (Redis) à arquitetura de microsserviços entregue na Fase 2.

---

## Índice

1. [Arquitetura](#arquitetura)
2. [Repositórios](#repositórios)
3. [Decisões da Fase 3](#decisões-da-fase-3)
4. [Pré-requisitos](#pré-requisitos)
5. [Subindo com Docker Compose](#subindo-com-docker-compose)
6. [Roteiro de demonstração](#roteiro-de-demonstração)
7. [Deploy no Kubernetes](#deploy-no-kubernetes)
8. [API Gateway (Kong)](#api-gateway-kong)
9. [Observabilidade (Prometheus + Grafana)](#observabilidade-prometheus--grafana)
10. [Persistência poliglota e cache](#persistência-poliglota-e-cache)
11. [Função serverless](#função-serverless)
12. [Referência de endpoints](#referência-de-endpoints)
13. [Solução de problemas](#solução-de-problemas)

---

## Arquitetura

```
                        ┌──────────────────────────────────┐
     Cliente  ─────────▶│   Kong API Gateway  :8000        │
                        │   rota · JWT · rate limit        │
                        └───────┬───────────────┬──────────┘
                                │               │
                     /api/auth  │               │  /api/games · /api/users/me
                                ▼               ▼
                        ┌───────────────┐  ┌──────────────────┐
                        │   Users API   │  │   Catalog API    │
                        │  SQLite + JWT │  │ SQLite + MongoDB │
                        └───────┬───────┘  └────┬────────┬────┘
                                │               │        │
                                │               │        ├──▶ MongoDB  (catálogo expandido, avaliações)
                                │               │        └──▶ Redis    (cache distribuído)
                                │               │
                                ▼               ▼
                        ┌──────────────────────────────────┐
                        │        RabbitMQ (MassTransit)    │
                        └───┬──────────────┬───────────────┘
                            │              │
              OrderPlaced   │              │  UserCreated / PaymentProcessed
                            ▼              ▼
                   ┌────────────────┐  ┌──────────────────────────┐
                   │  Payments API  │  │  Notifications Function  │
                   │   (simulação)  │  │  Azure Functions + SMTP  │
                   └────────────────┘  └────────────┬─────────────┘
                                                    ▼
                                                 Mailpit

     Prometheus coleta /metrics de Kong, Users, Catalog e Payments → Grafana
```

### Fluxos orientados a eventos

**Cadastro de usuário**
1. `POST /api/auth/register` na Users API cria o usuário e publica `UserCreatedEvent`.
2. A fila `notifications-user-created` aciona a **função serverless**, que envia o e-mail de boas-vindas.

**Compra de jogo**
1. `POST /api/users/me/library/games/{gameId}` na Catalog API publica `OrderPlacedEvent` e responde **202 Accepted**.
2. A Payments API consome, simula o pagamento e publica `PaymentProcessedEvent`.
3. A Catalog API consome: se aprovado, adiciona o jogo à biblioteca.
4. A fila `notifications-payment-processed` aciona a **função serverless**, que envia confirmação ou aviso de recusa.

---

## Repositórios

| Repositório | Conteúdo |
|---|---|
| [`fcg-platform`](https://github.com/VHMoschini/fcg-platform) | **Este.** Compose, Kubernetes, Kong, Prometheus/Grafana |
| [`fcg-users-api`](https://github.com/VHMoschini/fcg-users-api) | Cadastro, autenticação e emissão de JWT |
| [`fcg-catalog-api`](https://github.com/VHMoschini/fcg-catalog-api) | Catálogo, catálogo expandido, avaliações e biblioteca |
| [`fcg-payments-api`](https://github.com/VHMoschini/fcg-payments-api) | Simulação de pagamento |
| [`fcg-notifications-function`](https://github.com/VHMoschini/fcg-notifications-function) | **Novo na Fase 3.** Notificações como Azure Function |
| [`fcg-notifications-api`](https://github.com/VHMoschini/fcg-notifications-api) | *Substituído pela função serverless. Mantido como histórico.* |

Os repositórios precisam estar clonados **lado a lado**, porque o compose constrói a partir do contexto de cada um:

```
D:\TechChallenge2\
├─ fcg-platform\
├─ fcg-users-api\
├─ fcg-catalog-api\
├─ fcg-payments-api\
└─ fcg-notifications-function\
```

---

## Decisões da Fase 3

| Requisito | Escolha | Por quê |
|---|---|---|
| **API Gateway** | **Kong** em modo DB-less | Configuração declarativa em um único `kong.yml` versionado, idêntico no compose e no Kubernetes. Sem dependência de nuvem nem de banco do gateway. |
| **Serverless** | **Azure Functions** .NET 8 isolated com `RabbitMQTrigger` | Aciona direto na fila que já existe — nenhuma mudança no broker nem nos publicadores. O mesmo artefato roda em container no cluster e como Function App na Azure. |
| **Observabilidade** | **Opção A — Prometheus + Grafana** | Stack aberta, sem conta nem chave de terceiros, implantada por manifestos Kubernetes como o desafio exige. Dashboard versionado em JSON e provisionado automaticamente. |
| **NoSQL** | **MongoDB** com `MongoDB.Driver` | O catálogo expandido tem esquema que muda a cada jogo (mídias, requisitos por plataforma, atributos livres) e as avaliações têm alta volumetria — os dois casos que o modelo relacional atende mal. |
| **Cache** | **Redis** via `IDistributedCache` | Tira do caminho as consultas mais caras e mais repetidas: listagem do catálogo, documento expandido, páginas de avaliações e a agregação de notas. |

---

## Pré-requisitos

| Ferramenta | Versão | Para quê |
|---|---|---|
| Docker Desktop | Compose v2 | Subir a stack completa |
| .NET SDK | 8.0 | Compilar e depurar fora do container |
| kubectl | 1.28+ | Deploy no Kubernetes |
| Kubernetes local | Docker Desktop, Kind ou Minikube | Etapa de Kubernetes |

```powershell
docker --version
dotnet --list-sdks
kubectl version --client
```

---

## Subindo com Docker Compose

```powershell
cd fcg-platform
Copy-Item .env.example .env      # ajuste JWT_KEY se quiser
docker compose up -d --build
docker compose ps
```

A primeira execução compila cinco imagens .NET e pode levar alguns minutos.

### Endereços

| O quê | URL | Credenciais |
|---|---|---|
| **API Gateway (única porta de entrada)** | <http://localhost:8000> | — |
| Kong Admin API | <http://localhost:8001> | — |
| **Grafana** (dashboard já provisionado) | <http://localhost:3000> | `admin` / `admin` |
| **Prometheus** | <http://localhost:9090> | — |
| **Mailpit** (caixa de e-mails) | <http://localhost:8025> | — |
| RabbitMQ Management | <http://localhost:15672> | `guest` / `guest` |
| MongoDB | `mongodb://localhost:27017` | — |
| Redis | `localhost:6379` | — |

Os microsserviços **não publicam porta no host** — é justamente o ponto do gateway. Para abrir o Swagger de um deles durante a depuração:

```powershell
docker compose -f docker-compose.yml -f docker-compose.debug.yml up -d
```

| Serviço | Swagger | Métricas |
|---|---|---|
| Users API | <http://localhost:5101/swagger> | <http://localhost:5101/metrics> |
| Catalog API | <http://localhost:5102/swagger> | <http://localhost:5102/metrics> |
| Payments API | <http://localhost:5103/swagger> | <http://localhost:5103/metrics> |

### Parando

```powershell
docker compose down            # mantém os volumes
docker compose down -v         # apaga também os dados
```

---

## Roteiro de demonstração

Tudo passa pelo gateway, na porta **8000**.

### 1. Login do administrador

O ambiente de desenvolvimento cria um admin no start da Users API.

```powershell
$login = Invoke-RestMethod -Uri http://localhost:8000/api/auth/login -Method Post `
  -ContentType 'application/json' `
  -Body '{"email":"admin@fcg.local","password":"Admin@123"}'
$admin = @{ Authorization = "Bearer $($login.token)" }
```

### 2. O gateway barra requisição sem token

```powershell
# 401 vindo do Kong, sem sequer encostar na Catalog API
Invoke-WebRequest -Uri http://localhost:8000/api/games -Method Post `
  -ContentType 'application/json' -Body '{"titulo":"X","genero":"Y","preco":1}'
```

### 3. Cadastrar um jogo e enriquecer no MongoDB

```powershell
$jogo = Invoke-RestMethod -Uri http://localhost:8000/api/games -Method Post -Headers $admin `
  -ContentType 'application/json' `
  -Body '{"titulo":"Elden Ring","genero":"RPG de acao","preco":249.90}'

Invoke-RestMethod -Uri "http://localhost:8000/api/games/$($jogo.id)/details" -Method Put -Headers $admin `
  -ContentType 'application/json' -Body (@{
    descricao     = "RPG de acao em mundo aberto ambientado nas Terras Intermedias."
    desenvolvedora = "FromSoftware"
    publicadora    = "Bandai Namco"
    dataLancamento = "2022-02-25T00:00:00Z"
    tags           = @("souls-like","mundo-aberto","dificil","rpg")
    plataformas    = @("PC","PS5","Xbox Series X")
    idiomas        = @("Portugues do Brasil","Ingles","Japones")
    midias         = @{ capaUrl = "https://cdn.fcg.local/elden/capa.jpg"; trailerUrl = "https://cdn.fcg.local/elden/trailer.mp4"; screenshots = @("https://cdn.fcg.local/elden/1.jpg") }
    requisitos     = @{
      minimo      = @{ so = "Windows 10"; processador = "Intel Core i5-8400"; memoria = "12 GB"; video = "GTX 1060 3 GB" }
      recomendado = @{ so = "Windows 11"; processador = "Intel Core i7-8700K"; memoria = "16 GB"; video = "GTX 1070 8 GB" }
    }
  } | ConvertTo-Json -Depth 6)
```

### 4. Cache em ação

```powershell
# 1ª chamada: miss, busca no MongoDB.  2ª: hit, responde do Redis.
1..2 | ForEach-Object {
  Measure-Command { Invoke-RestMethod -Uri "http://localhost:8000/api/games/$($jogo.id)/details" } |
    Select-Object -ExpandProperty TotalMilliseconds
}
```

No Grafana, o painel **Acerto do cache (Redis)** reage na hora.

### 5. Busca no catálogo expandido

```powershell
Invoke-RestMethod -Uri "http://localhost:8000/api/games/search?q=mundo%20aberto&tag=rpg"
```

### 6. Cadastro de usuário e a função serverless

```powershell
$novo = Invoke-RestMethod -Uri http://localhost:8000/api/auth/register -Method Post `
  -ContentType 'application/json' `
  -Body '{"name":"Ana Souza","email":"ana@fcg.local","password":"Senha@123"}'
$ana = @{ Authorization = "Bearer $($novo.token)" }
```

O e-mail de boas-vindas aparece no **Mailpit** (<http://localhost:8025>). Os logs da invocação:

```powershell
docker compose logs -f notifications-function
```

### 7. Fluxo de compra completo

```powershell
Invoke-RestMethod -Uri "http://localhost:8000/api/users/me/library/games/$($jogo.id)" -Method Post -Headers $ana
Start-Sleep -Seconds 3
Invoke-RestMethod -Uri http://localhost:8000/api/users/me/library -Headers $ana
```

Pagamento aprovado → jogo na biblioteca **e** e-mail de confirmação no Mailpit. Para forçar uma recusa, crie um jogo com preço `99.99` (ver `Payments__RejectPrices__0` no compose).

### 8. Avaliar o jogo

```powershell
Invoke-RestMethod -Uri "http://localhost:8000/api/games/$($jogo.id)/reviews" -Method Post -Headers $ana `
  -ContentType 'application/json' `
  -Body '{"nota":5,"titulo":"Obra-prima","comentario":"Melhor jogo que ja joguei."}'

Invoke-RestMethod -Uri "http://localhost:8000/api/games/$($jogo.id)/reviews/summary"
```

Como Ana comprou o jogo, a avaliação sai com `compraVerificada: true`.

### 9. Gerar carga para o dashboard

```powershell
1..300 | ForEach-Object {
  Invoke-RestMethod -Uri http://localhost:8000/api/games | Out-Null
  Invoke-RestMethod -Uri "http://localhost:8000/api/games/$($jogo.id)/details" | Out-Null
}
```

Abra <http://localhost:3000> e acompanhe throughput, latência p95, status codes e taxa de acerto do cache.

---

## Deploy no Kubernetes

> **Atalho.** Os dois scripts deste repositório fazem os passos 1 a 3 de uma vez, encontrando sozinhos a pasta onde os repos estão clonados lado a lado:
>
> ```powershell
> .\build-images.ps1        # passo 1
> .\deploy-k8s.ps1          # passos 2 e 3
> .\deploy-k8s.ps1 -Delete  # desfaz
> ```
>
> Os passos abaixo são o que eles executam, para quem preferir aplicar à mão.

### 1. Construir as imagens

```powershell
docker build -t fcg/users-api:latest              ..\fcg-users-api            -f ..\fcg-users-api\Users.Api\Dockerfile
docker build -t fcg/catalog-api:latest            ..\fcg-catalog-api          -f ..\fcg-catalog-api\Catalog.Api\Dockerfile
docker build -t fcg/payments-api:latest           ..\fcg-payments-api         -f ..\fcg-payments-api\Payments.Api\Dockerfile
docker build -t fcg/notifications-function:latest ..\fcg-notifications-function
```

Em Kind, carregue cada imagem no cluster: `kind load docker-image fcg/users-api:latest` (repita para as demais).

### 2. Gerar os ConfigMaps a partir dos arquivos versionados

O `kong.yml` e o dashboard do Grafana são os mesmos arquivos usados pelo compose — nada é duplicado à mão:

```powershell
kubectl create configmap kong-declarative `
  --from-file=kong.yml=gateway/kong.yml `
  --dry-run=client -o yaml | kubectl apply -f -

kubectl create configmap grafana-dashboards `
  --from-file=fcg-overview.json=observability/grafana/dashboards/fcg-overview.json `
  --dry-run=client -o yaml | kubectl apply -f -
```

### 3. Aplicar os manifestos

```powershell
# Infraestrutura
kubectl apply -f k8s/rabbitmq.yaml
kubectl apply -f k8s/mongo.yaml
kubectl apply -f k8s/redis.yaml
kubectl apply -f k8s/mailpit.yaml
kubectl apply -f k8s/azurite.yaml

# Microsserviços
kubectl apply -f ..\fcg-users-api\k8s\
kubectl apply -f ..\fcg-catalog-api\k8s\
kubectl apply -f ..\fcg-payments-api\k8s\

# Função serverless
kubectl apply -f k8s/notifications-function.yaml

# Gateway e observabilidade
kubectl apply -f k8s/gateway/
kubectl apply -f k8s/observability/

# Escala automática (requer metrics-server)
kubectl apply -f k8s/scaling/

kubectl get pods
```

> **Atenção:** a `jwt-key` precisa ser idêntica nos três Secrets — `users-api-secret`, `catalog-api-secret` e `kong-secret`. Se divergirem, o gateway rejeita todo token válido.

### 4. Acessos (NodePort)

| O quê | URL |
|---|---|
| API Gateway | <http://localhost:30080> |
| Prometheus | <http://localhost:30090> |
| Grafana | <http://localhost:30300> |

```powershell
# Mailpit e RabbitMQ, sob demanda
kubectl port-forward svc/mailpit 8025:8025
kubectl port-forward svc/rabbitmq 15672:15672
```

### Recursos usados

| Recurso | Onde | Papel |
|---|---|---|
| `Deployment` | todos os serviços | Nenhum Pod é criado solto |
| `Service` | todos | Comunicação interna por nome DNS (`rabbitmq`, `mongo`, `redis`, `users-api`, …) |
| `ConfigMap` | configuração não sensível | Filas, hosts, TTLs de cache, `kong.yml`, `prometheus.yml`, dashboard |
| `Secret` | dados sensíveis | `jwt-key`, connection strings, credenciais do RabbitMQ e do Grafana |
| `PersistentVolumeClaim` | MongoDB | Dados do catálogo expandido sobrevivem ao restart do pod |
| `HorizontalPodAutoscaler` | Payments API, função | Escala por CPU |
| `readiness` / `liveness` | todos | Pod só entra no balanceamento quando as dependências respondem |

---

## API Gateway (Kong)

Configuração declarativa em [`gateway/kong.yml`](gateway/kong.yml) — versionada, revisada como código.

### Rotas

| Rota | Caminho | Métodos | Destino | JWT |
|---|---|---|---|---|
| `users-auth` | `/api/auth` | todos | Users API | ❌ público (rate limit 30/min por IP) |
| `catalog-public-read` | `/api/games` | `GET`, `HEAD`, `OPTIONS` | Catalog API | ❌ público (rate limit 300/min) |
| `catalog-write` | `/api/games` | `POST`, `PUT`, `PATCH`, `DELETE` | Catalog API | ✅ obrigatório |
| `catalog-library` | `/api/users/me` | todos | Catalog API | ✅ obrigatório |

A separação por método é o que permite deixar a leitura do catálogo aberta e ainda assim exigir token em toda escrita — inclusive ao publicar uma avaliação, que fica sob `/api/games/{id}/reviews`.

### Validação do JWT

O plugin `jwt` confere assinatura **HS256** e expiração antes de encaminhar. A ligação é feita pelo claim `iss`:

* a Users API assina com `Jwt:Issuer = "FCG"` e `Jwt:Key`;
* o Kong tem um consumer com `key: FCG` e o mesmo segredo.

O segredo **não fica no repositório**: `kong.yml` referencia `{vault://env/fcg-jwt-key}`, que o Kong resolve a partir da variável de ambiente `FCG_JWT_KEY` — vinda do `.env` no compose e de um `Secret` no Kubernetes.

Os microsserviços continuam validando o token por conta própria. O gateway corta o tráfego inválido na borda; o serviço não confia cegamente em quem chama.

### Outros plugins

| Plugin | Escopo | Papel |
|---|---|---|
| `rate-limiting` | rotas de auth e de leitura | Contém força bruta no login e abuso de leitura |
| `prometheus` | global | Publica métricas do gateway em `:8100/metrics` |
| `correlation-id` | global | `X-Correlation-ID` para correlacionar gateway e serviços |

### Conferindo a configuração carregada

```powershell
(Invoke-RestMethod http://localhost:8001/routes).data | Select-Object name, paths, methods
(Invoke-RestMethod http://localhost:8001/consumers).data
```

---

## Observabilidade (Prometheus + Grafana)

**Opção A do desafio.** Escolhida por ser inteiramente aberta, sem conta nem chave de terceiros, e por permitir a implantação via manifestos Kubernetes que o enunciado pede.

### Instrumentação

Users, Catalog e Payments usam **OpenTelemetry** e expõem `/metrics` no formato Prometheus:

| Origem | Métricas |
|---|---|
| `AddAspNetCoreInstrumentation` | `http_server_request_duration_seconds` — latência, contagem e status code |
| `AddHttpClientInstrumentation` | Chamadas HTTP de saída |
| `AddRuntimeInstrumentation` | GC, heap, thread pool, CPU |
| `AddMeter("MassTransit")` | Publicação e consumo de mensagens |
| Medidores próprios | Métricas de negócio e de cache |

Métricas de negócio:

| Métrica | Serviço | Labels |
|---|---|---|
| `fcg_users_registered_total` | Users | — |
| `fcg_users_logins_total` | Users | `result` |
| `fcg_catalog_cache_requests_total` | Catalog | `result` (hit/miss), `cache` |
| `fcg_catalog_purchases_started_total` | Catalog | — |
| `fcg_catalog_purchases_confirmed_total` | Catalog | — |
| `fcg_catalog_reviews_total` | Catalog | — |
| `fcg_catalog_mongo_operation_duration_seconds` | Catalog | `operation` |
| `fcg_payments_processed_total` | Payments | `status` |
| `fcg_payments_amount` | Payments | `status` |

### Coleta

* **Compose:** lista estática em [`observability/prometheus/prometheus.yml`](observability/prometheus/prometheus.yml).
* **Kubernetes:** descoberta dinâmica por `kubernetes_sd_configs`. Todo pod anotado com `prometheus.io/scrape: "true"` entra na coleta automaticamente — réplica nova é coletada sem tocar em configuração.

### Dashboard

[`observability/grafana/dashboards/fcg-overview.json`](observability/grafana/dashboards/fcg-overview.json) — 21 painéis provisionados no start, em cinco blocos:

| Bloco | Painéis |
|---|---|
| **Visão geral** | Throughput, taxa de erros, latência p95, acerto do cache · requisições por serviço e por status code · latência p50/p95/p99 · erros 4xx/5xx · rotas mais lentas |
| **API Gateway** | Requisições por status no Kong · latência do gateway x do upstream |
| **Persistência poliglota** | Cache hits x misses · acerto por tipo de consulta · duração das operações no MongoDB |
| **Fluxos de negócio** | Pagamentos por status · compras iniciadas x confirmadas · cadastros, logins e avaliações |
| **Runtime .NET** | Heap gerenciado · tempo de CPU · coletas de lixo por geração |

Os três itens exigidos pelo enunciado — **latência**, **contagem de requisições (total e por status code)** e **taxa de erros** — estão no primeiro bloco.

---

## Persistência poliglota e cache

### MongoDB — catálogo expandido e avaliações

O SQLite continua sendo a fonte da verdade transacional do jogo (título, gênero, preço, biblioteca do usuário). O MongoDB guarda o que o modelo relacional atende mal:

| Coleção | Conteúdo | Índices |
|---|---|---|
| `game_details` | Descrição, mídias, tags, plataformas, idiomas e requisitos por perfil — esquema que muda de jogo para jogo | Texto (título, descrição, tags) · tags · (ativo, gênero) |
| `game_reviews` | Avaliações dos usuários: nota, título, comentário e selo de compra verificada | Único (gameId, usuarioId) · (gameId, criadoEm desc) |

Título, gênero, preço e situação ficam **denormalizados** no documento, de modo que a busca acontece inteiramente no Mongo, sem join entre bancos. Toda escrita no catálogo relacional sincroniza esses campos.

O resumo das avaliações (total, nota média e distribuição) sai de um **pipeline de agregação** do MongoDB.

### Redis — cache distribuído

Acesso via `IDistributedCache` (`Microsoft.Extensions.Caching.StackExchangeRedis`), encapsulado em `CatalogCache`.

| Chave | TTL | Origem evitada |
|---|---|---|
| `games:list` | 60 s | Listagem do catálogo no SQLite |
| `game:{id}:details` | 300 s | Documento do MongoDB + agregação de notas |
| `game:{id}:reviews:{versão}:p{n}:s{n}` | 120 s | Página de avaliações |
| `game:{id}:reviews:{versão}:summary` | 120 s | Pipeline de agregação |
| `search:{versão}:{hash}` | 90 s | Busca no MongoDB |

**Invalidação por token de versão.** `IDistributedCache` não remove por prefixo, então cada escopo (as avaliações de um jogo, os resultados de busca) tem um token que entra na composição da chave. Gravar uma avaliação troca o token e derruba de uma vez todas as páginas em cache daquele jogo, sem varrer o Redis.

**Degradação controlada.** Falha do Redis é tratada como *miss* e registrada em log: a requisição segue para a origem e responde normalmente. `Cache__RedisEnabled=false` troca o Redis por cache em memória, útil em CI.

Cada consulta ao cache é contabilizada como *hit* ou *miss* na métrica `fcg_catalog_cache_requests_total`, o que alimenta o painel de taxa de acerto no Grafana.

---

## Função serverless

A `NotificationsAPI` da Fase 2 era um container ligado 24/7 apenas esperando mensagem. Na Fase 3 virou uma **Azure Function** .NET 8 isolated acionada pelo `RabbitMQTrigger`, no repositório `fcg-notifications-function`.

| Função | Fila | Ação |
|---|---|---|
| `UserCreatedFunction` | `notifications-user-created` | E-mail de boas-vindas |
| `PaymentProcessedFunction` | `notifications-payment-processed` | Confirmação de compra ou aviso de recusa |

### A ponte entre MassTransit e o trigger

O MassTransit publica em **exchange fanout** com o nome do tipo do evento e envolve a mensagem em um envelope. O `RabbitMQTrigger` escuta uma **fila** e entrega o corpo bruto. A ligação é feita em dois pontos:

1. [`rabbitmq/definitions.json`](rabbitmq/definitions.json) declara as filas `notifications-*` e os bindings com os exchanges do MassTransit — carregado pelo broker no start, via `load_definitions`.
2. `MassTransitEnvelope.Unwrap<T>` extrai o payload do envelope dentro da função.

Nenhum publicador precisou mudar: as APIs continuam publicando exatamente como na Fase 2.

### Storage do host de Functions

Triggers que não são HTTP exigem uma conta de storage (`AzureWebJobsStorage`) para leases e singletons. Localmente esse papel é do **Azurite**; na Azure, da Storage Account provisionada pelo Bicep.

---

## Referência de endpoints

Base: `http://localhost:8000` (compose) ou `http://localhost:30080` (Kubernetes).

### Users API

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| `POST` | `/api/auth/register` | — | Cadastra usuário e dispara boas-vindas |
| `POST` | `/api/auth/login` | — | Autentica e devolve o JWT |

### Catalog API — catálogo relacional

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| `GET` | `/api/games` | — | Lista o catálogo *(cache)* |
| `POST` | `/api/games` | Admin | Cria um jogo |
| `PUT` | `/api/games/{id}` | Admin | Atualiza um jogo |
| `DELETE` | `/api/games/{id}` | Admin | Inativa |
| `POST` | `/api/games/{id}/reactivate` | Admin | Reativa |

### Catalog API — catálogo expandido (MongoDB)

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| `GET` | `/api/games/{id}/details` | — | Documento expandido + resumo das notas *(cache)* |
| `PUT` | `/api/games/{id}/details` | Admin | Cria ou substitui o documento |
| `GET` | `/api/games/search` | — | Busca por texto, tag, gênero e preço *(cache)* |

Parâmetros da busca: `q`, `tag`, `genero`, `precoMaximo`, `pagina`, `tamanho`.

### Catalog API — avaliações (MongoDB)

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| `GET` | `/api/games/{id}/reviews` | — | Lista paginada *(cache)* |
| `GET` | `/api/games/{id}/reviews/summary` | — | Total, nota média e distribuição *(cache)* |
| `POST` | `/api/games/{id}/reviews` | ✅ | Cria ou atualiza a própria avaliação |
| `DELETE` | `/api/games/{id}/reviews/me` | ✅ | Remove a própria avaliação |

### Catalog API — biblioteca

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| `GET` | `/api/users/me/library` | ✅ | Jogos do usuário |
| `POST` | `/api/users/me/library/games/{gameId}` | ✅ | Inicia a compra (**202**) |

### Operação

| Rota | Descrição |
|---|---|
| `/health/live` | O processo está de pé |
| `/health/ready` | As dependências respondem (SQLite e, no Catalog, MongoDB) |
| `/metrics` | Métricas no formato Prometheus |

---

## Solução de problemas

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| Kong devolve **401** com token recém-emitido | `JWT_KEY` diferente entre Kong e Users API | Conferir `.env` e os Secrets; os três valores têm que ser iguais |
| Kong devolve **404** | Caminho fora das rotas declaradas | `Invoke-RestMethod http://localhost:8001/routes` |
| `/api/games/{id}/details` responde **404** | O jogo não existe no SQLite | Criar o jogo antes de enviar o documento expandido |
| Catalog API não sobe | MongoDB ainda iniciando | `docker compose logs mongo`; o `depends_on` já aguarda o healthcheck |
| Nenhum e-mail no Mailpit | Fila sem binding ou função parada | `docker compose logs notifications-function` e conferir as filas `notifications-*` no RabbitMQ |
| Alvo *down* no Prometheus | Serviço ainda subindo | <http://localhost:9090/targets> |
| Painel do Grafana vazio | Sem tráfego no intervalo | Gerar carga (passo 9 do roteiro) e reduzir a janela de tempo |
| Cache sempre *miss* | Redis fora do ar | `docker compose logs redis`; a API degrada de propósito e continua respondendo |
| HPA com `<unknown>` | `metrics-server` ausente | Instalar o metrics-server no cluster |
