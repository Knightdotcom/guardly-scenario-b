# Guardly Inspection API

Automatisk säkerhetsinspektion av byggplatsfoton. Ladda upp en bild, få tillbaka taggar,
konfidenspoäng och varningar om skyddsutrustning saknas.

Scenario B — .NET Cloud Developer, ITHS Göteborg. Byggt av Can "Knight" Öz.

| Dokument | Innehåll |
|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Tekniska val, ekonomianalys, reflektionsfrågorna |
| [`RAPPORT.md`](RAPPORT.md) | Kundrapport till Guardly AB |
| [`REFLEKTION_Can_Oz.md`](REFLEKTION_Can_Oz.md) | Individuell reflektion |
| [`docs/ROLLBACK.md`](docs/ROLLBACK.md) | Hur man rullar tillbaka en deploy |
| [`docs/PRESENTATION.md`](docs/PRESENTATION.md) | Talmanus för redovisningen |

---

## Kör lokalt — utan Azure-konto

Projektet startar i ett läge med lagring i minnet och en fejkad bildanalysator. Hela
flödet går alltså att testa utan att någonting kostar pengar.

```bash
cd src/Guardly.Api
dotnet run
```

Öppna **http://localhost:5080/swagger**.

Testa med curl:

```bash
# Ladda upp en bild
curl -F "image=@byggplats.jpg" \
     -F "siteId=kvarteret-vallgatan" \
     -F "zone=Plan 3" \
     http://localhost:5080/inspections

# Svar: {"id":"8f3a...","status":"Queued","statusUrl":"/inspections/8f3a...?siteId=..."}

# Hämta resultatet (vänta någon sekund)
curl http://localhost:5080/inspections/8f3a2b1c9d4e5f6a7b8c9d0e1f2a3b4c | jq

# Lista allt för en arbetsplats
curl "http://localhost:5080/inspections?siteId=kvarteret-vallgatan" | jq

# Analysera synkront och se fel direkt
curl -F "image=@byggplats.jpg" -F "siteId=test" \
     "http://localhost:5080/inspections?sync=true" | jq
```

## Kör lokalt mot riktiga Azure-resurser

```bash
az login   # DefaultAzureCredential plockar upp din inloggning automatiskt

cd src/Guardly.Api
dotnet user-secrets set "Storage:UseInMemory" "false"
dotnet user-secrets set "Storage:AccountName" "stguardlyprodab12cd"
dotnet user-secrets set "Vision:UseFake" "false"
dotnet user-secrets set "Vision:Endpoint" "https://<din-cv-resurs>.cognitiveservices.azure.com/"
dotnet run
```

Ditt eget konto behöver då rollerna **Storage Blob Data Contributor**,
**Storage Queue Data Contributor** och **Cognitive Services User**.

> `user-secrets` sparar värdena utanför projektmappen och kan därför aldrig hamna i git.
> Lägg inga hemligheter i `appsettings.json`.

## Testa

```bash
dotnet test
```

## Kör i Docker

```bash
docker build -t guardly-api:local .
docker run -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e Storage__UseInMemory=true \
  -e Vision__UseFake=true \
  guardly-api:local
```

---

## Driftsätt till Azure

### 1. Infrastruktur

Fyll först i `visionEndpoint` i parameterfilen — endpointen till den Computer
Vision-resurs kursansvarig har skapat.

```bash
# Se vad som skulle hända, utan att ändra något
./infra/deploy.sh rg-iths-delad prod what-if

# Kör deployen
./infra/deploy.sh rg-iths-delad prod
```

Skriv ner utdatan. `acrName` och `containerAppName` behövs i nästa steg.

Första deployen använder Microsofts quickstart-image som platshållare — vår egen image
finns ju inte i ACR än. Pipelinen byter ut den.

<details>
<summary><b>Om deployen klagar på rolltilldelningar</b></summary>

Rolltilldelningar kräver rollen Owner eller User Access Administrator. Har du bara
Contributor i den delade resursgruppen:

```bash
# Deploya utan rolltilldelningar
az deployment group create -g rg-iths-delad -f infra/main.bicep \
  -p infra/main.prod.bicepparam -p assignRoles=false
```

Be sedan kursansvarig köra följande, med `managedIdentityPrincipalId` från utdatan:

```bash
PRINCIPAL=<managedIdentityPrincipalId från deployen>

az role assignment create --assignee-object-id "$PRINCIPAL" \
  --assignee-principal-type ServicePrincipal \
  --role "AcrPull" --scope <acr-resource-id>

az role assignment create --assignee-object-id "$PRINCIPAL" \
  --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" --scope <storage-resource-id>

az role assignment create --assignee-object-id "$PRINCIPAL" \
  --assignee-principal-type ServicePrincipal \
  --role "Storage Queue Data Contributor" --scope <storage-resource-id>

az role assignment create --assignee-object-id "$PRINCIPAL" \
  --assignee-principal-type ServicePrincipal \
  --role "Cognitive Services User" --scope <computer-vision-resource-id>
```

Rollen **Cognitive Services User** på Computer Vision-resursen måste alltid tilldelas av
kursansvarig, eftersom resursen ligger utanför vår kontroll. Utan den får appen 401 från
bildanalysen — det syns tydligt på `/health/ready`.
</details>

### 2. Pipeline

1. Skapa en **service connection** av typen Azure Resource Manager i Azure DevOps och
   ge den åtkomst till resursgruppen.
2. Uppdatera de fyra variablerna överst i `azure-pipelines.yml`:

   ```yaml
   azureServiceConnection: 'sc-iths-azure'
   resourceGroup:          'rg-iths-delad'
   acrName:                '<acrName från Bicep-utdatan>'
   containerAppName:       '<containerAppName från Bicep-utdatan>'
   ```
3. Skapa pipelinen i Azure DevOps och peka den på filen.
4. Skapa en **environment** som heter `guardly-prod` (Pipelines → Environments).
5. Pusha till `main`.

Pipelinen bygger, testar, bygger imagen i ACR, deployar och kör ett röktest mot
`/health`. Grön pipeline betyder att appen faktiskt svarar på sin publika adress.

### 3. Kontrollera att det fungerar

```bash
FQDN=$(az containerapp show -n ca-guardly-api-prod -g rg-iths-delad \
  --query "properties.configuration.ingress.fqdn" -o tsv)

curl -s "https://$FQDN/health" | jq          # ska ge 200 och "Healthy"
curl -s "https://$FQDN/health/ready" | jq    # ska ge true på alla tre
open "https://$FQDN/swagger"
```

Svarar `/health/ready` med `computerVision: false` saknas rollen Cognitive Services User.

---

## Konfiguration

Allt sätts som miljövariabler. Dubbla understreck blir punkt i .NET-konfiguration, så
`Vision__Endpoint` motsvarar `Vision:Endpoint`.

| Variabel | Standard | Vad den gör |
|---|---|---|
| `Storage__AccountName` | — | Storage-kontots namn. Tomt = lagring i minnet |
| `Storage__UseInMemory` | `false` | Tvinga lagring i minnet |
| `Vision__Endpoint` | — | Computer Vision-endpoint. Tomt = fejkad analys |
| `Vision__Features` | `tags,objects,people` | **En transaktion debiteras per feature** |
| `Vision__UseFake` | `false` | Tvinga fejkad analys |
| `Vision__MaxRetries` | `3` | Omförsök vid 429 och 5xx |
| `Worker__Enabled` | `true` | Kör bakgrundsanalysen i den här instansen |
| `Worker__MaxConcurrentAnalyses` | `3` | Samtidiga Computer Vision-anrop per replica |
| `Worker__MaxPollDelaySeconds` | `30` | Backoff när kön är tom (kostnadsoptimering) |
| `SafetyRules__MinimumConfidence` | `0.55` | Tröskel för att lita på en tagg |
| `SafetyRules__PersonConfidence` | `0.60` | Tröskel för att räkna en person |
| `Api__MaxImageSizeMb` | `20` | Största bildstorlek |
| `Azure__ManagedIdentityClientId` | — | Sätts av Bicep. Pekar ut rätt identitet |

**Inga hemligheter finns i listan.** Autentiseringen sköts av managed identity — se
avsnittet Säkerhet i `ARCHITECTURE.md`.

---

## Varningskoder

| Kod | Allvarlighet | Betydelse |
|---|---|---|
| `MISSING_HELMET` | High | Person i bild utan synlig hjälm |
| `MISSING_VEST` | High | Person i bild utan synlig varselväst |
| `MISSING_BOOTS` | Medium | Inga skyddsskor syns (fötter hamnar ofta utanför bild) |
| `ZONE_HAZARD` | Medium/Info | Riskindikator: stege, ställning, kran, schakt, truck |
| `NO_PERSON_DETECTED` | Info | Ingen person i bild — skyddsutrustning kan inte bedömas |
| `LOW_IMAGE_QUALITY` | Info | Få säkra träffar, troligen mörk eller suddig bild |

Trösklar och nyckelord ligger i konfigurationen, så Guardly kan justera känsligheten
utan en ny deploy.

---

## Projektstruktur

```
.
├── src/Guardly.Api/
│   ├── Program.cs                    Uppstart, DI, Swagger
│   ├── Endpoints/                    Minimal API-endpoints
│   ├── Models/                       Inspection, varningar, DTO:er
│   ├── Options/                      Typad konfiguration
│   └── Services/
│       ├── AzureVisionAnalyzer.cs    Computer Vision via REST + managed identity
│       ├── SafetyRuleEngine.cs       Affärslogiken — taggar blir varningar
│       ├── BlobInspectionStore.cs    Ett JSON-dokument per inspektion
│       ├── StorageQueue*.cs          Kön mellan uppladdning och analys
│       └── InspectionWorker.cs       Bakgrundsanalysen
├── tests/Guardly.Tests/              Enhetstester på regelmotorn
├── infra/
│   ├── main.bicep                    All infrastruktur
│   ├── main.dev.bicepparam           Billig testmiljö
│   ├── main.prod.bicepparam          Produktion
│   └── deploy.sh
├── Dockerfile                        Multi-stage
└── azure-pipelines.yml               bygg → test → ACR → Container Apps
```

---

## Kravuppfyllnad

<details>
<summary><b>G-krav</b></summary>

| Krav | Var |
|---|---|
| Minst 4 endpoints | 7 st, `Endpoints/` |
| `GET /health` returnerar 200 | `SystemEndpoints.cs` |
| Swagger UI på `/swagger` | `Program.cs` |
| `.WithTags()` och `.Produces<T>()` på alla | `Endpoints/` |
| Computer Vision anropas korrekt | `AzureVisionAnalyzer.cs` |
| Strukturerat svar | `Models/Inspection.cs` |
| Managed Identity, ingen nyckel i kod eller historik | `Program.cs`, `main.bicep` |
| Resultat i Blob Storage, ett dokument per post | `BlobInspectionStore.cs` |
| Bicep till befintlig resursgrupp | `infra/main.bicep` (`targetScope = 'resourceGroup'`) |
| ACR + Container Apps Environment + Container App + Storage | `infra/main.bicep` |
| `what-if` fungerar | `./infra/deploy.sh <rg> <miljö> what-if` |
| Multi-stage Dockerfile | `Dockerfile` |
| Image i ACR | `azure-pipelines.yml`, steg `Package` |
| Minst 2 replicas | `main.prod.bicepparam`, `minReplicas = 2` |
| Publik URL | Bicep-utdata `apiUrl` |
| Pipeline triggas vid push till main | `azure-pipelines.yml`, `trigger` |
| bygg → test → ACR → Container Apps | Tre stages |
| Inga hårdkodade credentials | Se Säkerhet i `ARCHITECTURE.md` |
| Ekonomianalys med faktiska siffror | `ARCHITECTURE.md` → Ekonomi |
| Teknisk reflektion | `ARCHITECTURE.md` |
| Kundrapport | `RAPPORT.md` |
| Individuell reflektion | `REFLEKTION_Can_Oz.md` |
</details>

<details>
<summary><b>VG-krav (minst tre krävs — här finns sex)</b></summary>

| Krav | Var |
|---|---|
| **Autoskalning** | Två regler i `main.bicep`: HTTP vid >10 samtidiga requests, samt köbaserad KEDA-skalning på 20 meddelanden per replica |
| **Monitoring med custom alert** | Application Insights + Log Analytics. Två larm: >5 serverfel på 5 min, och >200 meddelanden i kön |
| **Parametriserad Bicep dev/prod** | `main.dev.bicepparam` mot `main.prod.bicepparam` — replicas, CPU, minne, storage-redundans, loggretention, köskalning, larm |
| **Felhantering mot Azure-tjänsten** | `AzureVisionAnalyzer.MapError()` översätter 429/401/400/5xx till rätt HTTP-status med förklarande text, loggar upstream-koden, och gör omförsök med backoff |
| **Rollback** | `docs/ROLLBACK.md` — dokumenterad och demonstrerbar via Container Apps-revisioner |
| **Välgrundade designval** | `ARCHITECTURE.md` → Designval vi övervägde och valde bort: Container Apps mot App Service mot AKS, kö mot synkront, blob mot databas, REST mot SDK |
</details>
