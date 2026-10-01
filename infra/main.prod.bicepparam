// =============================================================================
// Parameterfil: PROD
//
// Prod är byggd för att tåla fredagseftermiddagen. Skillnaderna mot dev:
//
//   minReplicas        2 mot 1      — kravet i uppgiften, och ingen kallstart
//                                     för den första platschefen som laddar upp
//   maxReplicas        10 mot 3     — tar 500 bilder på fem minuter utan att kön byggs på
//   CPU/minne          0.5/1.0Gi    — dubbla dev, rymmer parallell bildanalys
//   Loggretention      90 mot 30 d  — kunder frågar om gamla inspektioner
//   Köskalning         på           — skalar på analysbacklog, inte bara HTTP
//   Larm               (på)         — 5xx-larm och kölängdslarm, se alertEmail nedan
//
// Kör:
//   az deployment group what-if -g <rg> -f infra/main.bicep -p infra/main.prod.bicepparam
// =============================================================================

using './main.bicep'

param namePrefix = 'guardly'
param environment = 'prod'

// Kursens Vision-resurs. Den ligger i en annan Entra-tenant, så Managed Identity
// kan inte få en token för den — nyckeln hämtas från Key Vault i stället.
// Endpointen är ingen hemlighet; nyckeln finns bara i Key Vault.
param visionEndpoint = 'https://cloud25ai-cv-9f3b0.cognitiveservices.azure.com/'
param visionAccountName = ''
param visionKeyFromKeyVault = true

// Får skriva hemligheter i Key Vault. Lägg till fler object-id vid behov:
//   az ad signed-in-user show --query id -o tsv
param keyVaultAdminObjectIds = [
  'dc036cf3-174b-446c-b539-7a08c791407c'
]

param visionFeatures = 'tags,objects,people'

param minReplicas = 2
param maxReplicas = 10
param containerCpu = '0.5'
param containerMemory = '1.0Gi'

param acrSku = 'Basic'
param logRetentionDays = 90

param enableQueueScaling = true

// Larmen (action group + två metric alerts) är definierade i main.bicep men
// kursprenumerationens policy nekar alla Microsoft.Insights-larmtyper. Tom
// adress = larmen hoppas över så att deployen går igenom. I en egen
// prenumeration: sätt en riktig adress så skapas de.
param alertEmail = ''

param assignRoles = true

param tags = {
  project: 'Guardly'
  course: 'NET-Cloud-ITHS'
  environment: 'prod'
  managedBy: 'Bicep'
  costCenter: 'drift'
}
