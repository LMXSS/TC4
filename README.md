# FCG Payments API

Processamento (simulado) dos pagamentos da **FIAP Cloud Games**. É um serviço puramente orientado a eventos: consome `OrderPlacedEvent`, decide o resultado e publica `PaymentProcessedEvent`.

Na Fase 3 ganhou **instrumentação de métricas** no formato Prometheus e endpoints de health para as probes do Kubernetes.

## Responsabilidades

| Área | O quê |
|---|---|
| Mensageria | Consome `OrderPlacedEvent`, publica `PaymentProcessedEvent` |
| Idempotência | Grava cada `OrderId` processado; reentrega não gera cobrança duplicada |
| Persistência | EF Core — PostgreSQL em produção (Fase 4), SQLite local |
| Observabilidade | Expõe `/metrics` (OpenTelemetry) |

## Simulação do pagamento

Configurável por `Payments__*`:

| Modo | Comportamento |
|---|---|
| `Random` *(padrão)* | Aprova conforme `ApprovalRate` |
| `AlwaysApprove` | Aprova sempre |
| `AlwaysReject` | Recusa sempre |

`Payments__RejectPrices__0` lista preços que **sempre** reprovam — o caminho mais simples para demonstrar o fluxo de recusa: crie um jogo custando `99.99` e compre.

## Endpoints

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/api/payments` | Pedidos processados |
| `GET` | `/health` · `/health/live` | Processo de pé |
| `GET` | `/health/ready` | Banco respondendo |
| `GET` | `/metrics` | Métricas no formato Prometheus |

Este serviço **não é exposto pelo gateway**: ele participa da arquitetura apenas pela fila. Os endpoints HTTP existem para diagnóstico e coleta de métricas.

## Variáveis de ambiente

| Variável | Padrão | Descrição |
|---|---|---|
| `Database__Provider` | `Sqlite` | `Postgres` em produção |
| `ConnectionStrings__DefaultConnection` | `Data Source=payments.db` | Banco relacional |
| `RabbitMq__Host` | `localhost` | Broker |
| `RabbitMq__Username` / `__Password` | — | Sem elas, usa o padrão do broker local |
| `Payments__SimulationMode` | `Random` | `Random`, `AlwaysApprove` ou `AlwaysReject` |
| `Payments__ApprovalRate` | `0.92` | Taxa de aprovação no modo `Random` |
| `Payments__RejectPrices__0` | `99.99` | Preço que sempre reprova |

## Métricas expostas

Além de `http_server_request_duration_seconds` e das métricas de runtime .NET:

| Métrica | Labels |
|---|---|
| `fcg_payments_processed_total` | `status` (approved/rejected) |
| `fcg_payments_amount` | `status` — histograma do valor dos pedidos |

## Executando

### Stack completa (recomendado)

```powershell
cd ..\fcg-platform
docker compose up -d --build
```

### Somente este serviço

```powershell
docker run -d --name fcg-rabbit -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management-alpine

cd Payments.Api
dotnet run
```

Swagger em <http://localhost:5103/swagger>.

### Docker

```powershell
# contexto = raiz do repositório
docker build -t fcg/payments-api:latest . -f Payments.Api/Dockerfile
```

## CI/CD (Fase 4)

[`.github/workflows/ci-cd.yml`](.github/workflows/ci-cd.yml): build, imagem versionada, Trivy, push no ACR e rolling update no AKS. Detalhes no [README do fcg-platform](https://github.com/VHMoschini/fcg-platform#cicd-com-github-actions).

## Kubernetes

Manifestos em [`k8s/`](k8s) com Kustomize: `Deployment`, `Service`, `ConfigMap` e `kustomization.yaml`. O HPA está em `fcg-platform/k8s/scaling/`; o `payments-api-secret` é criado pela pipeline do `fcg-platform`.

```powershell
kubectl apply -k k8s/
```

## Repositórios relacionados

| Repositório | Papel |
|---|---|
| [`fcg-platform`](https://github.com/VHMoschini/fcg-platform) | Orquestração — comece por aqui |
| [`fcg-catalog-api`](https://github.com/VHMoschini/fcg-catalog-api) | Publica `OrderPlacedEvent` e consome o resultado |
| [`fcg-notifications-function`](https://github.com/VHMoschini/fcg-notifications-function) | Consome `PaymentProcessedEvent` e notifica o usuário |
