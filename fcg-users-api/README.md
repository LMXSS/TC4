# FCG Users API

Cadastro, autenticação e autorização de usuários da **FIAP Cloud Games**. É o serviço que emite o JWT usado por toda a plataforma — inclusive pelo API Gateway, que valida o token na borda.

Na Fase 3 ganhou **instrumentação de métricas** no formato Prometheus e endpoints de health para as probes do Kubernetes.

Na **Fase 4** ganhou **testes unitários**, **pipeline de CI/CD** com deploy no AKS, **PostgreSQL** em produção (compartilhado entre as réplicas) e deixou de ter qualquer credencial no código.

## Responsabilidades

| Área | O quê |
|---|---|
| Autenticação | Registro, login e emissão de JWT (HS256) |
| Persistência | EF Core — PostgreSQL em produção, SQLite local e nos testes |
| Mensageria | Publica `UserCreatedEvent` no cadastro |
| Observabilidade | Expõe `/metrics` (OpenTelemetry) |

## Endpoints

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| `POST` | `/api/auth/register` | — | Cadastra o usuário, publica `UserCreatedEvent` e devolve o token |
| `POST` | `/api/auth/login` | — | Autentica e devolve o token |
| `GET` | `/health/live` | — | Processo de pé |
| `GET` | `/health/ready` | — | Banco respondendo |
| `GET` | `/metrics` | — | Métricas no formato Prometheus |

### O token

Assinado em **HS256** com `Jwt:Key`, contendo:

| Claim | Conteúdo |
|---|---|
| `sub` | Id do usuário |
| `email` | E-mail |
| `name` | Nome |
| `role` | `Usuario` ou `Administrador` |
| `iss` / `aud` | `Jwt:Issuer` / `Jwt:Audience` |

O claim `iss` é a chave que liga o token ao consumer configurado no Kong. **`Jwt:Key` precisa ser idêntica na Users API, na Catalog API e no gateway** — se divergir, o Kong rejeita todo token válido com *Invalid signature*.

## Variáveis de ambiente

| Variável | Padrão | Descrição |
|---|---|---|
| `Database__Provider` | `Sqlite` | `Postgres` em produção |
| `ConnectionStrings__DefaultConnection` | `Data Source=users.db` | Banco relacional |
| `Jwt__Key` | — *(obrigatória)* | Chave de assinatura, mínimo 32 caracteres |
| `Jwt__Issuer` / `Jwt__Audience` | `FCG` / `FCG` | Emissor e audiência |
| `Jwt__ExpiresMinutes` | `120` | Validade do token |
| `RabbitMq__Host` | `localhost` | Broker |
| `RabbitMq__Username` / `__Password` | — | Sem elas, usa o padrão do broker local |
| `Seed__AdminEmail` / `__AdminName` | `admin@fcg.local` / `Administrador` | Admin criado no start |
| `Seed__AdminPassword` | — | Senha do admin. Sem ela, nenhum admin é criado |

Nenhuma credencial tem valor padrão: em produção chegam do Secret `users-api-secret` (criado pela pipeline do `fcg-platform`); localmente, do `.env` do compose ou de `dotnet user-secrets`.

## Métricas expostas

Além de `http_server_request_duration_seconds` (latência, throughput e status code) e das métricas de runtime .NET:

| Métrica | Labels |
|---|---|
| `fcg_users_registered_total` | — |
| `fcg_users_logins_total` | `result` (success/failure) |

## Executando

### Stack completa (recomendado)

```powershell
cd ..\fcg-platform
docker compose up -d --build
```

Acesso pelo gateway: <http://localhost:8000/api/auth/login>.

### Somente este serviço

```powershell
docker run -d --name fcg-rabbit -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management-alpine

cd Users.Api
dotnet user-secrets set "Jwt:Key" "<chave com 32+ caracteres>"
dotnet user-secrets set "Seed:AdminPassword" "<senha com letra, numero e especial>"
dotnet run
```

Swagger em <http://localhost:5101/swagger>. O admin de seed é `admin@fcg.local`, com a senha definida acima.

### Docker

```powershell
# contexto = raiz do repositório
docker build -t fcg/users-api:latest . -f Users.Api/Dockerfile
```

## Testes

```powershell
dotnet test
```

[`Users.Api.Tests`](Users.Api.Tests) (xUnit + NSubstitute):

| Classe | O que garante |
|---|---|
| `CredentialValidationTests` | Formato de e-mail e regra de senha forte, com a mensagem de cada recusa |
| `JwtTokenGeneratorTests` | Token HS256 com `sub`, `email`, `name`, `role` e versão da credencial; não valida com outra chave; expiração mínima de 5 min |
| `AuthServiceTests` | Cadastro grava hash BCrypt e publica `UserCreatedEvent`; e-mail duplicado e dados inválidos são recusados; login certo devolve token e o errado responde 401 com a mesma mensagem (SQLite em memória, broker substituído por dublê) |
| `UsuarioTests` | Regras da entidade e perfis válidos |

## CI/CD

[`.github/workflows/users-api.yml`](../.github/workflows/users-api.yml): **Build & Test → imagem `<run>-<sha7>` → Trivy → push no ACR → `kubectl apply -k k8s/` com rolling update → rollback automático se o rollout falhar.** Detalhes no [README do fcg-platform](../fcg-platform/README.md#cicd-com-github-actions).

## Kubernetes

Manifestos em [`k8s/`](k8s) com Kustomize: `Deployment` (rolling update sem downtime), `Service`, `ConfigMap`, `HorizontalPodAutoscaler` e `kustomization.yaml`.

```powershell
kubectl apply -k k8s/
```

Não há `Secret` neste repositório: o `users-api-secret` é criado pela pipeline do `fcg-platform`.

## Serviços relacionados (pastas deste repositório)

| Repositório | Papel |
|---|---|
| [`fcg-platform`](../fcg-platform/) | Orquestração — comece por aqui |
| [`fcg-catalog-api`](../fcg-catalog-api/) | Consome o token emitido aqui |
| [`fcg-notifications-function`](../fcg-notifications-function/) | Consome `UserCreatedEvent` e envia as boas-vindas |
