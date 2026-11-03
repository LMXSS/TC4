// Infraestrutura da FCG na Azure (Fase 4).
//
// Provisiona, num resource group:
//   * Azure Container Registry (ACR) — registry privado das imagens, sem usuario admin;
//   * Azure Kubernetes Service (AKS) — cluster gerenciado onde roda toda a plataforma;
//   * Log Analytics + Container Insights — logs e metricas dos nos e pods no portal;
//   * permissao AcrPull da identidade dos nos do AKS sobre o ACR, para que o cluster
//     baixe as imagens privadas sem nenhuma senha de registry.
//
// Deploy: ./deploy-infra.ps1 (ou az deployment group create -f main.bicep).

@description('Prefixo dos recursos (3 a 10 letras minusculas).')
@minLength(3)
@maxLength(10)
param nomeBase string = 'fcg'

@description('Regiao dos recursos.')
param localizacao string = resourceGroup().location

@description('Tamanho das VMs dos nos. B2ms (2 vCPU, 8 GB) comporta Elasticsearch, bancos e APIs com folga.')
param tamanhoNo string = 'Standard_B2ms'

@description('Quantidade de nos do pool.')
@minValue(1)
@maxValue(5)
param quantidadeNos int = 2

@description('Versao do Kubernetes. Vazio usa a versao padrao do AKS na regiao.')
param versaoKubernetes string = ''

@description('Habilita o Container Insights (logs e metricas dos pods no portal da Azure).')
param habilitarMonitoramento bool = true

var sufixo = uniqueString(resourceGroup().id)
var nomeAcr = toLower('${nomeBase}acr${sufixo}')
var nomeAks = '${nomeBase}-aks'
var nomeLogs = '${nomeBase}-logs-${sufixo}'

// AcrPull — role interna da Azure, mesmo id em todo tenant.
var roleAcrPull = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: nomeAcr
  location: localizacao
  sku: {
    name: 'Basic'
  }
  properties: {
    // Sem usuario/senha de registry: o AKS usa a identidade gerenciada e a pipeline usa OIDC.
    adminUserEnabled: false
  }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (habilitarMonitoramento) {
  name: nomeLogs
  location: localizacao
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource aks 'Microsoft.ContainerService/managedClusters@2024-02-01' = {
  name: nomeAks
  location: localizacao
  sku: {
    name: 'Base'
    tier: 'Free'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    dnsPrefix: '${nomeBase}-${sufixo}'
    kubernetesVersion: empty(versaoKubernetes) ? null : versaoKubernetes
    enableRBAC: true
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        count: quantidadeNos
        vmSize: tamanhoNo
        osType: 'Linux'
        type: 'VirtualMachineScaleSets'
        osDiskSizeGB: 64
      }
    ]
    networkProfile: {
      // Azure CNI em modo overlay: pods em faixa propria, sem consumir IPs da VNet.
      networkPlugin: 'azure'
      networkPluginMode: 'overlay'
      loadBalancerSku: 'standard'
    }
    addonProfiles: habilitarMonitoramento ? {
      omsagent: {
        enabled: true
        config: {
          logAnalyticsWorkspaceResourceID: logs.id
        }
      }
    } : {}
  }
}

// Os nos do AKS (kubelet identity) podem baixar imagens do ACR.
resource aksAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, aks.id, 'acrpull')
  scope: acr
  properties: {
    roleDefinitionId: roleAcrPull
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

output acrName string = acr.name
output acrLoginServer string = acr.properties.loginServer
output aksName string = aks.name
output resourceGroup string = resourceGroup().name
