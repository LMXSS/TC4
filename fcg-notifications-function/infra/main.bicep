// Infraestrutura como codigo da funcao serverless de notificacoes da FCG.
//
// Provisiona Storage Account, Application Insights, plano Elastic Premium e o Function App
// Linux (.NET 8 isolated) ja com as App Settings do trigger de RabbitMQ.
//
// O plano e Elastic Premium (EP1), e nao Consumption, porque triggers que nao sao HTTP
// dependem do runtime scale monitoring para escalar — recurso indisponivel no Consumption.
// Com functionsRuntimeScaleMonitoringEnabled a plataforma le a profundidade da fila do
// RabbitMQ e ajusta o numero de instancias, inclusive reduzindo quando a fila esvazia.

@description('Prefixo dos recursos. Precisa ser globalmente unico para o Function App.')
@minLength(3)
@maxLength(17)
param nomeBase string = 'fcgnotif'

@description('Regiao dos recursos.')
param localizacao string = resourceGroup().location

@description('Connection string AMQP do RabbitMQ (ex.: amqps://usuario:senha@host:5671/).')
@secure()
param rabbitMqConnection string

@description('Fila que recebe os eventos de usuario criado.')
param filaUserCreated string = 'notifications-user-created'

@description('Fila que recebe os eventos de pagamento processado.')
param filaPaymentProcessed string = 'notifications-payment-processed'

@description('Host do servidor SMTP usado para enviar os e-mails.')
param smtpHost string

@description('Porta do servidor SMTP.')
param smtpPort int = 587

@description('Usuario do servidor SMTP.')
param smtpUserName string = ''

@description('Senha do servidor SMTP.')
@secure()
param smtpPassword string = ''

@description('Remetente dos e-mails.')
param smtpFromAddress string = 'noreply@fcg.local'

var sufixo = uniqueString(resourceGroup().id)
var nomeStorage = toLower('${take(replace(nomeBase, '-', ''), 11)}${take(sufixo, 8)}')
var nomeFunctionApp = '${nomeBase}-${take(sufixo, 6)}'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: nomeStorage
  location: localizacao
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
  }
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${nomeBase}-logs'
  location: localizacao
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${nomeBase}-insights'
  location: localizacao
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

resource plano 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${nomeBase}-plan'
  location: localizacao
  sku: {
    name: 'EP1'
    tier: 'ElasticPremium'
  }
  kind: 'elastic'
  properties: {
    reserved: true
    maximumElasticWorkerCount: 10
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: nomeFunctionApp
  location: localizacao
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plano.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      // Necessario para escalar triggers que nao sao HTTP (RabbitMQ).
      functionsRuntimeScaleMonitoringEnabled: true
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: insights.properties.ConnectionString
        }
        {
          name: 'RabbitMqConnection'
          value: rabbitMqConnection
        }
        {
          name: 'RabbitMqUserCreatedQueue'
          value: filaUserCreated
        }
        {
          name: 'RabbitMqPaymentProcessedQueue'
          value: filaPaymentProcessed
        }
        {
          name: 'Smtp__Enabled'
          value: 'true'
        }
        {
          name: 'Smtp__Host'
          value: smtpHost
        }
        {
          name: 'Smtp__Port'
          value: string(smtpPort)
        }
        {
          name: 'Smtp__UseStartTls'
          value: 'true'
        }
        {
          name: 'Smtp__UserName'
          value: smtpUserName
        }
        {
          name: 'Smtp__Password'
          value: smtpPassword
        }
        {
          name: 'Smtp__FromAddress'
          value: smtpFromAddress
        }
        {
          name: 'Smtp__FromName'
          value: 'FIAP Cloud Games'
        }
      ]
    }
  }
}

output functionAppName string = functionApp.name
output functionAppHost string = functionApp.properties.defaultHostName
output applicationInsightsName string = insights.name
