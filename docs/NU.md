# Körordning — presentation om 2 timmar

Gör stegen i ordning. Hoppa inte över 1 och 2.

---

## Steg 1 — Pusha repot (5 min) · DETTA ÄR INLÄMNINGEN

Skapa först ett **tomt** repo (kryssa inte i README):
- Azure DevOps: `dev.azure.com` → ditt projekt → Repos → Files → New repository
- eller GitHub: `github.com/new` → Private

Sedan:

```bash
cd ~/sokvag/till/guardly
./scripts/push.sh
```

Skriptet kollar att inga nycklar ligger i historiken, lägger till remoten och
pushar. Skicka länken till Marcus **direkt** — innan du gör något annat. Är allt
annat halvfärdigt är en pushad repo-länk ändå inlämnat.

---

## Steg 2 — Få upp allt i Azure (20–30 min, mestadels väntan)

```bash
az login
./scripts/go.sh
```

Skriptet frågar om tre saker: resursgrupp, Vision-endpoint (klistra in Marcus
värde) och mejladress för larm. Sedan kör det allt:

| Fas | Vad |
|---|---|
| 0–1 | Kollar az-login, hittar resursgrupp och Computer Vision |
| 2 | Skriver `infra/main.local.bicepparam` med riktiga värden — ligger utanför git |
| 3 | `what-if` (sparas som bevis) och sedan deploy |
| 4 | Bygger imagen i ACR med `az acr build` — ingen lokal Docker behövs |
| 5 | Pekar Container App på imagen |
| 6 | Försöker tilldela Vision-rollen, annars skriver ut kommandot för Marcus |
| 7 | Verifierar varje G-krav och skriver ut PASS/FAIL |

**Kör fas 4–5 två gånger** så du får två revisioner att rulla mellan:

```bash
./scripts/go.sh 4
```

Fastnar något: kör om från den fasen, t.ex. `./scripts/go.sh 3`.

---

## Steg 3 — Fyll i pipelinen (3 min)

Fas 7 skriver ut tre värden och sparar dem i `scripts/.pipeline-varden.txt`.
Klistra in dem i `azure-pipelines.yml` rad 33–36, plus namnet på din service
connection:

```yaml
azureServiceConnection: 'DITT-SERVICE-CONNECTION-NAMN'
resourceGroup:          'fr005-...'
acrName:                'acrguardlyXXXXXX'
containerAppName:       'ca-guardly-api-prod'
```

Service connection skapas i Azure DevOps: Project settings → Service connections
→ New → Azure Resource Manager → Workload Identity federation → välj
prenumeration och resursgrupp.

Skapa också en environment som heter `guardly-prod` (Pipelines → Environments →
New), annars stoppar deploy-steget.

Sedan: commita, pusha, och låt pipelinen köra en gång så du har en **grön körning
att visa**.

```bash
git add -A && git commit -m "Fyll i riktiga pipeline-variabler" && git push
```

---

## Steg 4 — Om Computer Vision ger 502

Det är den vanligaste blockeraren och den ligger inte hos dig. Kolla:

```bash
curl -s https://DIN-URL/health/ready | jq
```

Visar den `"computerVision": false` saknas rollen. Mejla Marcus kommandot som
fas 6 skrev ut (det ligger också i `scripts/.principal-id.txt`).

**Nödplan om han inte svarar i tid** — slå på den mockade analysen:

```bash
az containerapp update -n ca-guardly-api-prod -g DIN-RG \
  --set-env-vars Vision__UseFake=true
```

Säg då **rakt ut** i presentationen: "Bildanalysen körs mockad just nu eftersom
rolltilldelningen på Computer Vision-resursen ligger hos kursansvarig. Här är
koden som gör det riktiga anropet, och här är felhanteringen som översätter
Azures statuskoder." Visa `AzureVisionAnalyzer.cs`. Det är ett ärligt svar som
visar att du förstår kedjan — betydligt bättre än ett demo som kraschar.

Slå av igen med `Vision__UseFake=false` när rollen är på plats.

---

## Steg 5 — Två saker att fixa i repot (2 min)

**Commit-historiken.** Kravet är att *alla* teammedlemmar har egna commits.
Skriv **inte** om historiken med `filter-branch` — det raderar den andra
personens commits och kräver force push mot en delad `main`. Se i stället till
att var och en committar med sitt eget namn:

```bash
git config user.name   # ska visa ditt eget namn
git config user.email  # ska vara en mejl som är kopplad till ditt GitHub-konto
```

**Reflektionen.** Varje person ska ha en egen `REFLEKTION_[dittnamn].md`,
skriven själv. Byt inte namn på någon annans fil — skapa din egen. Du måste kunna
stå för varje mening muntligt. Hinner du bara en sak: läs avsnittet om vad du lärde dig om molnekonomi,
det är det som ger VG-poängen.

---

## Steg 6 — Presentationen (resten av tiden)

Talmanuset ligger i [`docs/PRESENTATION.md`](PRESENTATION.md). Minsta möjliga
förberedelse om du har tjugo minuter kvar:

**Lär dig de sex siffrorna utantill:**

| | |
|---|---|
| Vid lansering | **509 kr/mån** mot 149 900 kr intäkt = **0,34 %** |
| Tredubblad kundbas | **922 kr/mån** — **1,81×**, inte 3× |
| Dyrast vid lansering | **Container Apps, 244 kr** — inte AI-tjänsten |
| Dyrast vid tillväxt | **Computer Vision, 567 kr** |
| 500 bilder på 5 min | **16 kr**, kön tom efter ca 2,5 min |
| Taket | ca **12 000 bilder/timme**. De gör 200 om dagen |

**Ha de tre meningarna klara:**

1. *"Kön är hela designen. Ett Computer Vision-anrop tar 1–2 sekunder, S1 klarar
   10 transaktioner per sekund, och vi begär tre features per bild. Synkron
   analys hade gett 429 på en stor del av 500 bilder — alltså misslyckade
   uppladdningar för platschefen."*

2. *"Det som överraskade mig var att Container Apps var dyrast vid lansering, inte
   AI-tjänsten. 0,3 % av tiden gör systemet något. 99,7 % väntar det, och det är
   väntan vi betalar för."*

3. *"Azure debiterar per feature, inte per bild. Tre features = tre transaktioner.
   Hade jag inte läst prissidan noga hade jag presenterat en tredjedel av den
   verkliga kostnaden för en styrelse."*

**Ta skärmbilder nu, medan allt fungerar** — Swagger, en inspektion med
varningar, `what-if`-utdatan, den gröna pipelinen, revisionslistan. Om nätet
strular under redovisningen har du dem.

---

## Frågor som kommer, och svaren

**"Varför larmar du på antal fel och inte procent?"**
Vid 200 bilder om dagen kan fem minuter innehålla en enda request. Ett
procentlarm hade larmat vid 100 % felfrekvens på ett enda fel. Absolut tal är
rätt mått vid låg volym — procent blir rätt när volymen vuxit, och då krävs en
scheduledQueryRule mot requests-tabellen med ett golv på antal requests.

**"Varför user-assigned managed identity?"**
Container App:en måste kunna hämta imagen från ACR redan när den skapas. En
system-assigned identity finns inte förrän appen finns, så den kan inte ha fått
AcrPull än. Moment 22. Med user-assigned skapas identiteten först, får rollerna,
och appen pekar på den.

**"Visa var i koden managed identity används."**
`Program.cs` registrerar `DefaultAzureCredential`.
`AzureVisionAnalyzer.GetTokenAsync` hämtar en token för scopet
`https://cognitiveservices.azure.com/.default`. Och `allowSharedKeyAccess: false`
på storage-kontot gör att nycklar inte ens fungerar om någon fick tag i dem.

**"Hur rullar du tillbaka?"**
`az containerapp ingress traffic set --revision-weight <gammal>=100`. Container
Apps behåller den gamla revisionen, så det är en trafikomläggning på sekunder,
inte en omdeploy. Manus i `docs/ROLLBACK.md`.

---

## Om du bara har 30 minuter

1. `./scripts/push.sh` — skicka länken
2. `./scripts/go.sh` — låt den gå medan du läser PRESENTATION.md
3. Lär dig de sex siffrorna
4. Skärmbilder på det som fungerar

Resten är bonus.
