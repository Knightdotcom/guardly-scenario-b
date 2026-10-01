// =============================================================================
// Guardly AB — infrastruktur
//
// Deployas i den BEFINTLIGA delade resursgruppen (targetScope = resourceGroup).
// Vi skapar alltså ingen egen resursgrupp, enligt förutsättningarna i uppgiften.
//
// Kör:
//   az deployment group what-if -g <rg> -f infra/main.bicep -p infra/main.dev.bicepparam
//   az deployment group create  -g <rg> -f infra/main.bicep -p infra/main.dev.bicepparam
// =============================================================================

targetScope = 'resourceGroup'

// -----------------------------------------------------------------------------
// Parametrar
// -----------------------------------------------------------------------------

@description('Kort prefix som alla resursnamn byggs av. Håll det kort — storage-konton får max 24 tecken.')
@minLength(3)
@maxLength(11)
param namePrefix string = 'guardly'

@description('Miljö. Styr SKU, antal replicas och loggretention.')
@allowed([ 'dev', 'prod' ])
param environment string = 'dev'

@description('Region. Ärver resursgruppens region som standard.')
param location string = resourceGroup().location

@description('Container-image att deploya. Första gången finns ingen egen image i ACR — då används Microsofts quickstart-image och pipelinen byter ut den.')
param containerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Endpoint till den Computer Vision-resurs som kursansvarig har skapat, t.ex. https://cv-iths.cognitiveservices.azure.com/')
param visionEndpoint string

@description('Namnet på den befintliga Computer Vision-resursen. Lämna tomt om den ligger i en annan resursgrupp — då får rolltilldelningen göras manuellt.')
param visionAccountName string = ''

@description('Vilka Computer Vision-features vi begär. OBS: Azure debiterar en transaktion per feature.')
param visionFeatures string = 'tags,objects,people'

@description('true = appen kör FakeVisionAnalyzer i stället för att anropa Computer Vision. Används i kursprenumerationen där vi saknar roll på kursansvarigs Vision-resurs.')
param visionUseFake bool = false

@description('true = appen autentiserar mot Computer Vision med en nyckel som hämtas från Key Vault. Behövs när Vision-resursen ligger i en annan Entra-tenant, där Managed Identity inte kan få en token. Hemligheten "vision-api-key" måste finnas i valvet innan detta slås på.')
param visionKeyFromKeyVault bool = false

@description('Object-id för de personer som ska få skriva hemligheter i Key Vault (az ad signed-in-user show --query id -o tsv).')
param keyVaultAdminObjectIds array = []

@description('Sätt till false om ditt konto saknar behörighet att skapa rolltilldelningar. Då måste en admin tilldela rollerna manuellt — se ARCHITECTURE.md.')
param assignRoles bool = true

@description('Skala även på köns längd, inte bara på HTTP-trafik. Kräver att managed identity får läsa kön.')
param enableQueueScaling bool = true

@description('Minsta antal replicas. Kravet i uppgiften är minst 2.')
@minValue(1)
param minReplicas int = 2

@description('Största antal replicas vid belastningstopp.')
@minValue(1)
param maxReplicas int = 10

@description('vCPU per replica. Container Apps kräver att CPU och minne följs åt.')
param containerCpu string = '0.5'

@description('Minne per replica.')
param containerMemory string = '1.0Gi'

@description('ACR-SKU. Basic räcker gott för en image.')
@allowed([ 'Basic', 'Standard', 'Premium' ])
param acrSku string = 'Basic'

@description('Hur länge loggar sparas i Log Analytics.')
@minValue(30)
@maxValue(730)
param logRetentionDays int = 30

@description('E-postadress som tar emot larm. Lämna tomt för att hoppa över action group och larm.')
param alertEmail string = ''

@description('Taggar på alla resurser.')
param tags object = {
  project: 'Guardly'
  course: 'NET-Cloud-ITHS'
  environment: environment
  managedBy: 'Bicep'
}

// -----------------------------------------------------------------------------
// Namn
// -----------------------------------------------------------------------------
// uniqueString ger ett deterministiskt suffix baserat på resursgruppens id.
// Samma indata ger alltid samma namn — det är en del av idempotensen.

var suffix = uniqueString(resourceGroup().id, namePrefix, environment)
var shortSuffix = substring(suffix, 0, 6)

var acrName = toLower('acr${namePrefix}${shortSuffix}')
var storageName = toLower('st${namePrefix}${environment}${shortSuffix}')
var identityName = 'id-${namePrefix}-${environment}'
var logAnalyticsName = 'log-${namePrefix}-${environment}'
var appInsightsName = 'appi-${namePrefix}-${environment}'
var environmentName = 'cae-${namePrefix}-${environment}'
var containerAppName = 'ca-${namePrefix}-api-${environment}'
var actionGroupName = 'ag-${namePrefix}-${environment}'
var keyVaultName = toLower('kv${namePrefix}${environment}${shortSuffix}')
var visionKeySecretName = 'vision-api-key'

var imageContainerName = 'images'
var inspectionContainerName = 'inspections'
var queueName = 'inspection-jobs'

// Inbyggda roller, identifierade med sina fasta GUID:n.
var roleIds = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  cognitiveServicesUser: 'a97b65f3-24c7-4388-baec-2e87135dc908'
}

// -----------------------------------------------------------------------------
// Managed Identity
// -----------------------------------------------------------------------------
// Vi använder en USER-assigned identity i stället för system-assigned. Skälet är
// konkret: Container App:en måste kunna hämta sin image från ACR redan när den
// skapas. Med en system-assigned identity finns identiteten inte förrän appen
// finns, och då kan den inte ha fått AcrPull än — moment 22. Med en user-assigned
// identity skapar vi identiteten först, ger den rollerna, och pekar sedan både
// image-hämtningen och applikationen på den.

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

// -----------------------------------------------------------------------------
// Azure Container Registry
// -----------------------------------------------------------------------------

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  tags: tags
  sku: {
    name: acrSku
  }
  properties: {
    // Admin-användaren är avstängd med flit. Den ger ett användarnamn och ett
    // lösenord som skulle behöva hanteras som en hemlighet — vi använder
    // managed identity i stället och har därmed ingen hemlighet att läcka.
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

// -----------------------------------------------------------------------------
// Storage Account: bilder, inspektionsdokument och kö
// -----------------------------------------------------------------------------

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: {
    // LRS i båda miljöerna. Kursprenumerationens policy (Allowed-SKUs) nekar
    // ZRS, annars hade prod speglats mot en andra zon.
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // Nyckelbaserad åtkomst är avstängd. Även om någon skulle få tag i en
    // kontonyckel går den inte att använda — allt måste gå via Entra ID.
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      // Soft delete: raderade blobbar går att återställa i 7 dagar.
      enabled: true
      days: 7
    }
  }
}

resource imageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: imageContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource inspectionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: inspectionContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource inspectionQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: queueName
}

// -----------------------------------------------------------------------------
// Key Vault: nyckeln till Computer Vision
// -----------------------------------------------------------------------------
// Kursens Vision-resurs ligger i en annan Entra-tenant. En managed identity kan
// bara få tokens i sin egen tenant, så där fungerar inte Managed Identity mot
// Vision. Nyckeln läggs därför i Key Vault, och Container App:en hämtar den med
// sin managed identity. Nyckeln finns aldrig i koden, i git eller i mallen.
//
// Åtkomstpolicyer i stället för RBAC: då behövs ingen rolltilldelning, som vårt
// konto inte får skapa i kursprenumerationen.

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: false
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    accessPolicies: concat(
      [
        {
          // Appen får bara läsa hemligheter, inget annat.
          tenantId: subscription().tenantId
          objectId: identity.properties.principalId
          permissions: {
            secrets: [ 'get' ]
          }
        }
      ],
      map(keyVaultAdminObjectIds, id => {
        tenantId: subscription().tenantId
        objectId: id
        permissions: {
          secrets: [ 'get', 'list', 'set' ]
        }
      })
    )
  }
}

// -----------------------------------------------------------------------------
// Log Analytics + Application Insights
// -----------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionDays
    workspaceCapping: {
      // Ett tak på hur mycket loggdata som får skickas in per dag.
      // Rent kostnadsskydd: en loggstorm ska inte kunna äta budgeten.
      dailyQuotaGb: environment == 'prod' ? 5 : 1
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
  }
}

// -----------------------------------------------------------------------------
// Rolltilldelningar
// -----------------------------------------------------------------------------
// Namnen är deterministiska GUID:n. Det är det som gör deployen idempotent:
// kör man om mallen skapas inte en ny tilldelning, den befintliga bekräftas bara.

resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRoles) {
  name: guid(acr.id, identity.id, roleIds.acrPull)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.acrPull)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRoles) {
  name: guid(storage.id, identity.id, roleIds.storageBlobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageBlobDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource queueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRoles) {
  name: guid(storage.id, identity.id, roleIds.storageQueueDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageQueueDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Computer Vision-resursen är skapad av kursansvarig och finns redan. Rollen
// tilldelas via en modul — se infra/modules/vision-role.bicep för varför.
// Ligger resursen i en annan resursgrupp: lämna visionAccountName tom och be
// kursansvarig tilldela rollen manuellt (kommandot finns i README.md).
module visionRole 'modules/vision-role.bicep' = if (assignRoles && !empty(visionAccountName)) {
  name: 'guardly-vision-role'
  params: {
    visionAccountName: visionAccountName
    principalId: identity.properties.principalId
  }
}

// -----------------------------------------------------------------------------
// Container Apps Environment
// -----------------------------------------------------------------------------

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    // 'azure-monitor' i stället för 'log-analytics'. Skillnaden är viktig:
    // alternativet 'log-analytics' kräver att man skickar med workspacets
    // sharedKey, som då hamnar i deployment-historiken i klartext. Med
    // 'azure-monitor' kopplas loggarna i stället via en diagnostic setting,
    // och ingen nyckel behöver hanteras alls. Det gör dessutom att
    // 'az deployment group what-if' fungerar rent, eftersom mallen inte
    // behöver anropa listKeys() på en resurs som ännu inte finns.
    appLogsConfiguration: {
      destination: 'azure-monitor'
    }
    zoneRedundant: false
  }
}

// Kopplar Container Apps-loggarna till vårt Log Analytics-workspace.
resource environmentDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: containerAppsEnvironment
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        // Allt appen skriver till stdout/stderr.
        category: 'ContainerAppConsoleLogs'
        enabled: true
      }
      {
        // Plattformens egna händelser: skalning, omstarter, misslyckade image-pulls.
        category: 'ContainerAppSystemLogs'
        enabled: true
      }
    ]
  }
}

// -----------------------------------------------------------------------------
// Skalningsregler
// -----------------------------------------------------------------------------
// Två regler som svarar på olika sorters last:
//
//  * HTTP-regeln tar hand om många samtidiga uppladdningar. Över 10 samtidiga
//    requests per replica startas en till.
//  * Köregeln tar hand om analysbacklogen. KEDA räknar meddelanden i kön och
//    siktar på 20 per replica. Det är den regeln som svarar på styrelse-
//    ordförandens fråga: 500 bilder på kön ger 500/20 = 25 önskade replicas,
//    som kapas till maxReplicas.
//
// Utan köregeln skulle appen skala ner direkt efter uppladdningarna — mitt i
// analysen — eftersom HTTP-trafiken då är noll.

var httpScaleRule = {
  name: 'http-concurrency'
  http: {
    metadata: {
      concurrentRequests: '10'
    }
  }
}

// OBS: 'identity' på en custom scale rule kräver API-version 2024-10-02-preview
// eller senare på containerApps. Med 2024-03-01 avvisas deployen med
// "Unknown properties identity in ContainerAppCustomScaleRule".
var queueScaleRule = {
  name: 'queue-depth'
  custom: {
    type: 'azure-queue'
    identity: identity.id
    metadata: {
      accountName: storage.name
      queueName: queueName
      queueLength: '20'
      cloud: 'AzurePublicCloud'
    }
  }
}

var scaleRules = enableQueueScaling ? [ httpScaleRule, queueScaleRule ] : [ httpScaleRule ]

// -----------------------------------------------------------------------------
// Container App
// -----------------------------------------------------------------------------

resource containerApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: [
        {
          server: acr.properties.loginServer
          // Ingen användare, inget lösenord — identiteten sköter inloggningen mot ACR.
          identity: identity.id
        }
      ]
      // Application Insights connection string är inte en hemlighet i egentlig
      // mening, men vi lägger den som secret ändå så att den inte visas i
      // klartext i portalens miljövariabellista. Vision-nyckeln är en referens
      // till Key Vault — värdet hämtas av plattformen med vår managed identity.
      secrets: concat(
        [
          {
            name: 'appinsights-connection-string'
            value: appInsights.properties.ConnectionString
          }
        ],
        visionKeyFromKeyVault ? [
          {
            name: 'vision-api-key'
            keyVaultUrl: '${keyVault.properties.vaultUri}secrets/${visionKeySecretName}'
            identity: identity.id
          }
        ] : []
      )
    }
    template: {
      containers: [
        {
          name: 'guardly-api'
          image: containerImage
          resources: {
            cpu: json(containerCpu)
            memory: containerMemory
          }
          env: concat(visionKeyFromKeyVault ? [
            {
              name: 'Vision__ApiKey'
              secretRef: 'vision-api-key'
            }
          ] : [], [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: environment == 'prod' ? 'Production' : 'Staging'
            }
            {
              name: 'Storage__AccountName'
              value: storage.name
            }
            {
              name: 'Storage__ImageContainer'
              value: imageContainerName
            }
            {
              name: 'Storage__InspectionContainer'
              value: inspectionContainerName
            }
            {
              name: 'Storage__QueueName'
              value: queueName
            }
            {
              name: 'Vision__Endpoint'
              value: visionEndpoint
            }
            {
              name: 'Vision__Features'
              value: visionFeatures
            }
            {
              name: 'Vision__UseFake'
              value: string(visionUseFake)
            }
            {
              // Talar om för DefaultAzureCredential vilken av maskinens
              // identiteter den ska använda.
              name: 'Azure__ManagedIdentityClientId'
              value: identity.properties.clientId
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              secretRef: 'appinsights-connection-string'
            }
            {
              // Färre samtidiga analyser i dev — vi delar Computer Vision-resurs
              // med resten av klassen och vill inte äta upp takgränsen.
              name: 'Worker__MaxConcurrentAnalyses'
              value: environment == 'prod' ? '3' : '1'
            }
          ])
          // Båda proberna pekar på /health, inte /health/ready. Det är medvetet:
          // /health/ready kollar även Computer Vision, och om rollen inte hunnit
          // få effekt skulle hela revisionen underkännas och deployen misslyckas.
          // /health svarar 200 så länge processen lever, vilket är precis vad en
          // liveness-probe ska mäta. Beroendena följer vi i stället via larm.
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 15
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: scaleRules
      }
    }
  }
  dependsOn: [
    // Rollerna måste finnas innan appen försöker hämta imagen från ACR.
    acrPullRole
    blobRole
    queueRole
    imageContainer
    inspectionContainer
    inspectionQueue
  ]
}

// -----------------------------------------------------------------------------
// Larm
// -----------------------------------------------------------------------------

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(alertEmail)) {
  name: actionGroupName
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'guardly'
    enabled: true
    emailReceivers: [
      {
        name: 'driftjour'
        emailAddress: empty(alertEmail) ? 'placeholder@example.com' : alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// Larm 1: ANTAL serverfel, inte andel.
//
// Vi mäter medvetet antal (fler än 5 stycken 5xx på fem minuter) och inte procent.
// Skälet är vår trafikprofil: vid 200 bilder om dagen kan fem minuter innehålla
// en enda request. Ett procentlarm hade då larmat vid 100 % felfrekvens på ett
// enda fel — larmet hade tjutit konstant utan att något var fel. Ett absolut tal
// är rätt mått vid låg volym.
//
// En riktig procentsats kräver en scheduledQueryRule som räknar
// countif(success == false) * 100.0 / count() över requests-tabellen i
// Application Insights. Det är rätt väg när volymen vuxit — tröskeln bör då vara
// 5 % med minst 20 requests i fönstret, så att en enstaka 500:a inte larmar.
resource failureRateAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = if (!empty(alertEmail)) {
  name: 'alert-${namePrefix}-${environment}-5xx'
  location: 'global'
  tags: tags
  properties: {
    description: 'Fler än 5 serverfel (HTTP 5xx) på fem minuter i Guardlys API. Absolut tal och inte procent — vid vår låga volym hade ett procentlarm larmat på ett enda fel. Kolla Application Insights innan kund hör av sig.'
    severity: 2
    enabled: true
    scopes: [ containerApp.id ]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'ServerErrors'
          metricNamespace: 'Microsoft.App/containerApps'
          metricName: 'Requests'
          operator: 'GreaterThan'
          threshold: 5
          timeAggregation: 'Total'
          criterionType: 'StaticThresholdCriterion'
          dimensions: [
            {
              name: 'statusCodeCategory'
              operator: 'Include'
              values: [ '5xx' ]
            }
          ]
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
    autoMitigate: true
  }
}

// Larm 2: kön växer. Byggs kön på ligger analysen efter — kunden ser inga resultat.
resource queueDepthAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = if (!empty(alertEmail)) {
  name: 'alert-${namePrefix}-${environment}-queue'
  location: 'global'
  tags: tags
  properties: {
    description: 'Fler än 200 meddelanden i analyskön. Antingen är det en topp, eller så har Computer Vision slutat svara.'
    severity: 3
    enabled: true
    // Kömetriken ligger på queueServices-resursen, inte på själva kontot.
    scopes: [ queueService.id ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'QueueDepth'
          metricNamespace: 'Microsoft.Storage/storageAccounts/queueServices'
          metricName: 'QueueMessageCount'
          operator: 'GreaterThan'
          threshold: 200
          timeAggregation: 'Average'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
    autoMitigate: true
  }
}

// -----------------------------------------------------------------------------
// Utdata
// -----------------------------------------------------------------------------

@description('Publik URL till API:et.')
output apiUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}'

@description('Swagger-adressen — kopiera den här till redovisningen.')
output swaggerUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}/swagger'

@description('Health check-adressen.')
output healthUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}/health'

@description('ACR:s inloggningsserver. Används av pipelinen.')
output acrLoginServer string = acr.properties.loginServer

@description('ACR:s namn.')
output acrName string = acr.name

@description('Container App:ens namn. Används av pipelinen vid deploy.')
output containerAppName string = containerApp.name

@description('Storage-kontots namn.')
output storageAccountName string = storage.name

@description('Managed identity-ns principal-id. Behövs om en admin ska tilldela rollen på Computer Vision manuellt.')
output managedIdentityPrincipalId string = identity.properties.principalId

@description('Managed identity-ns client-id.')
output managedIdentityClientId string = identity.properties.clientId

@description('Key Vault-namnet. Lägg Vision-nyckeln här: az keyvault secret set --vault-name <namn> --name vision-api-key ...')
output keyVaultName string = keyVault.name
