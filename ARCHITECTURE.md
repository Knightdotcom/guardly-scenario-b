# Arkitektur och tekniska val — Guardly AB

Scenario B, säkerhetsinspektion av byggplatsfoton med Azure Computer Vision.
Byggt av Can "Knight" Öz, .NET Cloud Developer, ITHS Göteborg.

---

## Innehåll

1. [Översikt](#översikt)
2. [Varför en kö mitt i lösningen](#varför-en-kö-mitt-i-lösningen)
3. [Container Apps](#container-apps)
4. [CI/CD](#cicd)
5. [IaC](#iac)
6. [Säkerhet](#säkerhet)
7. [Ekonomi](#ekonomi)
8. [Designval vi övervägde och valde bort](#designval-vi-övervägde-och-valde-bort)
9. [Vad vi skulle göra härnäst](#vad-vi-skulle-göra-härnäst)

---

## Översikt

```
   Platschefens mobil
          │  POST /inspections  (multipart/form-data)
          ▼
 ┌────────────────────────────────────────────────────────┐
 │  Azure Container Apps                                   │
 │  ┌──────────────────┐      ┌────────────────────────┐   │
 │  │  Minimal API     │      │  InspectionWorker      │   │
 │  │  6 endpoints     │      │  (BackgroundService)   │   │
 │  └────────┬─────────┘      └───────────┬────────────┘   │
 │           │  2–10 replicas, samma image │               │
 └───────────┼─────────────────────────────┼───────────────┘
             │                             │
   1. spara bild + dokument                │ 3. hämta jobb
   2. lägg jobb på kön                     │ 4. analysera
             │                             │ 5. spara resultat
             ▼                             ▼
   ┌──────────────────────┐      ┌──────────────────────────┐
   │  Azure Storage        │◄─────┤  Azure Computer Vision   │
   │  · blob: images       │      │  Image Analysis 4.0      │
   │  · blob: inspections  │      │  (tags, objects, people) │
   │  · queue: jobs        │      └──────────────────────────┘
   └──────────────────────┘
             ▲
             │  Managed Identity (ingen nyckel någonstans)
             │
   ┌──────────────────────┐
   │  Entra ID            │
   └──────────────────────┘
```

**Flödet, steg för steg:**

1. Platschefen laddar upp ett foto till `POST /inspections` med `siteId` och valfri `zone`.
2. API:et validerar filen, sparar originalbilden i blob-containern `images` och ett
   inspektionsdokument i containern `inspections`, lägger ett litet jobb på kön och
   svarar **202 Accepted** med inspektions-ID. Hela steget tar ungefär 200 ms.
3. `InspectionWorker` i samma container plockar jobbet, hämtar bilden och skickar den
   till Computer Visions Analyze Image-API.
4. Computer Vision svarar med taggar, objekt och personer, alla med konfidenspoäng.
5. Guardlys egen **regelmotor** (`SafetyRuleEngine`) avgör vad som ska flaggas:
   saknas hjälm, väst eller skyddsskor? Finns riskindikatorer som stegar eller ställningar?
6. Resultatet skrivs tillbaka till samma JSON-blob, som nu har status `Completed`.
7. Platschefen pollar `GET /inspections/{id}` och får taggar, konfidenspoäng och varningar.

**Viktig avgränsning:** Computer Vision är en generell bildanalystjänst. Den känner igen
"helmet" och "person", men den är inte tränad på svensk byggarbetsplatsstandard. Regel-
motorn är därför ett *stöd* för Guardlys ingenjörer, inte en ersättning. Det står också
tydligt i kundrapporten — se `RAPPORT.md`.

### Endpoints

| Metod | Väg | Vad den gör |
|---|---|---|
| `POST` | `/inspections` | Tar emot bild som multipart/form-data, returnerar inspektions-ID (202). Med `?sync=true` väntas analysen in och fel från Computer Vision returneras direkt. |
| `GET` | `/inspections/{id}` | Analysresultat: taggar, konfidenspoäng, varningar. |
| `GET` | `/inspections` | Lista alla inspektioner, filtrerbart på `siteId`, `status` och `limit`. |
| `GET` | `/inspections/{id}/image` | Strömmar originalbilden från Blob Storage. |
| `GET` | `/health` | Liveness. Svarar alltid 200 OK om processen lever. |
| `GET` | `/health/ready` | Readiness. Kollar Blob, kö och Computer Vision. 503 om något fattas. |
| `GET` | `/stats` | Aggregerad varningsstatistik per arbetsplats, underlag till veckorapporten. |

Alla är dokumenterade med `.WithTags()` och `.Produces<T>()`, och Swagger UI ligger på `/swagger`.

---

## Varför en kö mitt i lösningen

Det här är det enskilt viktigaste designvalet i hela lösningen, så det förtjänar en
egen rubrik.

Den uppenbara lösningen hade varit att låta `POST /inspections` anropa Computer Vision
direkt och returnera resultatet. Det hade varit enklare att bygga och enklare att
förklara. Vi valde bort den, och skälet är styrelseordförandens fråga.

Ett Computer Vision-anrop tar omkring 1–2 sekunder. Med synkron analys skulle varje
uppladdning hålla en HTTP-tråd öppen hela den tiden. Kommer 500 bilder på fem minuter,
och klientappen skickar dem parallellt, blir det snabbt hundratals samtidiga öppna
requests. Två saker går sönder då: replicorna skalar upp för att hantera väntan i
stället för arbete, och Computer Visions takgräns på 10 transaktioner per sekund gör
att en stor del av anropen får **429 Too Many Requests** — vilket platschefen ser som
en misslyckad uppladdning.

Med kön blir bilden en helt annan. Uppladdningen gör bara två snabba saker: skriver
bilden till Blob Storage och lägger ett litet meddelande på kön. Den kan inte få 429
från Computer Vision, eftersom den aldrig pratar med Computer Vision. **Ingen
uppladdning nekas.** Analysen sker sedan i exakt den takt Computer Vision klarar, och
workern har en inbyggd gräns (`Worker:MaxConcurrentAnalyses`) som håller oss under
takgränsen.

Kön ger tre saker till på köpet:

- **Omförsök gratis.** Ett jobb som misslyckas med ett tillfälligt fel tas inte bort
  från kön. Azure gör det synligt igen efter fem minuter och en annan replica tar över.
  Efter fem misslyckade försök markeras inspektionen som `Failed` med en förklaring.
- **Ett bättre skalningsmått.** Container App:en skalar på kölängd, inte bara HTTP-
  trafik. Utan det skulle appen skala ner direkt efter uppladdningarna — mitt i
  analysen — eftersom HTTP-trafiken då är noll.
- **Ett larm som betyder något.** Växer kön ligger analysen efter. Det är en sak
  driftjouren faktiskt vill bli väckt av, till skillnad från CPU-procent.

Priset vi betalar är att API:et blir *eventually consistent*: mellan uppladdning och
resultat finns några sekunder då inspektionen har status `Queued`. Vi hanterar det
genom att `POST` svarar med en färdig `statusUrl` att polla, och genom att `?sync=true`
finns kvar för den platschef som vill ha svar direkt på en enstaka bild.

---

## Container Apps

**Varför Container Apps och inte AKS?**

Det korta svaret är att Guardly inte har någon att anställa som Kubernetes-administratör,
och att vi inte behöver något som bara Kubernetes kan ge. Vår applikation är en enda
container som ska ta emot HTTP, skala upp vid toppar och skala ner däremellan. Container
Apps gör precis det och sköter noder, uppgraderingar, certifikat och ingress åt oss. Ett
AKS-kluster hade krävt att vi själva satt upp ingress controller, cert-manager, HPA och
nodpooler, och att vi patchade dem. Det är flera dagars arbete plus löpande underhåll —
för ett bolag med tre ingenjörer är det arbetstid som inte går till produkten. Dessutom
är AKS blockerat av kontopolicyn i den här kursen, så valet var i praktiken redan gjort,
men vi hade landat likadant ändå.

**Vilka begränsningar innebär det?**

Vi förlorar kontroll. Vi kan inte bestämma vilken typ av virtuell maskin koden kör på,
vi kan inte köra DaemonSets eller sidecars med godtycklig konfiguration, och vi kan inte
installera operatorer eller service mesh. Container Apps kör KEDA och Dapr åt oss, men
bara i den form Microsoft exponerar — vi kan till exempel inte välja en KEDA-scaler som
inte stöds. Vi är också låsta till de CPU- och minneskombinationer Consumption-planen
tillåter, med 0,25 vCPU / 0,5 GiB som minsta storlek. Nätverksmässigt är det begränsat:
vill man ha riktigt finkorniga nätverkspolicyer mellan tjänster får man leta någon
annanstans. Vi har inte heller tillgång till noderna för felsökning — loggar och
Application Insights är allt vi får.

**När skulle vi välja AKS i stället?**

Tre situationer skulle få oss att byta. Om Guardly växer till tio–femton tjänster som
ska prata med varandra och vi behöver service mesh för trafikstyrning och mTLS. Om vi
börjar träna egna bildmodeller och behöver GPU-noder med specifika drivrutiner. Eller om
en kund kräver att lösningen ska kunna köras i deras eget datacenter — då är Kubernetes
det enda som fungerar både i Azure och on-prem. Ingen av de tre är aktuell nu, och att
bygga för dem i förväg vore att betala komplexitet för något som kanske aldrig händer.

---

## CI/CD

**Flödet från `git push` till live app:**

1. **Push till `main`.** Azure DevOps upptäcker commiten och startar pipelinen.
   Ändringar som bara rör `.md`-filer triggar ingen körning — det står i `paths.exclude`
   och sparar både tid och pipeline-minuter.

2. **Steg `BuildAndTest`.** Agenten installerar .NET 8-SDK och kör `dotnet restore`,
   `dotnet build` och `dotnet test` på hela lösningen. Testerna körs med
   `--collect:"XPlat Code Coverage"` och resultatet publiceras som testrapport i
   Azure DevOps. Sist i steget valideras Bicep-mallen med `az bicep build`, vilket
   fångar syntaxfel i infrastrukturen innan vi kommer i närheten av en deploy.

3. **Steg `Package`.** Kör bara om `BuildAndTest` gick grönt **och** vi står på `main`
   (pull requests byggs och testas men deployas aldrig). `az acr build` bygger
   Docker-imagen *inne i Azure* i stället för på agenten. Fördelen är att vi slipper
   `docker login` med lösenord och att agenten inte behöver ladda upp alla lager.
   Imagen taggas både med `$(Build.BuildId)` och `latest`. Build-id:t är det som gör
   varje image spårbar till exakt en pipelinekörning — och det som gör rollback möjlig.

4. **Steg `Deploy`.** `az containerapp update` pekar Container App:en på den nya imagen.
   Container Apps skapar då en **ny revision** vid sidan av den gamla, startar den och
   flyttar över trafiken först när den svarar på sin liveness-probe. Den gamla
   revisionen finns kvar.

5. **Röktest.** Pipelinen anropar `/health` på den publika URL:en i upp till två minuter.
   Svarar den 200 OK är deployen godkänd och pipelinen skriver ut adressen. Svarar den
   aldrig 200 underkänns steget.

**Vad händer om bygget misslyckas?**

Pipelinen stannar på det steg som föll och inget efterföljande steg körs. Faller
`dotnet build` eller `dotnet test` byggs ingen image alls, så ACR rörs inte och
Container App:en fortsätter köra exakt samma revision som innan — kunderna märker
ingenting. Faller `az acr build` finns ingen ny tagg att deploya, samma sak. Faller
deploysteget eller röktestet har Container Apps redan den gamla revisionen kvar och
kan rullas tillbaka med ett kommando (se [ROLLBACK.md](docs/ROLLBACK.md)). Den som
pushade får ett mejl från Azure DevOps med länk till loggen, och commiten markeras
med ett rött kryss i historiken. Vi har alltså aldrig ett läge där en trasig build
tar ner produktionen — det värsta som händer är att en ny funktion inte kommer ut.

---

## IaC

**Varför Bicep i stället för att klicka i portalen?**

Det första skälet är att vi behöver kunna göra om det. Vi sätter upp den här miljön i
dev och i prod, och när Guardly växer sannolikt i en tredje miljö för en större kund.
Att klicka sig igenom tjugo resurser tre gånger betyder tre olika resultat — någon
glömmer alltid en inställning. Med en mall och två parameterfiler blir skillnaden
mellan miljöerna dokumenterad i stället för slumpmässig.

Det andra skälet är att infrastrukturen blir granskningsbar. `infra/main.bicep` ligger
i git, ändringar går via pull request och man kan läsa i historiken *när* vi stängde av
`allowSharedKeyAccess` och *varför*. Portalen har ingen commit-historik och ingen
kodgranskning.

Det tredje är `az deployment group what-if`. Innan vi rör produktionen kan vi se exakt
vilka resurser som skapas, ändras eller tas bort. Det är en säkerhetsspärr som inte
finns när man klickar.

**Vad är idempotens och varför spelar det roll?**

Idempotens betyder att samma operation kan köras hur många gånger som helst med samma
slutresultat. Kör vi vår Bicep-mall tio gånger i rad får vi *ett* storage-konto, inte
tio. ARM jämför mallens beskrivning av önskat läge mot det faktiska läget och gör bara
skillnaden.

Praktiskt betyder det tre saker för oss. Vi kan köra om en deploy som avbröts halvvägs
utan att städa först. Vi kan använda mallen som facit: har någon ändrat något i portalen
återställer nästa deploy det. Och pipelinen kan köra deployen vid varje push utan att
det blir dyrare eller farligare.

Idempotensen kräver dock att man skriver mallen rätt. Ett konkret exempel från vår mall
är rolltilldelningarna. En roll-tilldelning måste ha ett GUID som namn, och vi bygger det
med `guid(storage.id, identity.id, roleIds.storageBlobDataContributor)`. Eftersom samma
indata alltid ger samma GUID känner ARM igen tilldelningen vid nästa körning och
bekräftar den bara. Hade vi använt `newGuid()` hade varje deploy skapat en ny
tilldelning och vi hade fått hundratals dubletter. Samma tanke ligger bakom `uniqueString`
för resursnamnen — det ger ett deterministiskt suffix, inte ett slumpmässigt.

---

## Säkerhet

**Hur hanterar vi hemligheter och credentials?**

Den korta versionen: vi har inga. Det är ett medvetet mål, inte en slump. Genomgången
resurs för resurs:

| Vad som normalt är en hemlighet | Vad vi gör i stället |
|---|---|
| Storage connection string | Managed identity + rollen Storage Blob/Queue Data Contributor |
| Computer Vision-nyckel | Managed identity + rollen Cognitive Services User |
| ACR admin-lösenord | Managed identity + rollen AcrPull. `adminUserEnabled: false` i Bicep |
| Service principal-secret i pipelinen | Service connection i Azure DevOps (hanteras av plattformen) |

Applikationen använder `DefaultAzureCredential`, som i Container Apps hittar vår
user-assigned managed identity och begär en OAuth-token för rätt scope. Tokenen lever i
minnet, förnyas automatiskt och lämnar aldrig processen. Den kan inte kopieras till en
utvecklares laptop och den kan inte läcka i en commit, eftersom den aldrig existerar
som text någonstans.

Vi har dessutom stängt av nyckelbaserad åtkomst helt på storage-kontot
(`allowSharedKeyAccess: false`). Även om någon på något sätt fick tag i en kontonyckel
skulle den inte fungera — allt måste gå via Entra ID. Det är skillnaden mellan att
gömma en nyckel och att ta bort låset den passar i.

De värden som faktiskt sätts som miljövariabler — `Vision__Endpoint`,
`Storage__AccountName` — är adresser, inte hemligheter. Vem som helst kan känna till
dem utan att kunna använda dem, precis som en postadress inte är samma sak som en
husnyckel. Application Insights connection string lägger vi ändå som en Container
Apps-secret, mest för att den inte ska ligga i klartext i portalens miljövariabellista.

**Varför user-assigned identity och inte system-assigned?**

Ett konkret problem tvingade fram valet. Container App:en måste kunna hämta sin image
från ACR redan i det ögonblick den skapas. Med en system-assigned identity finns
identiteten inte förrän appen finns — och då har den inte hunnit få rollen AcrPull.
Moment 22. Med en user-assigned identity skapar vi identiteten först, ger den rollerna,
och pekar sedan både image-hämtningen och applikationen på den. `dependsOn` i mallen
säkerställer ordningen.

**Vad händer om en nyckel råkar hamna i git-historiken?**

Det första man måste förstå är att det inte räcker att ta bort raden och commita igen.
Nyckeln finns kvar i historiken och kan hämtas av vem som helst som har klonat repot,
och har repot legat publikt någonstans ska man utgå från att nyckeln redan är skördad
av en bot — det tar typiskt minuter, inte dagar.

Vår åtgärdsordning skulle vara:

1. **Rotera nyckeln omedelbart.** Det här är det enda steget som faktiskt stoppar
   skadan. Allt annat är städning. För Computer Vision görs det med
   `az cognitiveservices account keys regenerate`, för storage med
   `az storage account keys renew`. Från den sekunden är den läckta nyckeln värdelös.
2. **Ta reda på vad som hann hända.** Kolla aktivitetsloggen och Application Insights
   efter anrop från okända IP-adresser under tiden nyckeln var exponerad.
3. **Städa historiken** med `git filter-repo` eller BFG Repo-Cleaner och tvinga fram en
   push. Alla som har klonat repot måste klona om. Det här görs *efter* rotationen,
   aldrig i stället för.
4. **Anmäl incidenten** enligt rutin. Har kunddata kunnat nås kan det vara en
   personuppgiftsincident — byggplatsfoton innehåller identifierbara personer, och
   då gäller GDPR:s 72-timmarsregel.
5. **Sätt in en spärr.** Aktivera secret scanning och en pre-commit-hook som vägrar
   commita strängar som ser ut som nycklar.

Att vi inte har några nycklar alls gör förstås att hela det här scenariot är betydligt
mindre sannolikt hos oss — men rutinen ska finnas innan man behöver den.

---

## Ekonomi

Alla priser är hämtade från Azures prissida och prissättningskalkylatorn i september 2026,
region Sweden Central. Växelkurs **1 USD = 10,50 SEK**. Siffrorna avrundas till hela kronor.

### Prisunderlag

| Resurs | Pris |
|---|---|
| Computer Vision S1, Image Analysis (tags/objects/people) | 1,00 USD per 1 000 transaktioner, första miljonen/mån |
| Container Apps, vCPU aktiv | 0,000024 USD/vCPU-sekund |
| Container Apps, vCPU idle | 0,000003 USD/vCPU-sekund |
| Container Apps, minne | 0,000003 USD/GiB-sekund |
| Container Apps, gratisgräns per prenumeration/mån | 180 000 vCPU-s, 360 000 GiB-s, 2 milj. requests |
| Blob Storage Hot LRS | 0,0184 USD/GB/mån |
| Container Registry Basic | ca 5 USD/mån |
| Log Analytics | 2,76 USD/GB, första 5 GB/mån gratis |

> **Den viktigaste raden i hela kalkylen:** Azure debiterar **en transaktion per feature**,
> inte per bild. Vi begär `tags,objects,people` — alltså **tre transaktioner per bild**.
> Det tredubblar Computer Vision-kostnaden jämfört med vad man tror om man bara räknar
> bilder, och det är därför `Vision:Features` är en konfigurationsparameter och inte
> hårdkodad. Se [optimeringar](#var-pengarna-finns-att-hämta) nedan.

### Månadskostnad vid lansering

20 kunder × 5 arbetsplatser = **100 arbetsplatser**, ca **200 bilder/dag** = 6 000 bilder/mån
= **18 000 Computer Vision-transaktioner/mån**.

| Resurs | USD/mån | SEK/mån | Andel |
|---|---:|---:|---:|
| **Container Apps** (2 replicas, 0,5 vCPU / 1 GiB) | 23,27 | **244 kr** | 48 % |
| **Computer Vision** (18 000 transaktioner) | 18,00 | **189 kr** | 37 % |
| **Container Registry** (Basic) | 5,00 | **52 kr** | 10 % |
| **Blob Storage** (ca 88 GB efter halvår + skrivningar) | 1,73 | **18 kr** | 4 % |
| Egress | 0,50 | 5 kr | 1 % |
| Log Analytics (under gratisgränsen) | 0,00 | 0 kr | 0 % |
| **Summa** | **48,50** | **509 kr/mån** | |

**Intäkt:** 100 × 1 499 = **149 900 kr/mån**.
**Infrastrukturen är 0,34 % av intäkten**, eller **5,09 kr per arbetsplats och månad**
mot ett pris på 1 499 kr. Marginalen på infrastruktursidan är alltså inte något
Guardly behöver oroa sig för — lönekostnaden för de tre ingenjörerna är storleksordningar
större.

### Månadskostnad om kundbasen tredubblas

60 kunder × 5 arbetsplatser = **300 arbetsplatser**, ca **600 bilder/dag** = 18 000 bilder/mån
= **54 000 transaktioner/mån**.

| Resurs | USD/mån | SEK/mån | Förändring |
|---|---:|---:|---|
| **Computer Vision** | 54,00 | **567 kr** | ×3,0 — skalar rakt med volymen |
| **Container Apps** | 23,14 | **243 kr** | oförändrad |
| **Blob Storage** | 5,20 | **55 kr** | ×3,0 |
| **Container Registry** | 5,00 | **52 kr** | oförändrad |
| Egress | 0,50 | 5 kr | oförändrad |
| **Summa** | **87,84** | **922 kr/mån** | **×1,81** |

**Kundbasen tredubblas, kostnaden blir 1,81 gånger så hög.** Det är den viktigaste
observationen i hela kalkylen, och förklaringen är att ungefär 28 USD/mån av lanserings-
kostnaden är **fast**: två replicas som står och väntar plus ACR kostar lika mycket vare
sig de betjänar 100 eller 300 arbetsplatser. Kostnaden per arbetsplats faller från
**5,09 kr till 3,07 kr** — vi får stordriftsfördelar rakt av.

### Vilken resurs är dyrast, och varför?

**Vid lansering: Container Apps, 244 kr/mån (48 %).** Det överraskade oss, för det är
den resurs som marknadsförs som "serverless" och som man förväntar sig ska kosta nästan
ingenting vid låg last. Förklaringen är att vi kör med `minReplicas: 2`. Två replicas
gånger 0,5 vCPU och 1 GiB, dygnet runt, blir cirka 5,2 miljoner replica-sekunder per
månad. Själva arbetet — 6 000 bilder som tar ett par sekunder var — är bara omkring
13 800 sekunder, alltså **0,3 % av tiden**. Resten är idle-tid vi betalar för att
alltid kunna svara direkt. Det aktiva arbetet ryms med god marginal inom gratisgränsen
på 180 000 vCPU-sekunder och kostar oss noll kronor.

**Vid tredubblad volym: Computer Vision, 567 kr/mån (61 %).** Computer Vision är den
enda posten som är helt rörlig — varje bild kostar exakt lika mycket som den förra. Den
går alltså från näst dyrast till klart dyrast så fort volymen växer, medan Container
Apps ligger still. Slutsatsen för Guardlys ledning är att **priset per bild är det som
avgör lönsamheten på sikt**, inte serverkostnaden. Det är också där optimeringarna ska
sättas in.

En detalj som är lätt att missa: vi valde medvetet en **backoff i bakgrundsworkern** som
låter pollningen sakta ner till 30 sekunder när kön är tom. Azure räknar en replica som
"aktiv" om den använder mer än 0,01 kärnor eller tar emot mer än 1 000 byte per sekund.
En worker som pollar kön varje sekund skulle alltså räknas som aktiv dygnet runt, och
aktiv vCPU kostar **åtta gånger** mer än idle. Backoffen är alltså inte en
prestandaoptimering — den är en ren kostnadsoptimering värd storleksordningen
1 500 kr/år för två replicas.

### Var är flaskhalsen om trafiken fyrdubblas?

Fyrdubblad trafik betyder 800 bilder/dag. **Det är inte ett problem** — i genomsnitt är
det 0,009 bilder per sekund. Genomsnittet är dock fel sak att titta på. Flaskhalsen är
toppen, inte medelvärdet, och den sitter på ett tydligt ställe:

**Computer Visions takgräns på 10 transaktioner per sekund (S1, standardvärde).**

Eftersom varje bild kostar tre transaktioner kan vi som mest analysera **3,3 bilder per
sekund**, alltså cirka 12 000 bilder i timmen eller 288 000 per dygn. Fyrdubblad trafik
är 800 bilder per dygn, alltså **0,3 % av taket**. Vi skulle behöva ungefär 1 400 gånger
dagens volym för att slå i det på dygnsbasis.

Men takgränsen är per sekund, inte per dygn. Den slår till vid samtidiga toppar — till
exempel fredag eftermiddag när alla platschefer laddar upp veckorapporten samtidigt.
Med 100 arbetsplatser som var och en laddar upp 50 bilder mellan 15 och 17 blir det
5 000 bilder på två timmar, i snitt 0,7 bilder per sekund. Ryms bra. Vid fyrdubblad
kundbas blir det 2,8 bilder per sekund — **fortfarande under 3,3, men marginalen börjar
bli tunn**. Det är där vi skulle behöva agera.

Ordningen vi skulle åtgärda i, billigast först:

1. **Minska antal features.** Tre transaktioner per bild till en (bara `tags`) tredubblar
   kapaciteten till 10 bilder/sekund och sänker samtidigt kostnaden med två tredjedelar.
   En ändring av en miljövariabel. Kostar noggrannhet i persondetekteringen.
2. **Be Microsoft om högre takgräns.** S1 går att höja till 50 TPS eller mer via ett
   supportärende med affärsmotivering. Det är gratis och ger 16,7 bilder/sekund.
3. **Flera Computer Vision-resurser** med fördelning mellan dem. Takgränsen är per
   resurs, så två resurser ger dubbel kapacitet. Kostar ingenting extra i sig eftersom
   priset är per transaktion.
4. **Höj `maxReplicas`.** Bara meningsfullt efter steg 1–3 — fler replicas hjälper inte
   om det är Computer Vision som bromsar. Det vore att bygga ut motorvägen fram till en
   vägbom.

Andraflaskhalsen, om Computer Vision vore ur vägen, är `GET /inspections` utan filter.
Den listar blobbar och blir långsammare ju fler inspektioner som finns. Vid några hundra
tusen dokument behöver vi ett riktigt index — se [Vad vi skulle göra härnäst](#vad-vi-skulle-göra-härnäst).

### Styrelseordförandens fråga: 500 bilder på fem minuter

> *"Vad händer om en kund laddar upp 500 bilder på fem minuter? Skalas systemet — och vad kostar det?"*

**Svar: ja, det skalar, ingen uppladdning nekas, resultaten är klara ungefär två och en
halv minut efter sista bilden, och det kostar 16 kronor.**

Steg för steg:

**Uppladdningarna.** 500 bilder på 300 sekunder är 1,7 uppladdningar per sekund i snitt,
men skickas de parallellt kan det bli 30–50 samtidiga. Varje uppladdning är en
blob-skrivning plus ett kömeddelande, cirka 200 ms. HTTP-skalningsregeln lägger till en
replica per 10 samtidiga requests, så vid 50 samtidiga går vi till 5 replicas. **Alla
500 uppladdningar lyckas.** De kan inte få 429 från Computer Vision eftersom de aldrig
pratar med Computer Vision.

**Analysen.** 500 bilder × 3 features = **1 500 transaktioner**. Vid takgränsen 10 TPS
tar det minst 150 sekunder. Kön går alltså från 500 meddelanden till tom på omkring
**två och en halv minut**. Under tiden ser köskalningsregeln att kön är lång (mål: 20
meddelanden per replica) och begär 25 replicas, som kapas till `maxReplicas: 10`. Det är
inte begränsningen som styr — det är Computer Vision. Fler replicas hade bara gett fler
429:or.

**Kostnaden för bursten:**

| Post | USD | SEK |
|---|---:|---:|
| Computer Vision, 1 500 transaktioner | 1,50 | 16 kr |
| Container Apps, aktiv compute (~1 150 replica-sekunder) | 0,02 | 0,2 kr |
| Blob Storage, 500 bilder (~1,2 GB) + skrivoperationer | 0,03 | 0,3 kr |
| **Summa** | **1,55** | **≈ 16 kr** |

Den arbetsplatsen betalar 1 499 kr i månaden. **Bursten kostar alltså ungefär 1 % av en
månadsavgift.** Även om varenda arbetsplats gjorde samma sak varje vecka skulle det bli
cirka 70 kr per arbetsplats och månad — fortfarande under 5 % av priset.

Det som skulle göra ont är inte 500 bilder, utan om avtalet "obegränsat antal bilder"
tolkas som att en kund kan ladda upp 50 000 bilder i månaden per arbetsplats. Då blir
Computer Vision-kostnaden 150 000 transaktioner × 1 USD/1 000 = 150 USD = 1 575 kr per
arbetsplats och månad — **mer än priset**. Rekommendationen till ledningen är därför att
skriva in en rimlighetsgräns i avtalet, till exempel 2 000 bilder per arbetsplats och
månad med rörlig debitering därutöver. Vid 2 000 bilder är kostnaden 6 USD = 63 kr, alltså
4 % av priset — trygg marginal.

### Var pengarna finns att hämta

| Åtgärd | Besparing/mån vid lansering | Vad det kostar oss |
|---|---:|---|
| Bara `tags` i stället för tre features | 126 kr | Sämre persondetektering, fler falska varningar |
| Lifecycle-regel: bilder till Cool efter 30 dagar | ca 10 kr nu, växer med volymen | Långsammare åtkomst till gamla bilder |
| `minReplicas: 1` utanför kontorstid | ca 120 kr | Kallstart för första uppladdningen på morgonen |
| Sampling i Application Insights | 0 kr nu | Inget — bör göras innan volymen växer |

Den första är den enda som är väsentlig, och den är ett produktbeslut snarare än ett
tekniskt: hur mycket noggrannhet är Guardly beredd att byta bort mot två tredjedelars
lägre analyskostnad? Vårt förslag är att behålla tre features tills volymen närmar sig
100 000 bilder i månaden, och då utvärdera på riktiga data.

---

## Designval vi övervägde och valde bort

**Container Apps eller App Service?** Det här var det jämnaste valet. App Service kan
också köra containrar, har inbyggd deployment slots för blå/grön-deploy och är enklare
att komma igång med. Vi valde ändå Container Apps, av tre skäl. För det första skalar
App Service på CPU och minne — det finns ingen inbyggd möjlighet att skala på kölängd,
vilket är precis det mått som säger något om vår last. Vi hade fått bygga egen logik med
Azure Functions eller en autoscale-regel mot en custom metric. För det andra kan
Container Apps skala till noll, vilket gör en dev-miljö nästan gratis; App Service Plan
debiteras per timme oavsett trafik. För det tredje kör Container Apps KEDA, vilket ger
oss samma skalningsmodell som Kubernetes utan att vi behöver Kubernetes — och därmed en
rimlig migreringsväg om Guardly någon gång måste flytta. App Service hade fungerat, men
vi hade fått bygga skalningen själva.

**Databas eller Blob Storage som lagring?** Kravspecen säger "ett dokument per post i
Blob Storage", så valet var delvis givet. Vi tycker det är rätt för den här
applikationen: inspektionerna läses nästan alltid antingen en och en via ID eller som en
lista per arbetsplats, och blob-prefix (`{siteId}/{id}.json`) hanterar båda. Vi slipper
en databas att patcha och betala för. Begränsningen är att vi inte kan söka fritt —
"visa alla inspektioner där hjälm saknades i mars" kräver att vi läser alla dokument.
Den dagen frågan ställs lägger vi till Azure Table Storage eller Cosmos DB som **index**
och behåller blobbarna som lagring av sanningen.

**SDK eller REST mot Computer Vision?** Vi anropar Analyze Image via REST med
`HttpClient` i stället för `Azure.AI.Vision.ImageAnalysis`-paketet. Två skäl: färre
beroenden att uppdatera, och att det blir synligt i koden exakt hur Managed Identity
fungerar — vi hämtar en token för scopet `https://cognitiveservices.azure.com/.default`
och skickar den som en Bearer-header. För ett team som ska förstå vad som händer är det
mer värt än den lilla bekvämlighet SDK:t ger. Vi får också full kontroll över hur JSON:en
tolkas och hur felkoder översätts till våra egna svar.

**Separat worker-Container App?** Vi övervägde att dela upp API och worker i två
Container Apps för att kunna skala dem oberoende. Vi valde att ha båda i samma image med
en flagga (`Worker:Enabled`). Skälet är att de skalar av samma orsak — fler uppladdningar
betyder fler jobb — så att skala dem separat hade gett två saker att sköta utan vinst.
Flaggan gör att vi kan dela på dem senare utan att skriva om något.

---

## Vad vi skulle göra härnäst

Saker vi medvetet inte hann med och som en riktig konsult borde flagga för kunden:

1. **Autentisering.** API:et är helt öppet i dag. Nästa steg är Entra ID-autentisering
   via Container Apps inbyggda auth, eller API-nycklar per kund via Azure API Management.
   Det här är den viktigaste luckan — byggplatsfoton är personuppgifter.
2. **Index för sökning.** Azure Table Storage med `siteId` som partitionsnyckel och
   `inspectionId` som radnyckel. Gör `GET /inspections/{id}` till ett uppslag i stället
   för en listning, och möjliggör riktig filtrering på datum och varningstyp.
3. **Lifecycle management på blobbarna.** Bilder till Cool efter 30 dagar, Archive efter
   ett år. Sparar lite nu, mycket när volymen växer.
4. **Egen modell.** Computer Vision är generell. En Custom Vision-modell tränad på
   svenska byggarbetsplatser skulle ge betydligt färre falska varningar — men kräver
   några tusen märkta bilder, vilket Guardlys tre ingenjörer faktiskt sitter på i sitt
   Google Sheet. Det är den naturliga vägen från "stöd" till "ersättning".
5. **Blå/grön-deploy med trafikdelning.** Container Apps stödjer att skicka 10 % av
   trafiken till en ny revision. Vi kör i dag allt eller inget.
