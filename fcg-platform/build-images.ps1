# Builda as 4 imagens fcg/*:latest usadas pelo docker-compose.yml e pelos manifestos k8s.
#
# O compose deste repositorio nao builda a partir do codigo-fonte: cada servico vive
# em seu proprio repositorio Git. Este script encontra esses repos e builda cada
# imagem a partir da RAIZ do repo correspondente.
#
# Fase 3: o notifications-api saiu (virou a funcao serverless) e entrou o
# fcg-notifications-function, cujo Dockerfile fica na raiz do repo, nao numa subpasta.
#
# Uso:
#   .\build-images.ps1                                  # detecta automaticamente
#   .\build-images.ps1 -ReposRoot D:\PosGrad\fcg-repos  # pasta com os 4 repos
#   .\build-images.ps1 -NoCache
#
# Layout esperado: <ReposRoot>\fcg-users-api\Users.Api\Dockerfile (e os demais).

param(
    [string]$ReposRoot,
    [switch]$NoCache
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "Docker nao encontrado no PATH. Instale o Docker Desktop e reabra o terminal." -ForegroundColor Red
    exit 1
}

$services = @(
    @{ Name = "Users.Api";       Repo = "fcg-users-api";              Dockerfile = "Users.Api/Dockerfile";    Tag = "fcg/users-api:latest" },
    @{ Name = "Catalog.Api";     Repo = "fcg-catalog-api";            Dockerfile = "Catalog.Api/Dockerfile";  Tag = "fcg/catalog-api:latest" },
    @{ Name = "Payments.Api";    Repo = "fcg-payments-api";           Dockerfile = "Payments.Api/Dockerfile"; Tag = "fcg/payments-api:latest" },
    @{ Name = "Notifications.Fn"; Repo = "fcg-notifications-function"; Dockerfile = "Dockerfile";              Tag = "fcg/notifications-function:latest" }
)

# Descobre a pasta que contem os repos de codigo-fonte.
$parent = Split-Path -Parent $PSScriptRoot

$candidates = @()
if ($ReposRoot) { $candidates += $ReposRoot }
$candidates += $parent

$rootPath = $null
foreach ($c in $candidates) {
    if (-not (Test-Path $c)) { continue }

    $ok = $true
    foreach ($svc in $services) {
        if (-not (Test-Path (Join-Path (Join-Path $c $svc.Repo) $svc.Dockerfile))) { $ok = $false; break }
    }
    if ($ok) { $rootPath = (Resolve-Path $c).Path; break }
}

if (-not $rootPath) {
    Write-Host "Nao encontrei o codigo-fonte dos servicos." -ForegroundColor Red
    Write-Host "Rode a partir do clone do TC4 (as pastas fcg-* ficam lado a lado) ou aponte a pasta:" -ForegroundColor Yellow
    Write-Host "  .\build-images.ps1 -ReposRoot C:\caminho\para\os\repos" -ForegroundColor Gray
    Write-Host "Esperado: <ReposRoot>\fcg-users-api\Users.Api\Dockerfile (e os outros 3)" -ForegroundColor Gray
    exit 1
}

Write-Host "Origem: $rootPath" -ForegroundColor Cyan

foreach ($svc in $services) {
    $context = Join-Path $rootPath $svc.Repo

    $buildArgs = @("build", "-f", $svc.Dockerfile, "-t", $svc.Tag)
    if ($NoCache) { $buildArgs += "--no-cache" }
    $buildArgs += "."

    Write-Host ""
    Write-Host ">> [$($svc.Name)] docker $($buildArgs -join ' ')" -ForegroundColor Cyan
    Write-Host "   contexto: $context" -ForegroundColor DarkGray

    Push-Location $context
    try {
        & docker @buildArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[ERRO] Falha ao buildar $($svc.Tag)" -ForegroundColor Red
            exit $LASTEXITCODE
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host ""
Write-Host "[OK] Imagens prontas:" -ForegroundColor Green
docker images --filter "reference=fcg/*" --format "  {{.Repository}}:{{.Tag}}  {{.Size}}"
Write-Host ""
Write-Host "Proximos passos:" -ForegroundColor Gray
Write-Host "  Docker:     docker compose up -d" -ForegroundColor Gray
Write-Host "  Kubernetes: .\deploy-k8s.ps1 -ReposRoot <pasta-dos-repos>" -ForegroundColor Gray
