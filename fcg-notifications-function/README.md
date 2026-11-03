# FCG Notifications Function

Notificações da **FIAP Cloud Games** como função serverless — a migração da `NotificationsAPI` da Fase 2 (container .NET rodando 24/7) para uma **Azure Function** acionada diretamente por mensagens do RabbitMQ.

| | Fase 2 — `fcg-notifications-api` | Fase 3 — este repositório |
|---|---|---|
| Modelo | Container ASP.NET com `IHostedService` do MassTransit | Azure Function .NET 8 isolated |
| Ciclo de vida | Sempre ligado, ocioso na maior parte do tempo | Instanciada por mensagem, escala a zero quando a fila esvazia |
| Consumo | Consumers do MassTransit | `RabbitMQTrigger` nativo do host de Functions |
| Custo | 1 réplica reservada o tempo todo | Cobrança por execução / instância elástica |

## Funções

| Função | Fila | Evento | Ação |
|---|---|---|---|
| `UserCreatedFunction` | `notifications-user-created` | `UserCreatedEvent` | E-mail de boas-vindas |
| `PaymentProcessedFunction` | `notifications-payment-processed` | `PaymentProcessedEvent` | E-mail de compra confirmada ou de pagamento recusado |

### Por que existe um envelope a desempacotar

Os microsserviços publicam com **MassTransit**, que envolve a mensagem em um envelope (`messageId`, `messageType`, `message`, …) e a entrega em um **exchange fanout** com o nome do tipo do evento. O `RabbitMQTrigger` escuta uma **fila** e entrega o corpo bruto, então:

* o repositório de orquestração declara as filas `notifications-*` e as amarra aos exchanges do MassTransit (`fcg-platform/rabbitmq/definitions.json`);
* `Contracts/MassTransitEnvelope.cs` extrai o `message` do envelope — e ainda aceita um JSON solto, publicado à mão pelo painel do RabbitMQ, o que facilita testar a função isoladamente.

## Configuração

Todas as chaves são App Settings do Function App (ou variáveis de ambiente no container):

| Chave | Descrição | Exemplo |
|---|---|---|
| `RabbitMqConnection` | Connection string AMQP | `amqp://<usuario>:<senha>@rabbitmq:5672/` (Secret `notifications-function-secret` no cluster) |
| `RabbitMqUserCreatedQueue` | Fila de usuários criados | `notifications-user-created` |
| `RabbitMqPaymentProcessedQueue` | Fila de pagamentos processados | `notifications-payment-processed` |
| `AzureWebJobsStorage` | Storage do host de Functions (Azurite no local) | `UseDevelopmentStorage=true` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Telemetria na Azure | *(gerado pelo Bicep)* |
| `Smtp__Enabled` | `false` só registra no log, sem enviar | `true` |
| `Smtp__Host` / `Smtp__Port` | Servidor SMTP | `mailpit` / `1025` |
| `Smtp__UseStartTls` | STARTTLS (obrigatório em SMTP real) | `false` no Mailpit |
| `Smtp__UserName` / `Smtp__Password` | Credenciais SMTP | — |
| `Smtp__FromAddress` / `Smtp__FromName` | Remetente | `noreply@fcg.local` |

## Rodando localmente

### Junto com a stack completa (recomendado)

A função sobe como container no `docker-compose` do repositório de orquestração, com RabbitMQ, Azurite e Mailpit já configurados:

```powershell
cd ..\fcg-platform
docker compose up --build
```

Os e-mails chegam no Mailpit: <http://localhost:8025>.

### Com o Azure Functions Core Tools

```powershell
cd src\FcgNotifications.Functions
Copy-Item local.settings.example.json local.settings.json
func start
```

Requer RabbitMQ e Azurite acessíveis nos endereços do `local.settings.json`. `local.settings.json` está no `.gitignore` — nunca comite credenciais.

## Deploy na Azure

A infraestrutura está em [`infra/main.bicep`](infra/main.bicep): Storage Account, Log Analytics, Application Insights, plano **Elastic Premium (EP1)** e o Function App Linux .NET 8 isolated, já com as App Settings do trigger.

```powershell
cd infra
Copy-Item main.parameters.example.json main.parameters.json   # preencha os valores
az login
.\deploy.ps1 -ResourceGroup rg-fcg-fase3 -Location brazilsouth
```

**Por que Elastic Premium e não Consumption:** triggers que não são HTTP dependem do *runtime scale monitoring* para que a plataforma leia a profundidade da fila e decida escalar. Esse recurso não existe no plano Consumption, então o template habilita `functionsRuntimeScaleMonitoringEnabled` em um plano EP1.

O broker precisa estar acessível pelo Function App — RabbitMQ com endpoint público e TLS (`amqps://`), ou o Function App integrado à VNet onde o broker roda.

## Kubernetes e CI/CD (Fase 4)

O manifesto que roda a função no AKS fica em [`k8s/`](k8s) (com `kustomization.yaml`), e a pipeline [`.github/workflows/notifications-function.yml`](../.github/workflows/notifications-function.yml) faz build, imagem versionada, Trivy, push no ACR e rolling update no cluster a cada push na `master`. As credenciais (AMQP e storage do host) chegam pelo Secret `notifications-function-secret`, criado pela pipeline do `fcg-platform`.

```powershell
kubectl apply -k k8s/
```

## Estrutura

```
fcg-notifications-function/
├─ src/FcgNotifications.Functions/
│  ├─ Functions/          UserCreatedFunction, PaymentProcessedFunction
│  ├─ Contracts/          eventos e leitura do envelope do MassTransit
│  ├─ Email/              IEmailSender, SmtpEmailSender (MailKit) e templates
│  ├─ Program.cs          host isolated + Application Insights + DI
│  └─ host.json           prefetch do RabbitMQ e política de retry
├─ infra/                 Bicep + script de deploy
├─ Dockerfile             imagem do host de Functions v4
└─ global.json            SDK .NET 8 fixado
```

## Observabilidade

* **Na Azure:** logs, traces e métricas vão para o Application Insights provisionado pelo Bicep (`ConfigureFunctionsApplicationInsights`).
* **No ambiente local:** os logs saem no stdout do container e são coletados junto com os demais serviços da stack.
* Toda invocação registra o `InvocationId`, o identificador do evento e o destinatário, o que permite correlacionar a mensagem no RabbitMQ com o e-mail entregue no Mailpit.

## Serviços relacionados (pastas deste repositório)

| Repositório | Papel |
|---|---|
| [`fcg-platform`](../fcg-platform/) | Orquestração: compose, Kubernetes, Kong, Prometheus/Grafana |
| [`fcg-users-api`](../fcg-users-api/) | Publica `UserCreatedEvent` |
| [`fcg-catalog-api`](../fcg-catalog-api/) | Publica `OrderPlacedEvent` |
| [`fcg-payments-api`](../fcg-payments-api/) | Publica `PaymentProcessedEvent` |
