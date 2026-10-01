// =============================================================================
// Parameterfil: DEV
//
// Dev är byggd för att vara billig och snabb att riva. Skillnaderna mot prod:
//
//   minReplicas        2 i båda      — kravet gäller alla miljöer, inte bara prod
//   maxReplicas        3 mot 10     — taket skyddar budgeten
//   CPU/minne          0.25/0.5Gi   — halva prods storlek
//   Loggretention      30 mot 90 d  — mindre loggdata att betala för
//   Köskalning         av           — färre rörliga delar när man felsöker
//   Larm               av           — ingen mejlar driftjouren om dev
//
// Kör:
//   az deployment group what-if -g <rg> -f infra/main.bicep -p infra/main.dev.bicepparam
// =============================================================================

using './main.bicep'

param namePrefix = 'guardly'
param environment = 'dev'

// Fylls i av pipelinen. Lokalt: byt ut mot kursens riktiga endpoint.
param visionEndpoint = 'https://REPLACE-ME.cognitiveservices.azure.com/'

// Lämnas tom om Computer Vision-resursen ligger i en annan resursgrupp.
// Då får rollen Cognitive Services User tilldelas manuellt — se ARCHITECTURE.md.
param visionAccountName = ''

// Tre features = tre debiterade transaktioner per bild. I dev räcker taggar.
param visionFeatures = 'tags,objects,people'

param minReplicas = 2
param maxReplicas = 3
param containerCpu = '0.25'
param containerMemory = '0.5Gi'

param acrSku = 'Basic'
param logRetentionDays = 30

// Köskalning av i dev: en rörlig del mindre när man felsöker.
param enableQueueScaling = false

// Inga larm i dev.
param alertEmail = ''

param assignRoles = true

param tags = {
  project: 'Guardly'
  course: 'NET-Cloud-ITHS'
  environment: 'dev'
  managedBy: 'Bicep'
  costCenter: 'utbildning'
}
