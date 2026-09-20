// =============================================================================
// Ger Guardlys managed identity rollen Cognitive Services User på den BEFINTLIGA
// Computer Vision-resurs som kursansvarig har skapat.
//
// Varför en egen modul? Resursen deklareras här som 'existing' utan villkor, och
// hela modulen deployas villkorligt från main.bicep. Det är renare än ett villkor
// på själva 'existing'-resursen — Bicep tillåter inte att man pekar en scope på
// en resurs som kanske inte finns, och what-if blir betydligt mer lättläst.
// =============================================================================

targetScope = 'resourceGroup'

@description('Namnet på den befintliga Computer Vision-resursen.')
param visionAccountName string

@description('Principal-id för den managed identity som ska få rollen.')
param principalId string

// Rollen Cognitive Services User. Den ger rätt att ANVÄNDA tjänsten (anropa
// Analyze Image) men inte att läsa ut nycklarna eller ändra resursen — precis
// så lite behörighet som applikationen behöver.
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'

resource visionAccount 'Microsoft.CognitiveServices/accounts@2023-05-01' existing = {
  name: visionAccountName
}

resource visionRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // Deterministiskt GUID: samma indata ger samma namn, så en omkörning skapar
  // ingen dubblett utan bekräftar bara den befintliga tilldelningen.
  name: guid(visionAccount.id, principalId, cognitiveServicesUserRoleId)
  scope: visionAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Id för rolltilldelningen, mest för spårbarhet i deploy-utdatan.')
output roleAssignmentId string = visionRole.id
