# FCG Payments API

Processamento (simulado) dos pagamentos da **FIAP Cloud Games**. É um serviço puramente orientado a eventos: consome `OrderPlacedEvent`, decide o resultado e publica `PaymentProcessedEvent`.

Na Fase 3 ganhou **instrumentação de métricas** no formato Prometheus e endpoints de health para as probes do Kubernetes.

## Responsabilidades

| Área | O quê |
|---|---|
| Mensageria | Consome `OrderPlacedEvent`, publica `PaymentProcessedEvent` |
| Idempotência | Grava cada `OrderId` processado; reentrega não gera cobrança duplicada |
| Persistência | SQLite via EF Core |
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
| `ConnectionStrings__DefaultConnection` | `Data Source=payments.db` | SQLite |
| `RabbitMq__Host` / `__Username` / `__Password` | `localhost` / `guest` / `guest` | Broker |
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

## Kubernetes

Manifestos em [`k8s/`](k8s): `Deployment`, `Service`, `ConfigMap` e `Secret`. O HPA correspondente está em `fcg-platform/k8s/scaling/`.

```powershell
kubectl apply -f k8s/
```

Como o serviço é um consumidor de fila, escalar réplicas aumenta o paralelismo de processamento — a idempotência por `OrderId` garante que a mesma ordem não seja cobrada duas vezes.

## Repositórios relacionados

| Repositório | Papel |
|---|---|
| [`fcg-platform`](https://github.com/VHMoschini/fcg-platform) | Orquestração — comece por aqui |
| [`fcg-catalog-api`](https://github.com/VHMoschini/fcg-catalog-api) | Publica `OrderPlacedEvent` e consome o resultado |
| [`fcg-notifications-function`](https://github.com/VHMoschini/fcg-notifications-function) | Consome `PaymentProcessedEvent` e notifica o usuário |
