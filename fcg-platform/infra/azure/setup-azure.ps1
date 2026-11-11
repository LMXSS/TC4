<#
.SYNOPSIS
  Prepara tudo o que a Fase 4 precisa na Azure e no GitHub, de uma vez.

.DESCRIPTION
  1. Cria o resource group e aplica o main.bicep (ACR + AKS + Container Insights).
  2. Baixa as credenciais do cluster para o kubectl local.
  3. Cria uma identidade gerenciada para o GitHub Actions com federacao OIDC — sem client secret:
     as pipelines da branch main do repositorio trocam o token da propria execucao por
     um token da Azure. Permissoes minimas: AcrPush no ACR, Cluster User no AKS, Reader
     no resource group.
  4. Gera senhas aleatorias e grava tudo como GitHub Secrets / Variables do repositorio.
     As senhas tambem ficam em infra/azure/.secrets.local.json (fora do git) para voce
     consultar a senha do admin e do Grafana durante a demonstracao.

  Assinaturas Azure for Students so aceitam algumas regioes; descubra quais com:
    az policy assignment list --disable-scope-strict-match --query "[].parameters.listOfAllowedLocations.value" -o json

  Pre-requisitos: Azure CLI (az login feito), GitHub CLI (gh auth login feito) com
  permissao de admin no repositorio, e PowerShell 7+.

.EXAMPLE
  .\setup-azure.ps1
  .\setup-azure.ps1 -Location brazilsouth -GithubRepo outro-usuario/TC4
  .\setup-azure.ps1 -SkipGithub      # so a parte da Azure
  .\setup-azure.ps1 -Monitoramento   # liga o Container Insights (cobra por GB de log)
#>
param(
    [string]$ResourceGroup = "rg-fcg",
    [string]$Location = "chilecentral",
    [string]$NomeBase = "fcg",
    [string]$GithubRepo = "LMXSS/TC4",
    # Container Insights cobra por GB de log ingerido. Desligado por padrao para poupar o
    # credito do Azure for Students; os pods continuam visiveis no portal (AKS > Workloads).
    [switch]$Monitoramento,
    [switch]$SkipGithub
)

$ErrorActionPreference = "Stop"
# Faz az/gh/kubectl com exit code != 0 interromperem o script (PowerShell 7.3+), em vez
# de seguir adiante com variaveis vazias.
$PSNativeCommandUseErrorActionPreference = $true

# Monorepo: as cinco pipelines vivem no mesmo repositorio e fazem deploy a partir da main.
$branch = "main"

function Step($texto) { Write-Host ""; Write-Host ">> $texto" -ForegroundColor Cyan }

function New-Senha([int]$tamanho = 24) {
    # So letras e numeros: a senha entra em connection strings (Npgsql, Redis, AMQP) sem escapar nada.
    $alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789".ToCharArray()
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes($tamanho)
    -join ($bytes | ForEach-Object { $alfabeto[$_ % $alfabeto.Length] })
}

foreach ($cmd in @("az") + $(if ($SkipGithub) { @() } else { @("gh") })) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { throw "$cmd nao encontrado no PATH." }
}

$conta = az account show | ConvertFrom-Json
Write-Host "Assinatura: $($conta.name) ($($conta.id))" -ForegroundColor Gray

# ---------------------------------------------------------------- 1. Infra
Step "Resource group $ResourceGroup em $Location"
az group create --name $ResourceGroup --location $Location --output none

Step "Aplicando main.bicep (ACR + AKS). Leva de 5 a 10 minutos..."
$deploy = az deployment group create `
    --resource-group $ResourceGroup `
    --template-file (Join-Path $PSScriptRoot "main.bicep") `
    --parameters nomeBase=$NomeBase habilitarMonitoramento=$($Monitoramento.IsPresent.ToString().ToLower()) `
    --query properties.outputs | ConvertFrom-Json

$acrName = $deploy.acrName.value
$aksName = $deploy.aksName.value
Write-Host "ACR: $acrName  |  AKS: $aksName" -ForegroundColor Green

# ---------------------------------------------------------------- 2. kubectl
Step "Credenciais do cluster para o kubectl"
az aks get-credentials --resource-group $ResourceGroup --name $aksName --overwrite-existing
kubectl get nodes

if ($SkipGithub) {
    Write-Host "`n-SkipGithub: parte do GitHub ignorada." -ForegroundColor Yellow
    return
}

# ---------------------------------------------------------------- 3. OIDC
Step "Identidade do GitHub Actions (OIDC, sem client secret)"
# Identidade gerenciada (user-assigned) com credencial federada para o GitHub: o
# azure/login troca o token OIDC da execucao por um token desta identidade. Criar a
# identidade so exige permissao na assinatura (Azure RBAC), nao no Entra ID — funciona
# tambem em contas de estudante/convidadas, que nao podem registrar aplicativos.
$idName = "$NomeBase-github-actions"
$identidade = az identity create --name $idName --resource-group $ResourceGroup --location $Location | ConvertFrom-Json
$appId = $identidade.clientId
$spId = $identidade.principalId

# O subject do token OIDC do GitHub pode vir em dois formatos, conforme a configuracao
# do repositorio/organizacao: o classico (repo:dono/repo:ref:...) e o com IDs imutaveis
# (repo:dono@<id>/repo@<id>:ref:...). Cria uma credencial federada para cada um.
$repoInfo = gh api "repos/$GithubRepo" | ConvertFrom-Json
$dono, $repoNome = $GithubRepo.Split("/")
$subjects = [ordered]@{
    (($GithubRepo -replace "[^A-Za-z0-9-]", "-") + "-$branch")     = "repo:${GithubRepo}:ref:refs/heads/$branch"
    (($GithubRepo -replace "[^A-Za-z0-9-]", "-") + "-$branch-ids") = "repo:${dono}@$($repoInfo.owner.id)/${repoNome}@$($repoInfo.id):ref:refs/heads/$branch"
}
$existentes = az identity federated-credential list --identity-name $idName --resource-group $ResourceGroup --query "[].name" -o tsv
foreach ($nome in $subjects.Keys) {
    if ($existentes -notcontains $nome) {
        az identity federated-credential create `
            --name $nome `
            --identity-name $idName `
            --resource-group $ResourceGroup `
            --issuer "https://token.actions.githubusercontent.com" `
            --subject $subjects[$nome] `
            --audiences "api://AzureADTokenExchange" `
            --output none
    }
}
Write-Host "  identidade: $idName  |  federacao: $GithubRepo ($branch)"

$rgId = az group show --name $ResourceGroup --query id -o tsv
$acrId = az acr show --name $acrName --query id -o tsv
$aksId = az aks show --resource-group $ResourceGroup --name $aksName --query id -o tsv

foreach ($atribuicao in @(
        @{ Role = "Reader"; Scope = $rgId },
        @{ Role = "AcrPush"; Scope = $acrId },
        @{ Role = "Azure Kubernetes Service Cluster User Role"; Scope = $aksId })) {
    # Identidade recem-criada pode levar alguns segundos para replicar: tenta ate 6 vezes.
    $ok = $false
    for ($tentativa = 1; $tentativa -le 6 -and -not $ok; $tentativa++) {
        try {
            az role assignment create --assignee-object-id $spId --assignee-principal-type ServicePrincipal `
                --role $atribuicao.Role --scope $atribuicao.Scope --output none
            $ok = $true
        } catch {
            if ($tentativa -eq 6) { throw }
            Start-Sleep -Seconds 10
        }
    }
    Write-Host "  role: $($atribuicao.Role)"
}

# ---------------------------------------------------------------- 4. GitHub
Step "Senhas da plataforma"
$arquivoSenhas = Join-Path $PSScriptRoot ".secrets.local.json"
if (Test-Path $arquivoSenhas) {
    $senhas = Get-Content $arquivoSenhas | ConvertFrom-Json -AsHashtable
    Write-Host "  reaproveitando $arquivoSenhas"
} else {
    $senhas = [ordered]@{
        JWT_KEY             = New-Senha 48
        POSTGRES_PASSWORD   = New-Senha
        MONGO_PASSWORD      = New-Senha
        REDIS_PASSWORD      = New-Senha
        RABBITMQ_PASSWORD   = New-Senha
        ELASTIC_PASSWORD    = New-Senha
        GRAFANA_PASSWORD    = New-Senha 16
        # Precisa passar na regra de senha forte da Users API: letra, numero e especial.
        SEED_ADMIN_PASSWORD = "Fcg#" + (New-Senha 12) + "7"
        AZURITE_ACCOUNT_KEY = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(64))
    }
    $senhas | ConvertTo-Json | Set-Content -Path $arquivoSenhas -Encoding utf8
    Write-Host "  geradas e salvas em $arquivoSenhas (no .gitignore)"
}

Step "GitHub Secrets e Variables em $GithubRepo"
gh secret set AZURE_CLIENT_ID       --repo $GithubRepo --body $appId
gh secret set AZURE_TENANT_ID       --repo $GithubRepo --body $conta.tenantId
gh secret set AZURE_SUBSCRIPTION_ID --repo $GithubRepo --body $conta.id
gh variable set ACR_NAME            --repo $GithubRepo --body $acrName
gh variable set AKS_RESOURCE_GROUP  --repo $GithubRepo --body $ResourceGroup
gh variable set AKS_CLUSTER_NAME    --repo $GithubRepo --body $aksName

# Senhas da plataforma: usadas pela pipeline "Platform deploy" para criar os Secrets do Kubernetes.
foreach ($nome in $senhas.Keys) {
    gh secret set $nome --repo $GithubRepo --body $senhas[$nome]
}
Write-Host "  Azure (OIDC), registry, cluster e $($senhas.Count) senhas gravados"

Write-Host ""
Write-Host "Pronto. Proximos passos:" -ForegroundColor Green
Write-Host "  1. GitHub > Actions > 'Platform deploy' > Run workflow."
Write-Host "  2. GitHub > Actions > cada 'CI/CD - ...' > Run workflow (publica as imagens e faz o deploy)."
Write-Host "  3. IP publico do gateway: kubectl get svc kong-proxy"
Write-Host "  Admin da FCG: admin@fcg.local / $($senhas.SEED_ADMIN_PASSWORD)"
Write-Host "  Grafana:      admin / $($senhas.GRAFANA_PASSWORD)  (kubectl port-forward svc/grafana 3000:3000)"
