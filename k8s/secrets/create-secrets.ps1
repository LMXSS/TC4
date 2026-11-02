<#
.SYNOPSIS
  Cria os Secrets do Kubernetes num cluster local (Docker Desktop, Kind, Minikube).

.DESCRIPTION
  Equivalente local do create-secrets.sh usado pela pipeline. Gera senhas aleatorias
  (ou reaproveita as de .secrets.local.json, se existir) e aplica os mesmos Secrets,
  com os mesmos nomes e chaves. Nenhum valor fica em arquivo versionado.

  Para usar valores proprios, defina as variaveis de ambiente antes de rodar
  (JWT_KEY, POSTGRES_PASSWORD, ...) — as que existirem tem precedencia.
#>
$ErrorActionPreference = "Stop"

function New-Senha([int]$tamanho = 24) {
    $alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789".ToCharArray()
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes($tamanho)
    -join ($bytes | ForEach-Object { $alfabeto[$_ % $alfabeto.Length] })
}

$arquivo = Join-Path $PSScriptRoot "..\..\infra\azure\.secrets.local.json"
$salvas = if (Test-Path $arquivo) { Get-Content $arquivo | ConvertFrom-Json -AsHashtable } else { @{} }

$padroes = [ordered]@{
    JWT_KEY             = { New-Senha 48 }
    POSTGRES_PASSWORD   = { New-Senha }
    MONGO_PASSWORD      = { New-Senha }
    REDIS_PASSWORD      = { New-Senha }
    RABBITMQ_PASSWORD   = { New-Senha }
    ELASTIC_PASSWORD    = { New-Senha }
    GRAFANA_PASSWORD    = { New-Senha 16 }
    SEED_ADMIN_PASSWORD = { "Fcg#" + (New-Senha 12) + "7" }
    AZURITE_ACCOUNT_KEY = { [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(64)) }
}

$v = [ordered]@{}
foreach ($nome in $padroes.Keys) {
    $doAmbiente = [Environment]::GetEnvironmentVariable($nome)
    $v[$nome] = if ($doAmbiente) { $doAmbiente } elseif ($salvas[$nome]) { $salvas[$nome] } else { & $padroes[$nome] }
}
New-Item -ItemType Directory -Force -Path (Split-Path $arquivo) | Out-Null
$v | ConvertTo-Json | Set-Content -Path $arquivo -Encoding utf8

function Set-Secret($nome, [hashtable]$dados) {
    $argumentos = @("create", "secret", "generic", $nome, "--dry-run=client", "-o", "yaml")
    foreach ($k in $dados.Keys) { $argumentos += "--from-literal=$k=$($dados[$k])" }
    & kubectl @argumentos | kubectl apply -f -
    if ($LASTEXITCODE -ne 0) { throw "Falha ao aplicar o Secret $nome." }
}

$pg = { param($db) "Host=postgres;Port=5432;Database=$db;Username=fcg;Password=$($v.POSTGRES_PASSWORD)" }
$azurite = "DefaultEndpointsProtocol=http;AccountName=fcgstorage;AccountKey=$($v.AZURITE_ACCOUNT_KEY);" +
           "BlobEndpoint=http://azurite:10000/fcgstorage;QueueEndpoint=http://azurite:10001/fcgstorage;TableEndpoint=http://azurite:10002/fcgstorage"

Set-Secret postgres-secret      @{ username = "fcg"; password = $v.POSTGRES_PASSWORD }
Set-Secret mongo-secret         @{ username = "fcg"; password = $v.MONGO_PASSWORD }
Set-Secret redis-secret         @{ password = $v.REDIS_PASSWORD }
Set-Secret rabbitmq-secret      @{ username = "fcg"; password = $v.RABBITMQ_PASSWORD }
Set-Secret elasticsearch-secret @{ password = $v.ELASTIC_PASSWORD }
Set-Secret azurite-secret       @{ accounts = "fcgstorage:$($v.AZURITE_ACCOUNT_KEY)" }
Set-Secret grafana-secret       @{ "admin-user" = "admin"; "admin-password" = $v.GRAFANA_PASSWORD }
Set-Secret kong-secret          @{ "fcg-jwt-key" = $v.JWT_KEY }

Set-Secret users-api-secret @{
    "jwt-key"             = $v.JWT_KEY
    "connection-string"   = (& $pg "fcg_users")
    "rabbitmq-username"   = "fcg"
    "rabbitmq-password"   = $v.RABBITMQ_PASSWORD
    "seed-admin-password" = $v.SEED_ADMIN_PASSWORD
}
Set-Secret catalog-api-secret @{
    "jwt-key"                 = $v.JWT_KEY
    "connection-string"       = (& $pg "fcg_catalog")
    "rabbitmq-username"       = "fcg"
    "rabbitmq-password"       = $v.RABBITMQ_PASSWORD
    "mongo-connection-string" = "mongodb://fcg:$($v.MONGO_PASSWORD)@mongo:27017/?authSource=admin"
    "redis-connection-string" = "redis:6379,password=$($v.REDIS_PASSWORD),abortConnect=false"
    "elasticsearch-username"  = "elastic"
    "elasticsearch-password"  = $v.ELASTIC_PASSWORD
}
Set-Secret payments-api-secret @{
    "connection-string" = (& $pg "fcg_payments")
    "rabbitmq-username" = "fcg"
    "rabbitmq-password" = $v.RABBITMQ_PASSWORD
}
Set-Secret notifications-function-secret @{
    "rabbitmq-connection"   = "amqp://fcg:$($v.RABBITMQ_PASSWORD)@rabbitmq:5672/"
    "azure-webjobs-storage" = $azurite
}

Write-Host ""
Write-Host "Secrets aplicados. Senhas em $arquivo (fora do git)." -ForegroundColor Green
Write-Host "  Admin da FCG: admin@fcg.local / $($v.SEED_ADMIN_PASSWORD)"
Write-Host "  Grafana:      admin / $($v.GRAFANA_PASSWORD)"
