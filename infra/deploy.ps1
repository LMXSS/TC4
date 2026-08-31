<#
.SYNOPSIS
    Provisiona a infraestrutura e publica a funcao de notificacoes da FCG na Azure.

.DESCRIPTION
    1. Cria (ou reutiliza) o resource group.
    2. Aplica o template Bicep com os parametros informados.
    3. Publica o codigo da funcao no Function App criado.

    Pre-requisitos: Azure CLI autenticado (az login) e Azure Functions Core Tools v4.

.EXAMPLE
    .\deploy.ps1 -ResourceGroup rg-fcg-fase3 -Location brazilsouth -ParametersFile .\main.parameters.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [string]$Location = 'brazilsouth',
    [string]$ParametersFile = "$PSScriptRoot\main.parameters.json"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ParametersFile)) {
    throw "Arquivo de parametros nao encontrado: $ParametersFile. Copie main.parameters.example.json e preencha os valores."
}

Write-Host "==> Resource group $ResourceGroup ($Location)" -ForegroundColor Cyan
az group create --name $ResourceGroup --location $Location --output none

Write-Host '==> Aplicando o template Bicep' -ForegroundColor Cyan
$saida = az deployment group create `
    --resource-group $ResourceGroup `
    --template-file "$PSScriptRoot\main.bicep" `
    --parameters "@$ParametersFile" `
    --query properties.outputs `
    --output json | ConvertFrom-Json

$functionApp = $saida.functionAppName.value
Write-Host "==> Function App provisionado: $functionApp" -ForegroundColor Green

Write-Host '==> Publicando o codigo da funcao' -ForegroundColor Cyan
Push-Location "$PSScriptRoot\..\src\FcgNotifications.Functions"
try {
    func azure functionapp publish $functionApp --dotnet-isolated
}
finally {
    Pop-Location
}

Write-Host "==> Concluido. Logs em: Application Insights de $($saida.applicationInsightsName.value)" -ForegroundColor Green
