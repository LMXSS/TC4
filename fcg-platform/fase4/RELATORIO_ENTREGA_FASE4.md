# Relatório de entrega — Tech Challenge Fase 4

**Pós Tech — Arquitetura de Sistemas .NET**
**Fase 4 — Automação, performance e portabilidade**

---

## 1. Identificação

| | |
|---|---|
| **Nome do grupo** | `PREENCHER` |
| **Data de entrega** | `PREENCHER` |

### Participantes

| Nome completo | Username no Discord |
|---|---|
| Brenda Aparecida Ferreira da Silva | `PREENCHER` |
| Gabriel Lamas de Carvalho | `PREENCHER` |
| Leonardo de Sales Araujo | `PREENCHER` |
| Nicholas Eduardo Cezar | `PREENCHER` |
| Victor Hugo Moschini Santos | `PREENCHER` |

---

## 2. Links

| Item | Link |
|---|---|
| **Vídeo "Pitch Final"** | `PREENCHER` |
| **Documentação (README detalhado)** | https://github.com/LMXSS/TC4/blob/main/fcg-platform/README.md |

### Repositório

**https://github.com/LMXSS/TC4** — monorepo com os cinco projetos, um por pasta:

| Pasta | Papel | Link |
|---|---|---|
| `fcg-platform` | Bicep da Azure, manifestos da infra, Kong, observabilidade, pipeline da plataforma | https://github.com/LMXSS/TC4/tree/main/fcg-platform |
| `fcg-users-api` | Cadastro, autenticação e JWT — com testes e pipeline | https://github.com/LMXSS/TC4/tree/main/fcg-users-api |
| `fcg-catalog-api` | Catálogo, **busca avançada (Elasticsearch)**, MongoDB, Redis — com testes e pipeline | https://github.com/LMXSS/TC4/tree/main/fcg-catalog-api |
| `fcg-payments-api` | Simulação de pagamento — com pipeline | https://github.com/LMXSS/TC4/tree/main/fcg-payments-api |
| `fcg-notifications-function` | Notificações (Azure Function) — com pipeline | https://github.com/LMXSS/TC4/tree/main/fcg-notifications-function |

---

## 3. Atendimento aos requisitos

### 3.1 Infraestrutura gerenciada e portabilidade

| Exigência | Como foi atendida | Onde |
|---|---|---|
| Provedor de nuvem | **Azure** | — |
| Kubernetes gerenciado (sem IaaS isolado) | **AKS**, plano Free, 2 nós `Standard_B2ms`, Azure CNI overlay | `fcg-platform/infra/azure/main.bicep` |
| Registry privado nativo | **ACR** Basic, `adminUserEnabled: false`; o AKS puxa por identidade gerenciada (role `AcrPull` atribuída no Bicep) | `main.bicep` |
| Exposição externa | Kong como `Service type: LoadBalancer` → **Azure Load Balancer** com IP público | `fcg-platform/k8s/gateway/kong.yaml` |
| Infra como código | Bicep + manifestos Kubernetes com Kustomize | `infra/azure/`, `k8s/` de cada repositório |

Um script (`infra/azure/setup-azure.ps1`) provisiona tudo e configura a pipeline: aplica o Bicep, conecta o kubectl, cria a identidade OIDC do GitHub Actions com permissões mínimas e grava os GitHub Secrets.

### 3.2 Persistência poliglota e alta performance

**NoSQL — MongoDB** com `MongoDB.Driver` 3.11.1 (oficial):

| Coleção | Cenário do enunciado | Conteúdo |
|---|---|---|
| `game_details` | Catálogo expandido | Descrição, mídias, tags, plataformas, idiomas e requisitos por perfil — esquema que muda a cada jogo |
| `game_reviews` | Sistema de avaliações | Nota, comentário e compra verificada, com resumo por pipeline de agregação |

**Cache — Redis** com `IDistributedCache` (`Microsoft.Extensions.Caching.StackExchangeRedis`):

| O que fica em cache | TTL |
|---|---|
| Listagem do catálogo | 60 s |
| Documento expandido + resumo das notas | 300 s |
| Páginas e resumo de avaliações | 120 s |
| **Resultados da busca no Elasticsearch** (novo) | 90 s |

Invalidação por token de versão e degradação controlada (Redis fora vira *miss*).

**Relacional — PostgreSQL** (novo): até a Fase 3 cada pod tinha um SQLite em `emptyDir`, o que dava um banco diferente por réplica e apagava os dados a cada rolling update. Na Fase 4 as réplicas de Users, Catalog e Payments compartilham um PostgreSQL com volume persistente; o provider é escolhido por configuração (`Database__Provider`), e o SQLite segue no desenvolvimento local e nos testes.

### 3.3 Busca avançada com Elasticsearch

| Exigência | Como foi atendida | Onde |
|---|---|---|
| Motor independente da consulta relacional | `GET /api/games/search` consulta só o Elasticsearch (com cache no Redis) | `Catalog.Api/Infrastructure/Search/` |
| Serviço | Elasticsearch 8.15 no próprio AKS (StatefulSet, segurança habilitada); cliente REST compatível com Elastic Cloud e Amazon OpenSearch | `fcg-platform/k8s/elasticsearch.yaml` |
| Sincronização no CatalogAPI | Todo insert, edit, inativação, reativação e `PUT /details` reindexa o jogo na mesma requisição (`refresh=wait_for`); reindex completo no start se o índice estiver vazio e via `POST /api/games/search/reindex` | `GameSearchIndexer`, `GameDetailsService`, `SearchIndexInitializer` |
| **Fuzzy search** | `multi_match` com `fuzziness: AUTO` em título, tags, gênero, estúdio, plataformas e descrição | `ElasticQueryBuilder` |
| **Ordenação por relevância** | `sort: _score desc`, título com peso 4, frase exata e prefixo com boost; relevância e destaques na resposta | `ElasticQueryBuilder` |

Extras: analisador com `asciifolding` (acentos), filtros por gênero/tag/preço que não alteram a nota, fallback para o MongoDB quando o Elasticsearch está fora (campo `motor` na resposta) e `/api/games/search/basic` para comparar com a busca da Fase 3.

### 3.4 Automação de CI/CD e GitOps

**GitHub Actions**, uma pipeline para **cada um dos cinco projetos** (`.github/workflows/`, cada uma disparando só quando a pasta dela muda) (o mínimo exigido era UsersAPI e CatalogAPI).

| Etapa exigida | Implementação |
|---|---|
| Build & Test | `dotnet restore/build` em Release + `dotnet test` com xUnit; resultados e cobertura publicados como artefato |
| Containerização com tag | `docker build` com tag `<run>-<sha7>` (imutável por commit) + `latest` |
| Security scan (desejável) | **Trivy** sobre a imagem, HIGH/CRITICAL com correção disponível; bloqueante com `TRIVY_EXIT_CODE=1` |
| Push para o registry | `az acr login` via **OIDC** + `docker push` no ACR |
| Deploy sem downtime | `kustomize edit set image` + `kubectl apply -k`, `RollingUpdate` com `maxSurge: 1` / `maxUnavailable: 0`, readiness, `preStop`; `kubectl rollout status` e **rollback automático** em caso de falha |

`main` reflete produção: todo push na branch principal publica a imagem daquele commit e atualiza o Deployment. Pull requests rodam build, testes e scan, sem deploy.

**Testes unitários:** Users API (validação de credenciais, JWT, cadastro e login com SQLite em memória e broker substituído) e Catalog API (consulta do Elasticsearch, cliente HTTP com handler falso, serviço de busca com cache e fallback, indexador, cache distribuído, domínio).

A pipeline do `fcg-platform` cria os Secrets e aplica a infraestrutura compartilhada, e reprova se algum manifesto voltar a ter `kind: Secret`.

### 3.5 Segurança e gestão de segredos

| Exigência | Como foi atendida |
|---|---|
| Zero hardcoded credentials | Removidos: `secret.yaml` de todas as APIs, Secrets inline do Kong, Grafana e função, `Jwt:Key` e senhas do RabbitMQ dos `appsettings`, fallback `"guest"` dos `Program.cs`, senha de seed do `appsettings.Development.json`, chave padrão do Azurite e chaves padrão do `docker-compose.yml` |
| Injeção em tempo de execução | **Secrets do Kubernetes** criados pela pipeline (`k8s/secrets/create-secrets.sh`) a partir dos **GitHub Secrets**, consumidos por `secretKeyRef` |
| Credenciais da pipeline | **OIDC** (federated credentials): nenhum client secret existe |
| Serviços internos | MongoDB, Redis, RabbitMQ e Elasticsearch passaram a exigir autenticação |

---

## 4. Vídeo — o que é demonstrado

| Exigência | Momento do vídeo |
|---|---|
| Infra rodando na nuvem (painel da Azure e pods) | Bloco 2 |
| Live Deploy (alterar, push, pipeline atualizando produção) | Bloco 5 |
| Busca avançada | Bloco 3 |
| Impacto do cache na resposta | Bloco 4 |
| Integração do NoSQL | Bloco 4 |

Roteiro completo em `fcg-platform/fase4/ROTEIRO_VIDEO_FASE4.md`.

---

## 5. Como executar

Guia completo no README do `fcg-platform`, seção *Subindo na Azure (AKS)*. Resumo:

```powershell
git clone https://github.com/LMXSS/TC4.git
cd TC4\fcg-platform\infra\azure
.\setup-azure.ps1                 # Bicep (ACR + AKS) + OIDC + GitHub Secrets

# GitHub: Actions → Platform deploy → Run workflow
# GitHub: Actions → cada "CI/CD - ..." → Run workflow (ou push na main)

kubectl get svc kong-proxy        # IP público do gateway
```

---

## 6. Validação

`PREENCHER após o deploy` — sugestão de checklist:

| Verificação | Resultado |
|---|---|
| `kubectl get nodes` com 2 nós `Ready` | |
| Todos os pods `Running`/`Ready` | |
| Imagens com tag `<run>-<sha7>` no ACR | |
| Gateway responde no IP do Load Balancer | |
| Login do admin e cadastro de jogo | |
| Busca `?q=eldn rign` encontra *Elden Ring* (`motor: elasticsearch`) | |
| Busca simples (`/search/basic`) com o mesmo erro não encontra | |
| 2ª busca igual com `doCache: true` | |
| `/details`: 1ª chamada × 2ª chamada (ms) | |
| Live deploy: push → pipeline verde → versão nova respondendo sem erro durante o rollout | |
| Pipeline de PR sem deploy | |
| `grep -r "kind: Secret" */k8s` vazio | |
