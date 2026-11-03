# Aplica num cluster Kubernetes (local ou o AKS do kubectl atual) a stack completa:
# Secrets, infraestrutura, as 3 APIs, a funcao serverless, o gateway Kong e a
# observabilidade.
#
# Fase 4: na nuvem quem faz isso e a pipeline (.github/workflows/platform.yml na raiz do
# repositorio, mais a pipeline de cada servico). Este script continua servindo para o cluster local.
#
# Os manifestos das APIs vivem na pasta k8s/ da RAIZ de cada repositorio de API
# (nao neste repo), entao o script precisa saber onde eles estao. Os manifestos de
# infraestrutura, da funcao, do gateway e da observabilidade sao deste repositorio.
#
# Uso:
#   .\deploy-k8s.ps1                                  # detecta automaticamente
#   .\deploy-k8s.ps1 -ReposRoot D:\PosGrad\fcg-repos  # pasta com os repos de API
#   .\deploy-k8s.ps1 -Delete                          # remove tudo do cluster
#
# Pre-requisitos:
#   - Kubernetes habilitado (Docker Desktop -> Settings -> Kubernetes)
#   - Imagens fcg/*:latest ja buildadas (.\build-images.ps1)
#   - Os Secrets sao criados pelo k8s\secrets\create-secrets.ps1 na primeira execucao,
#     com senhas aleatorias (e a mesma jwt-key em Users, Catalog e Kong).

param(
    [string]$ReposRoot,
    [switch]$Delete
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
    Write-Host "kubectl nao encontrado no PATH." -ForegroundColor Red
    exit 1
}

# Cada servico e dono dos seus manifestos (pasta k8s/ com kustomization.yaml),
# aplicados com kubectl apply -k.
$services = @(
    @{ Api = "Users.Api";        Repo = "fcg-users-api" },
    @{ Api = "Catalog.Api";      Repo = "fcg-catalog-api" },
    @{ Api = "Payments.Api";     Repo = "fcg-payments-api" },
    @{ Api = "Notifications.Fn"; Repo = "fcg-notifications-function" }
)

# Descobre a pasta que contem os repos de API.
$parent = Split-Path -Parent $PSScriptRoot

$candidates = @()
if ($ReposRoot) { $candidates += $ReposRoot }
$candidates += $parent

$rootPath = $null
foreach ($c in $candidates) {
    if (-not (Test-Path $c)) { continue }

    $ok = $true
    foreach ($svc in $services) {
        if (-not (Test-Path (Join-Path (Join-Path $c $svc.Repo) "k8s"))) { $ok = $false; break }
    }
    if ($ok) { $rootPath = (Resolve-Path $c).Path; break }
}

if (-not $rootPath) {
    Write-Host "Nao encontrei as pastas k8s/ das APIs." -ForegroundColor Red
    Write-Host "Rode a partir do clone do TC4 (as pastas fcg-* ficam lado a lado) ou aponte a pasta:" -ForegroundColor Yellow
    Write-Host "  .\deploy-k8s.ps1 -ReposRoot C:\caminho\para\os\repos" -ForegroundColor Gray
    Write-Host "Esperado: <ReposRoot>\fcg-users-api\k8s\ (e os outros 3)" -ForegroundColor Gray
    exit 1
}

$verb = if ($Delete) { "delete" } else { "apply" }

Write-Host "Origem dos manifestos das APIs: $rootPath" -ForegroundColor Cyan
Write-Host "Operacao: kubectl $verb" -ForegroundColor Cyan

function Invoke-Kubectl($path, $label, [switch]$Kustomize) {
    $flag = if ($Kustomize) { "-k" } else { "-f" }
    Write-Host ""
    Write-Host ">> [$label] kubectl $verb $flag $path" -ForegroundColor Cyan
    & kubectl $verb $flag $path
    if ($LASTEXITCODE -ne 0 -and -not $Delete) { exit $LASTEXITCODE }
}

# Os ConfigMaps do Kong e do dashboard do Grafana sao gerados a partir dos mesmos
# arquivos que o compose usa — nada e duplicado a mao.
if (-not $Delete) {
    $temSecrets = kubectl get secret catalog-api-secret --ignore-not-found -o name
    if (-not $temSecrets) {
        Write-Host ""
        Write-Host ">> Secrets ainda nao existem: gerando senhas aleatorias" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "k8s\secrets\create-secrets.ps1")
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    Write-Host ""
    Write-Host ">> ConfigMaps gerados de gateway\kong.yml e do dashboard do Grafana" -ForegroundColor Cyan

    $kongYml = Join-Path $PSScriptRoot "gateway\kong.yml"
    kubectl create configmap kong-declarative --from-file=kong.yml=$kongYml --dry-run=client -o yaml | kubectl apply -f -
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $dashboard = Join-Path $PSScriptRoot "observability\grafana\dashboards\fcg-overview.json"
    kubectl create configmap grafana-dashboards --from-file=fcg-overview.json=$dashboard --dry-run=client -o yaml | kubectl apply -f -
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Infra primeiro: os servicos dependem de postgres, rabbitmq, mongo, redis,
# elasticsearch, mailpit e azurite por nome de Service.
foreach ($f in @("postgres.yaml", "rabbitmq.yaml", "mongo.yaml", "redis.yaml", "elasticsearch.yaml", "mailpit.yaml", "azurite.yaml")) {
    Invoke-Kubectl (Join-Path $PSScriptRoot "k8s\$f") "infra"
}

foreach ($svc in $services) {
    Invoke-Kubectl (Join-Path (Join-Path $rootPath $svc.Repo) "k8s") $svc.Api -Kustomize
}

Invoke-Kubectl (Join-Path $PSScriptRoot "k8s\gateway")                     "gateway"
Invoke-Kubectl (Join-Path $PSScriptRoot "k8s\observability")               "observabilidade"

# Os HPAs sao aplicados um a um, e nao pela pasta: um Deployment que caia em
# k8s\scaling\ por engano sobrescreveria o Deployment real do servico homonimo.
# O HPA depende do metrics-server; sem ele o apply passa mas o autoscaling fica ocioso.
foreach ($f in @("hpa-payments-api.yaml", "hpa-notifications-function.yaml")) {
    Invoke-Kubectl (Join-Path $PSScriptRoot "k8s\scaling\$f") "escala"
}

if ($Delete) {
    kubectl delete configmap kong-declarative grafana-dashboards --ignore-not-found
    Write-Host ""
    Write-Host "[OK] Recursos removidos do cluster." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "[OK] Manifestos aplicados. Acompanhe os pods:" -ForegroundColor Green
Write-Host "  kubectl get pods -w" -ForegroundColor Gray
Write-Host ""
kubectl get pods
Write-Host ""
Write-Host "Acesso:" -ForegroundColor Gray
Write-Host "  API Gateway: http://localhost  (Service LoadBalancer; na nuvem: kubectl get svc kong-proxy)" -ForegroundColor Gray
Write-Host "  Prometheus:  http://localhost:30090" -ForegroundColor Gray
Write-Host "  Grafana:     http://localhost:30300" -ForegroundColor Gray
Write-Host ""
Write-Host "Sob demanda (port-forward, um por terminal):" -ForegroundColor Gray
Write-Host "  kubectl port-forward svc/mailpit 8025:8025" -ForegroundColor Gray
Write-Host "  kubectl port-forward svc/rabbitmq 15672:15672" -ForegroundColor Gray
