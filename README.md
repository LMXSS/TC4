# FCG Users API

Cadastro, autenticação e autorização de usuários da **FIAP Cloud Games**. É o serviço que emite o JWT usado por toda a plataforma — inclusive pelo API Gateway, que valida o token na borda.

Na Fase 3 ganhou **instrumentação de métricas** no formato Prometheus e endpoints de health para as probes do Kubernetes.

## Responsabilidades

| Área | O quê |
|---|---|
| Autenticação | Registro, login e emissão de JWT (HS256) |
| Persistência | SQLite via EF Core |
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
| `ConnectionStrings__DefaultConnection` | `Data Source=users.db` | SQLite |
| `Jwt__Key` | — | Chave de assinatura, mínimo 32 caracteres |
| `Jwt__Issuer` / `Jwt__Audience` | `FCG` / `FCG` | Emissor e audiência |
| `Jwt__ExpiresMinutes` | `120` | Validade do token |
| `RabbitMq__Host` / `__Username` / `__Password` | `localhost` / `guest` / `guest` | Broker |
| `Seed__AdminEmail` / `__AdminPassword` / `__AdminName` | — | Admin criado no start (apenas em Development) |

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
dotnet run
```

Swagger em <http://localhost:5101/swagger>. Em Development o admin de seed é `admin@fcg.local` / `Admin@123` (ver `appsettings.Development.json`).

### Docker

```powershell
# contexto = raiz do repositório
docker build -t fcg/users-api:latest . -f Users.Api/Dockerfile
```

## Kubernetes

Manifestos em [`k8s/`](k8s): `Deployment`, `Service`, `ConfigMap` e `Secret`.

```powershell
kubectl apply -f k8s/
```

O Deployment traz as anotações `prometheus.io/scrape` para entrar na coleta automática, e probes apontando para `/health/ready` e `/health/live`.

## Repositórios relacionados

| Repositório | Papel |
|---|---|
| [`fcg-platform`](https://github.com/VHMoschini/fcg-platform) | Orquestração — comece por aqui |
| [`fcg-catalog-api`](https://github.com/VHMoschini/fcg-catalog-api) | Consome o token emitido aqui |
| [`fcg-notifications-function`](https://github.com/VHMoschini/fcg-notifications-function) | Consome `UserCreatedEvent` e envia as boas-vindas |
