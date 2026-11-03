# FCG Platform — orquestração da FIAP Cloud Games

Repositório central da plataforma. Reúne a infraestrutura como código da Azure, os manifestos Kubernetes, a pipeline da plataforma, a configuração do API Gateway e a stack de observabilidade dos microsserviços da FCG.

> **Fase 4 — Automação, performance e portabilidade.** A plataforma sai do cluster local e passa a rodar no **Azure Kubernetes Service (AKS)**, com imagens privadas no **Azure Container Registry (ACR)**, deploy contínuo por **GitHub Actions** com rolling update sem downtime, **busca avançada com Elasticsearch**, banco relacional compartilhado (**PostgreSQL**) e **nenhuma credencial em código ou YAML**.

---

## Índice

1. [O que mudou na Fase 4](#o-que-mudou-na-fase-4)
2. [Arquitetura](#arquitetura)
3. [Repositórios](#repositórios)
4. [Decisões da Fase 4](#decisões-da-fase-4)
5. [Subindo na Azure (AKS)](#subindo-na-azure-aks)
6. [CI/CD com GitHub Actions](#cicd-com-github-actions)
7. [Segurança e gestão de segredos](#segurança-e-gestão-de-segredos)
8. [Persistência poliglota e cache](#persistência-poliglota-e-cache)
9. [Busca avançada com Elasticsearch](#busca-avançada-com-elasticsearch)
10. [Roteiro de demonstração em produção](#roteiro-de-demonstração-em-produção)
11. [Ambiente local](#ambiente-local)
12. [API Gateway (Kong)](#api-gateway-kong)
13. [Observabilidade (Prometheus + Grafana)](#observabilidade-prometheus--grafana)
14. [Função serverless](#função-serverless)
15. [Referência de endpoints](#referência-de-endpoints)
16. [Custos e limpeza](#custos-e-limpeza)
17. [Solução de problemas](#solução-de-problemas)

---

## O que mudou na Fase 4

| Gargalo do enunciado | Solução |
|---|---|
| **Ambiente de produção gerenciado** | AKS provisionado por **Bicep** ([`infra/azure/main.bicep`](infra/azure/main.bicep)), imagens no **ACR** privado (sem usuário admin; o AKS puxa por identidade gerenciada), gateway exposto por **Azure Load Balancer** |
| **Automação de entrega** | Uma pipeline por serviço (**Build & Test → Imagem com tag versionada → Trivy → Push no ACR → Rolling Update no AKS**, com rollback automático) e uma pipeline da plataforma. Login na Azure por **OIDC**, sem senha guardada |
| **Performance e latência** | **Elasticsearch** para a busca (fuzzy + relevância), **Redis** na frente das consultas caras, **MongoDB** para dados flexíveis e de alto volume, **PostgreSQL** compartilhado entre as réplicas |
| **Segurança** | **Zero hardcoded credentials**: os `secret.yaml` com valores saíram dos repositórios; os Secrets do Kubernetes são criados pela pipeline a partir dos GitHub Secrets. MongoDB, Redis, RabbitMQ e Elasticsearch passaram a exigir autenticação |

O que já vinha da Fase 3 e continua: Kong como gateway, a função serverless de notificações, a observabilidade com Prometheus + Grafana, MongoDB e Redis no Catalog.

---

## Arquitetura

```
                          Internet
                             │
                 ┌───────────▼────────────┐
                 │  Azure Load Balancer   │  IP público (Service kong-proxy, type LoadBalancer)
                 └───────────┬────────────┘
  ┌──────────────────────────┼─────────────────────── AKS ──────────────────────────────────┐
  │              ┌───────────▼────────────┐                                                  │
  │              │   Kong API Gateway     │  rota · JWT · rate limit · métricas              │
  │              └─────┬────────────┬─────┘                                                  │
  │        /api/auth   │            │  /api/games · /api/games/search · /api/users/me        │
  │              ┌─────▼─────┐ ┌────▼──────────────┐                                         │
  │              │ Users API │ │   Catalog API     │──▶ Elasticsearch  (busca fuzzy)         │
  │              │  2..5 pods│ │    2..5 pods      │──▶ MongoDB        (catálogo expandido)  │
  │              └─────┬─────┘ └────┬──────────────┘──▶ Redis          (cache distribuído)   │
  │                    │            │                                                        │
  │                    ├────────────┴──────────▶ PostgreSQL (fcg_users · fcg_catalog · fcg_payments)
  │                    ▼            ▼                                                        │
  │              ┌────────────────────────┐                                                  │
  │              │ RabbitMQ (MassTransit) │                                                  │
  │              └──────┬───────────┬─────┘                                                  │
  │        OrderPlaced  │           │  UserCreated / PaymentProcessed                        │
  │              ┌──────▼──────┐ ┌──▼────────────────────┐                                   │
  │              │ Payments API│ │ Notifications Function│──▶ Mailpit                        │
  │              └─────────────┘ └───────────────────────┘                                   │
  │                                                                                          │
  │   Prometheus (descoberta por anotação) ──▶ Grafana          Container Insights ──▶ portal│
  └──────────────────────────────────────────────────────────────────────────────────────────┘
            ▲  kubectl apply -k (rolling update)            ▲  imagens privadas (AcrPull)
            │                                               │
     GitHub Actions ── push da imagem versionada ──▶ Azure Container Registry
        (OIDC)
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

**Cadastro ou edição de jogo (novo na Fase 4)**
1. `POST`/`PUT /api/games` grava no PostgreSQL (fonte da verdade).
2. O documento do MongoDB é sincronizado e o jogo é **reindexado no Elasticsearch** na mesma requisição.
3. O cache de buscas no Redis é invalidado por troca de token de versão.

---

## Repositórios

| Repositório | Conteúdo | Pipeline |
|---|---|---|
| [`fcg-platform`](https://github.com/VHMoschini/fcg-platform) | **Este.** Bicep da Azure, manifestos da infra, Kong, Prometheus/Grafana, scripts de segredos | `platform-deploy.yml` |
| [`fcg-users-api`](https://github.com/VHMoschini/fcg-users-api) | Cadastro, autenticação e emissão de JWT | `ci-cd.yml` |
| [`fcg-catalog-api`](https://github.com/VHMoschini/fcg-catalog-api) | Catálogo, busca avançada, avaliações e biblioteca | `ci-cd.yml` |
| [`fcg-payments-api`](https://github.com/VHMoschini/fcg-payments-api) | Simulação de pagamento | `ci-cd.yml` |
| [`fcg-notifications-function`](https://github.com/VHMoschini/fcg-notifications-function) | Notificações como Azure Function | `ci-cd.yml` |

Cada serviço é dono do próprio código, dos próprios manifestos (`k8s/` com `kustomization.yaml`) e da própria pipeline. Este repositório cuida do que é compartilhado.

```
fcg-platform/
├─ infra/azure/            Bicep (ACR + AKS + Container Insights) e setup-azure.ps1
├─ k8s/                    PostgreSQL, MongoDB, Redis, Elasticsearch, RabbitMQ, Mailpit, Azurite
│  ├─ gateway/             Kong
│  ├─ observability/       Prometheus e Grafana
│  ├─ scaling/             HPAs da Payments e da função
│  └─ secrets/             create-secrets.sh (pipeline) e create-secrets.ps1 (local)
├─ gateway/kong.yml        Configuração declarativa do Kong (compose e Kubernetes)
├─ observability/          prometheus.yml e dashboard do Grafana
├─ fase4/                  Relatório de entrega e roteiro do vídeo
└─ .github/workflows/      platform-deploy.yml
```

---

## Decisões da Fase 4

| Requisito | Escolha | Por quê |
|---|---|---|
| **Nuvem** | **Azure** | Mesma nuvem da função serverless da Fase 3 (Bicep, Function App). Um provedor só, uma identidade só |
| **Kubernetes gerenciado** | **AKS**, plano Free, 2 nós `Standard_B2ms`, Azure CNI overlay | Control plane gerenciado sem custo; 8 GB por nó comportam Elasticsearch, bancos e APIs |
| **Registry** | **ACR** Basic, `adminUserEnabled: false` | O AKS puxa as imagens pela identidade gerenciada (role `AcrPull`); a pipeline publica por OIDC (role `AcrPush`). Nenhuma senha de registry existe |
| **Exposição** | **Service `type: LoadBalancer` no Kong** | O Kong já é o gateway (roteamento, JWT, rate limit); colocar um Ingress Controller na frente dele seria um segundo proxy fazendo o mesmo papel. O AKS provisiona o Azure Load Balancer e o IP público |
| **IaC** | **Bicep** + manifestos K8s com **Kustomize** | Bicep é a linguagem nativa da Azure. Kustomize troca a imagem por commit sem template nem `sed` |
| **CI/CD** | **GitHub Actions** | Os repositórios já estão no GitHub; OIDC com a Azure é nativo |
| **Autenticação da pipeline** | **OIDC (federated credentials)** | Cada execução recebe um token de curta duração; não há client secret para vazar ou expirar |
| **Versionamento de imagem** | `<run>-<sha7>` (ex.: `42-a1b2c3d`) + `latest` | Tag imutável por commit: dá para saber exatamente o que está em produção e voltar para uma versão identificável |
| **Security scan** | **Trivy** (imagem oficial, versão fixa) | Lista CVEs HIGH/CRITICAL com correção disponível. Por padrão só relata; `TRIVY_EXIT_CODE=1` faz bloquear |
| **Deploy** | **Rolling Update** `maxSurge: 1`, `maxUnavailable: 0` + `preStop` + rollback automático | Pod novo só substitui o antigo depois de passar no readiness; se não ficar saudável em 5 min, a pipeline faz `rollout undo` |
| **Busca** | **Elasticsearch 8** no cluster, via API REST | Fuzzy e relevância nativos; a mesma chamada REST funciona em Elastic Cloud e Amazon OpenSearch |
| **NoSQL** | **MongoDB** (`MongoDB.Driver`) — mantido da Fase 3 | Catálogo expandido de esquema livre e avaliações de alta volumetria |
| **Cache** | **Redis** (`IDistributedCache`) — mantido e estendido | Agora também guarda resultados da busca no Elasticsearch |
| **Relacional** | **PostgreSQL** no cluster (antes: SQLite por pod) | O SQLite em `emptyDir` dava um banco diferente para cada réplica e era apagado a cada rolling update. O SQLite continua no desenvolvimento local e nos testes |
| **Segredos** | **Secrets do Kubernetes** criados pela pipeline a partir dos **GitHub Secrets** | Nada em código ou YAML; os valores existem só nos GitHub Secrets e no cluster |

---

## Subindo na Azure (AKS)

### Pré-requisitos

| Ferramenta | Para quê |
|---|---|
| Azure CLI (`az login`) | Criar a infra e a identidade da pipeline |
| GitHub CLI (`gh auth login`) com admin nos repositórios | Gravar Secrets e Variables |
| kubectl | Acompanhar o cluster |
| PowerShell 7+ | Rodar o `setup-azure.ps1` |

### 1. Infra e GitHub em um comando

```powershell
cd fcg-platform\infra\azure
.\setup-azure.ps1                       # padrão: rg-fcg em eastus
.\setup-azure.ps1 -Location brazilsouth # outra região
```

O script:

1. cria o resource group e aplica o [`main.bicep`](infra/azure/main.bicep) — ACR, AKS e Container Insights, mais a permissão `AcrPull` dos nós do AKS sobre o ACR;
2. configura o `kubectl` local para o cluster;
3. cria a identidade `fcg-github-actions` com **federated credentials** para cada repositório (`repo:VHMoschini/<repo>:ref:refs/heads/main`) e as roles mínimas: `AcrPush` no ACR, `Azure Kubernetes Service Cluster User Role` no AKS e `Reader` no resource group;
4. gera senhas aleatórias e grava os **GitHub Secrets e Variables** de todos os repositórios. As senhas ficam também em `infra/azure/.secrets.local.json` (no `.gitignore`) para consulta durante a demonstração.

### 2. Plataforma

No GitHub: **fcg-platform → Actions → Platform deploy → Run workflow**. A pipeline cria os Secrets do Kubernetes, sobe PostgreSQL, MongoDB, Redis, Elasticsearch, RabbitMQ, Kong e a observabilidade.

### 3. Serviços

Em cada repositório de serviço: **Actions → CI/CD → Run workflow** (ou qualquer push na branch principal). Cada pipeline publica a imagem no ACR e faz o rolling update no AKS.

### 4. Acesso

```powershell
kubectl get svc kong-proxy        # EXTERNAL-IP = IP público do gateway
$GW = "http://<EXTERNAL-IP>"

kubectl get pods -o wide          # pods distribuídos pelos 2 nós
kubectl port-forward svc/grafana 3000:3000
kubectl port-forward svc/mailpit 8025:8025
```

No portal da Azure: **resource group `rg-fcg` → `fcg-aks` → Workloads / Insights** mostra pods, nós, CPU e memória; **`fcgacr...` → Repositories** mostra as imagens com as tags de cada commit.

### Recursos Kubernetes usados

| Recurso | Onde | Papel |
|---|---|---|
| `Deployment` | APIs, função, Kong, Redis, RabbitMQ, MongoDB, observabilidade | Nenhum pod é criado solto |
| `StatefulSet` + `volumeClaimTemplates` | PostgreSQL, Elasticsearch | Identidade estável e Azure Disk por réplica |
| `Service` `ClusterIP` | todos | Comunicação interna por nome DNS |
| `Service` `LoadBalancer` | `kong-proxy` | Único ponto exposto à internet |
| `ConfigMap` | configuração não sensível | Hosts, TTLs de cache, provider do banco, `kong.yml`, dashboard |
| `Secret` | credenciais | Criados pela pipeline, nunca versionados |
| `HorizontalPodAutoscaler` | Users, Catalog, Payments, função | Escala por CPU (o AKS já traz o metrics-server) |
| `readiness` / `liveness` / `preStop` | todos | Rolling update sem requisição perdida |

---

## CI/CD com GitHub Actions

### Pipeline de cada serviço — `.github/workflows/ci-cd.yml`

```
push na main
  │
  ├─ Build & Test ── dotnet restore · build (Release) · dotnet test (xUnit, cobertura, resultados como artefato)
  │
  ├─ Imagem ──────── docker build · tag <run>-<sha7> + latest · Trivy (HIGH/CRITICAL) · az acr login (OIDC) · docker push
  │
  └─ Deploy no AKS ─ azure/login (OIDC) · aks-set-context
                     kustomize edit set image <svc>=<acr>.azurecr.io/<svc>:<tag>
                     kubectl apply -k k8s/
                     kubectl rollout status (5 min) ── falhou? ──▶ kubectl rollout undo
```

Em **pull request** roda Build & Test e o build/scan da imagem, sem push e sem deploy. O resumo de cada execução mostra a imagem publicada, os pods e o histórico de revisões.

| Serviço | Testes unitários |
|---|---|
| Users API | Validação de credenciais, emissão e validação do JWT, cadastro e login (SQLite em memória, broker substituído por dublê), entidade `Usuario` |
| Catalog API | Montagem da consulta do Elasticsearch (fuzzy, pesos, filtros, ordenação), cliente HTTP do Elasticsearch (handler falso), serviço de busca com cache e fallback, indexador, cache distribuído, entidade `Jogo` |
| Payments API / Função | Build e imagem (sem testes nesta fase) |

### Pipeline da plataforma — `.github/workflows/platform-deploy.yml`

Valida a sintaxe dos manifestos e **reprova se algum manifesto voltar a ter `kind: Secret`**. Depois cria os Secrets, os ConfigMaps gerados (`kong.yml`, dashboard) e aplica bancos, busca, mensageria, gateway, observabilidade e HPAs, aguardando cada rollout.

### Por que o deploy não derruba nada

* `maxUnavailable: 0` — nenhum pod antigo sai antes de um novo estar pronto;
* `readinessProbe` em `/health/ready` — o pod novo só entra no balanceamento quando o banco (e, no Catalog, o MongoDB) responde;
* `preStop: sleep 5` — o pod antigo sai do Service antes de receber o SIGTERM, sem cortar requisição em andamento;
* `rollout undo` automático se o rollout não terminar.

---

## Segurança e gestão de segredos

**Nenhuma string de conexão, chave ou senha está no código-fonte ou nos YAML.**

```
GitHub Secrets (fcg-platform)
        │  injetados como variáveis de ambiente no runner (mascarados no log)
        ▼
k8s/secrets/create-secrets.sh ── kubectl create secret ... --dry-run=client | kubectl apply
        │
        ▼
Secrets do Kubernetes ── secretKeyRef nos Deployments ──▶ variáveis de ambiente do pod em runtime
```

| Secret | Usado por | Conteúdo |
|---|---|---|
| `postgres-secret` | PostgreSQL | usuário e senha |
| `mongo-secret` | MongoDB | usuário root e senha |
| `redis-secret` | Redis | `requirepass` |
| `rabbitmq-secret` | RabbitMQ | usuário padrão e senha |
| `elasticsearch-secret` | Elasticsearch | senha do usuário `elastic` |
| `azurite-secret` | Azurite | conta e chave próprias (não a chave pública do emulador) |
| `grafana-secret` | Grafana | admin |
| `kong-secret` | Kong | chave do JWT |
| `users-api-secret` | Users API | JWT, connection string, RabbitMQ, senha do admin inicial |
| `catalog-api-secret` | Catalog API | JWT, connection strings (PostgreSQL, MongoDB, Redis), RabbitMQ, Elasticsearch |
| `payments-api-secret` | Payments API | connection string, RabbitMQ |
| `notifications-function-secret` | Função | AMQP, storage do host |

O que mudou no código para isso:

* `appsettings.json` sem `Jwt:Key`, sem usuário/senha do RabbitMQ e sem senha de seed;
* `Program.cs` sem fallback `"guest"` — sem credencial configurada, o MassTransit usa o padrão do broker local;
* `<UserSecretsId>` nos projetos, para desenvolvimento local com `dotnet user-secrets`;
* o `docker-compose.yml` exige `JWT_KEY`, `SEED_ADMIN_PASSWORD`, `ELASTIC_PASSWORD` e `GRAFANA_PASSWORD` no `.env` e recusa subir sem eles.

Na pipeline, a autenticação na Azure é por **OIDC**: não há client secret em lugar nenhum.

---

## Persistência poliglota e cache

| Banco | Papel | Por quê |
|---|---|---|
| **PostgreSQL** | Fonte da verdade transacional: usuários, jogos, biblioteca, pagamentos | Compartilhado entre as réplicas e persistente; cada serviço tem o seu database |
| **MongoDB** | Catálogo expandido (`game_details`) e avaliações (`game_reviews`) | Esquema que muda a cada jogo; avaliações com alto volume e agregação de notas |
| **Elasticsearch** | Índice de busca `fcg-games` | Busca textual tolerante a erro e ordenada por relevância, fora do banco transacional |
| **Redis** | Cache distribuído | Tira do caminho as consultas mais caras e repetidas |

### Como o NoSQL entra na arquitetura

O PostgreSQL guarda o jogo (título, gênero, preço, situação) e o vínculo com a biblioteca. O MongoDB guarda o que mudaria o schema relacional a cada jogo novo — descrição, mídias, tags, plataformas, requisitos por perfil — e as avaliações dos usuários, com o resumo de notas calculado por pipeline de agregação. Título, gênero, preço e situação ficam denormalizados no documento, e toda escrita no relacional os sincroniza (`GameDetailsService.SyncFromRelationalAsync`).

O índice do Elasticsearch junta os dois: dados do PostgreSQL + texto rico do MongoDB.

### Redis — o que fica em cache

| Chave | TTL | Origem evitada |
|---|---|---|
| `games:list` | 60 s | Listagem do catálogo no PostgreSQL |
| `game:{id}:details` | 300 s | Documento do MongoDB + agregação de notas |
| `game:{id}:reviews:{versão}:p{n}:s{n}` | 120 s | Página de avaliações |
| `game:{id}:reviews:{versão}:summary` | 120 s | Pipeline de agregação |
| `search:{versão}:{hash}` | 90 s | **Busca no Elasticsearch** (e a busca simples no MongoDB) |

**Invalidação por token de versão:** cada escopo tem um token que entra na chave; gravar troca o token e derruba todas as entradas daquele escopo de uma vez.

**Degradação controlada:** falha do Redis vira *miss* e a origem responde; a métrica `fcg_catalog_cache_requests_total` alimenta o painel de acerto no Grafana.

---

## Busca avançada com Elasticsearch

Detalhes no [README da Catalog API](https://github.com/VHMoschini/fcg-catalog-api#busca-avançada-elasticsearch). Resumo:

| Item | Como |
|---|---|
| Endpoint | `GET /api/games/search?q=&genero=&tag=&precoMaximo=&pagina=&tamanho=` |
| Fuzzy | `multi_match` com `fuzziness: AUTO` — "eldn rign" encontra *Elden Ring* |
| Relevância | Ordena por `_score`; o título pesa 4×, tags 2×; frase exata e prefixo no título somam pontos |
| Acentos | Analisador com `asciifolding`: "acao" encontra "ação" |
| Sincronização | Todo insert/edit/inativação de jogo e todo `PUT /details` reindexa o jogo na hora (`refresh=wait_for`) |
| Recuperação | No start, se o índice estiver vazio, reindexa tudo; `POST /api/games/search/reindex` (admin) refaz sob demanda |
| Degradação | Elasticsearch fora do ar → a busca responde pelo MongoDB e o campo `motor` mostra `"mongodb"` |
| Cache | Resultado vai para o Redis; a segunda busca igual volta com `doCache: true` |
| Comparação | `GET /api/games/search/basic` é a busca da Fase 3 (MongoDB, sem fuzzy) |

---

## Roteiro de demonstração em produção

O roteiro completo do vídeo, com tempos e falas, está em [`fase4/ROTEIRO_VIDEO_FASE4.md`](fase4/ROTEIRO_VIDEO_FASE4.md). Os comandos essenciais:

```powershell
$GW = "http://$(kubectl get svc kong-proxy -o jsonpath='{.status.loadBalancer.ingress[0].ip}')"
$senhas = Get-Content .\infra\azure\.secrets.local.json | ConvertFrom-Json

# Login do admin
$login = Invoke-RestMethod "$GW/api/auth/login" -Method Post -ContentType 'application/json' `
  -Body (@{ email = "admin@fcg.local"; password = $senhas.SEED_ADMIN_PASSWORD } | ConvertTo-Json)
$admin = @{ Authorization = "Bearer $($login.token)" }

# Cadastra um jogo: grava no PostgreSQL, sincroniza o MongoDB e indexa no Elasticsearch
$jogo = Invoke-RestMethod "$GW/api/games" -Method Post -Headers $admin -ContentType 'application/json' `
  -Body '{"titulo":"Elden Ring","genero":"RPG de acao","preco":249.90}'

# Busca com erro de digitação
Invoke-RestMethod "$GW/api/games/search?q=eldn%20rign" | Select-Object motor, total, doCache, tempoMs
(Invoke-RestMethod "$GW/api/games/search?q=eldn%20rign").itens | Select-Object titulo, relevancia

# Busca simples (Fase 3) não encontra nada com o mesmo erro
(Invoke-RestMethod "$GW/api/games/search/basic?q=eldn%20rign").total

# Impacto do cache: 1ª chamada vai ao MongoDB, a 2ª sai do Redis
1..3 | ForEach-Object { (Measure-Command { Invoke-RestMethod "$GW/api/games/$($jogo.id)/details" }).TotalMilliseconds }
```

---

## Ambiente local

### Docker Compose

```powershell
cd fcg-platform
Copy-Item .env.example .env      # preencha JWT_KEY, SEED_ADMIN_PASSWORD, ELASTIC_PASSWORD, GRAFANA_PASSWORD
docker compose up -d --build
```

Localmente as APIs usam **SQLite** (provider padrão) e o compose sobe também o Elasticsearch.

| O quê | URL |
|---|---|
| **API Gateway** | <http://localhost:8000> |
| Grafana | <http://localhost:3000> |
| Prometheus | <http://localhost:9090> |
| Mailpit | <http://localhost:8025> |
| RabbitMQ Management | <http://localhost:15672> |
| Elasticsearch | <http://localhost:9200> (usuário `elastic`) |

> O RabbitMQ do compose usa a credencial padrão de desenvolvimento do broker (`guest`), que só existe no ambiente local. No cluster o usuário e a senha vêm do Secret `rabbitmq-secret`.

### Kubernetes local (Docker Desktop)

```powershell
.\build-images.ps1        # imagens fcg/*:latest
.\deploy-k8s.ps1          # cria os Secrets (senhas aleatórias), aplica infra, serviços (-k), gateway e observabilidade
.\deploy-k8s.ps1 -Delete  # desfaz
```

O gateway responde em <http://localhost> (o Docker Desktop atende `type: LoadBalancer` no localhost).

---

## API Gateway (Kong)

Configuração declarativa em [`gateway/kong.yml`](gateway/kong.yml), igual no compose e no Kubernetes.

| Rota | Caminho | Métodos | Destino | JWT |
|---|---|---|---|---|
| `users-auth` | `/api/auth` | todos | Users API | ❌ público (rate limit 30/min por IP) |
| `catalog-public-read` | `/api/games` | `GET`, `HEAD`, `OPTIONS` | Catalog API | ❌ público (rate limit 300/min) — inclui a busca |
| `catalog-write` | `/api/games` | `POST`, `PUT`, `PATCH`, `DELETE` | Catalog API | ✅ obrigatório — inclui o reindex |
| `catalog-library` | `/api/users/me` | todos | Catalog API | ✅ obrigatório |

O plugin `jwt` confere assinatura HS256 e expiração antes de encaminhar; o segredo vem de `{vault://env/fcg-jwt-key}`, resolvido a partir do Secret `kong-secret`. Os microsserviços continuam validando o token por conta própria.

---

## Observabilidade (Prometheus + Grafana)

Users, Catalog e Payments expõem `/metrics` (OpenTelemetry). No Kubernetes o Prometheus descobre os pods por anotação (`prometheus.io/scrape: "true"`), então réplica nova criada pelo HPA ou pelo rolling update entra na coleta sozinha.

O dashboard [`fcg-overview.json`](observability/grafana/dashboards/fcg-overview.json) é provisionado no start e tem um bloco novo na Fase 4:

| Bloco | Painéis |
|---|---|
| Visão geral | Throughput, taxa de erros, latência p95, acerto do cache, requisições por serviço e status |
| API Gateway | Requisições por status no Kong, latência do gateway × upstream |
| Persistência poliglota | Cache hits × misses, acerto por tipo de consulta, duração das operações no MongoDB |
| **Busca avançada** | **Buscas por motor (Elasticsearch / MongoDB / Redis), duração p95 no motor, indexações por resultado** |
| Fluxos de negócio | Pagamentos, compras, cadastros e logins |
| Runtime .NET | Heap, alocação, coletas de lixo |

Métricas novas da Catalog API: `fcg_catalog_search_requests_total{engine}`, `fcg_catalog_search_duration_seconds`, `fcg_catalog_search_index_operations_total{operation,result}`.

---

## Função serverless

A notificação por e-mail é uma **Azure Function** .NET 8 isolated acionada por `RabbitMQTrigger` (repositório `fcg-notifications-function`), rodando como pod no AKS ao lado do Azurite, que faz o papel da storage do host. Na Fase 4 o manifesto dela foi para o próprio repositório, junto da pipeline que gera a imagem.

| Função | Fila | Ação |
|---|---|---|
| `UserCreatedFunction` | `notifications-user-created` | E-mail de boas-vindas |
| `PaymentProcessedFunction` | `notifications-payment-processed` | Confirmação de compra ou aviso de recusa |

As filas e os bindings com os exchanges do MassTransit são importados no RabbitMQ pelo hook `postStart` do container (no compose, por `load_definitions`).

---

## Referência de endpoints

Base: `http://<IP do kong-proxy>` (AKS), `http://localhost:8000` (compose) ou `http://localhost` (Kubernetes local).

### Users API

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| `POST` | `/api/auth/register` | — | Cadastra usuário e dispara boas-vindas |
| `POST` | `/api/auth/login` | — | Autentica e devolve o JWT |

### Catalog API

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| `GET` | `/api/games` | — | Lista o catálogo *(cache)* |
| `POST` | `/api/games` | Admin | Cria um jogo *(indexa no Elasticsearch)* |
| `PUT` | `/api/games/{id}` | Admin | Atualiza *(reindexa)* |
| `DELETE` | `/api/games/{id}` | Admin | Inativa *(reindexa como inativo)* |
| `POST` | `/api/games/{id}/reactivate` | Admin | Reativa |
| `GET` | `/api/games/{id}/details` | — | Documento expandido + resumo das notas *(cache)* |
| `PUT` | `/api/games/{id}/details` | Admin | Cria ou substitui o documento *(reindexa)* |
| `GET` | **`/api/games/search`** | — | **Busca avançada — fuzzy e relevância** *(cache)* |
| `GET` | `/api/games/search/basic` | — | Busca simples no MongoDB (Fase 3) |
| `POST` | `/api/games/search/reindex` | Admin | Reconstrói o índice |
| `GET` | `/api/games/info` | — | Versão do código e pod que respondeu (acompanhar deploy) |
| `GET` | `/api/games/{id}/reviews` | — | Avaliações paginadas *(cache)* |
| `GET` | `/api/games/{id}/reviews/summary` | — | Total, média e distribuição *(cache)* |
| `POST` | `/api/games/{id}/reviews` | ✅ | Cria ou atualiza a própria avaliação |
| `DELETE` | `/api/games/{id}/reviews/me` | ✅ | Remove a própria avaliação |
| `GET` | `/api/users/me/library` | ✅ | Jogos do usuário |
| `POST` | `/api/users/me/library/games/{gameId}` | ✅ | Inicia a compra (**202**) |

### Operação

| Rota | Descrição |
|---|---|
| `/health/live` | O processo está de pé |
| `/health/ready` | As dependências respondem (banco relacional e, no Catalog, MongoDB) |
| `/metrics` | Métricas no formato Prometheus |

---

## Custos e limpeza

O cluster cobra por hora enquanto existir (2 VMs B2ms, discos, Load Balancer e IP público; o control plane do AKS no plano Free não é cobrado). Fora das gravações:

```powershell
az aks stop  --resource-group rg-fcg --name fcg-aks   # para os nós, mantém tudo
az aks start --resource-group rg-fcg --name fcg-aks

az group delete --name rg-fcg --yes --no-wait          # apaga tudo
```

---

## Solução de problemas

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| Pod em `CreateContainerConfigError` | Secret ainda não existe | Rode o workflow **Platform deploy** antes das pipelines dos serviços |
| Pod em `ImagePullBackOff` | AKS sem `AcrPull` no ACR, ou tag inexistente | `az aks check-acr --resource-group rg-fcg --name fcg-aks --acr <acr>.azurecr.io` |
| Pipeline falha no `azure/login` | Federated credential não bate com repo/branch | Conferir o subject `repo:<owner>/<repo>:ref:refs/heads/<branch>` no app `fcg-github-actions` |
| `kong-proxy` sem `EXTERNAL-IP` | Load Balancer ainda sendo criado | Aguarde 1–2 min: `kubectl get svc kong-proxy -w` |
| Busca responde `"motor": "mongodb"` | Elasticsearch fora ou ainda subindo | `kubectl logs statefulset/elasticsearch`; depois `POST /api/games/search/reindex` |
| Elasticsearch em `Pending` | Nós sem memória livre | `kubectl describe pod elasticsearch-0`; aumente `quantidadeNos` ou `tamanhoNo` no Bicep |
| Catalog/Users reiniciando no primeiro deploy | PostgreSQL ainda iniciando | A API tenta de novo com espera crescente; aguarde o `postgres-0` ficar `Ready` |
| Kong devolve **401** com token recém-emitido | `JWT_KEY` diferente entre Kong e Users | Os três Secrets vêm do mesmo `JWT_KEY`; rode o Platform deploy de novo e reinicie as APIs |
| Cache sempre *miss* | Redis fora do ar ou senha errada | `kubectl logs deploy/catalog-api`; a API degrada de propósito e continua respondendo |
| HPA com `<unknown>` | Pods sem `resources.requests` ou recém-criados | Aguarde a primeira coleta do metrics-server (~1 min) |
