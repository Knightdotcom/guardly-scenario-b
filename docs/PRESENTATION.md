# Presentationsupplägg — 10 minuter, Can Öz och Jakob El Saidi

Redovisning fredag 3 oktober. Upplägget nedan är ett **talmanus, inte en slide-mall** —
det viktiga är ordningen, vem som pratar och de siffror ni ska ha i huvudet.

**Grundregel:** kör demot live om nätet håller, men **spela in en skärminspelning som
backup kvällen innan**. Ett demo som hänger äter fyra minuter av tio.

**Fördelning:** var och en presenterar det den byggde. Den som inte pratar sköter
skärmen — det gör bytena snabbare och ingen står still.

---

## Vem gjorde vad

| | Can Öz | Jakob El Saidi |
|---|---|---|
| Applikation | API:et i .NET 8, regelmotorn med tester, kön och bakgrundsworkern | Key Vault-stödet i `AzureVisionAnalyzer.cs` och `GuardlyOptions.cs`, rätt Swagger-typ för bild-endpointen |
| Infrastruktur | `infra/main.bicep` med user-assigned identity, Dockerfilen, parameterfiler för dev och prod | Anpassade mallen till kursprenumerationen: Container Apps API `2025-01-01`, Standard_LRS, larm avstängda i prod. Key Vault för Vision-nyckeln |
| Pipeline | `azure-pipelines.yml`, service connection `sc-iths-azure` med workload identity federation | Resursgrupp och ACR-namn, kontrollen som stoppar körningen på platshållare. Följde körning 4 och 5 till grönt |
| Driftsättning | `scripts/go.sh` (sju faser med PASS/FAIL-verifiering), `scripts/push.sh`, rättade `Vision__UseFake` som Bicep skrev över | Hittade varför första prod-deployen föll, felsökte Vision (fel endpoint, sedan tenant-problemet), testade bildanalysen med riktiga foton |
| Dokumentation | `ARCHITECTURE.md`, kostnadskalkylen, `docs/ROLLBACK.md` | Uppdaterade `README.md` och `ARCHITECTURE.md` efter Key Vault och policyerna, skrev om `RAPPORT.md` enligt kursens mall |

Kort sagt: Can byggde systemet, Jakob fick det att fungera i kursens miljö och stämde
av det mot kraven. Prod-deployen felsökte ni tillsammans.

---

## Tidsplan

| Min | Vem | Vad | Poäng ni ska landa |
|---:|---|---|---|
| 0–1 | Can | Problemet och lösningen | Tre ingenjörer i ett Google Sheet hinner inte |
| 1–2 | Can | Arkitekturskiss | Kön är det intressanta valet |
| 2–5 | Jakob | **Live-demo** | Det fungerar på riktigt, och vi vet var det brister |
| 5–6 | Jakob | Pipeline, Key Vault och rollback | Grön pipeline = live app, ingen nyckel i git |
| 6–8 | Can | **Ekonomi** | Siffrorna — Container Apps dyrast, inte AI:n |
| 8–9 | Can | Designval | Varför Container Apps och inte App Service/AKS |
| 9–10 | Jakob | Vad vi skulle göra härnäst | Ärlighet om luckorna |

---

## 0–1 min: Problemet (Can)

> "Guardly säljer säkerhetsinspektion som tjänst. I dag sitter tre ingenjörer och
> granskar byggplatsfoton manuellt i ett Google Sheet åt tolv kunder, och de hinner
> inte. De kan inte ta in fler kunder utan att anställa.
>
> Vi har byggt en plattform som gör den första granskningen automatiskt: platschefen
> laddar upp ett foto, systemet svarar med taggar, konfidenspoäng och konkreta
> varningar. Jag byggde applikationen och infrastrukturen, och Jakob fick det att
> fungera i kursens Azure-miljö."

Säg tidigt att systemet är ett **stöd**, inte en ersättning. Det visar omdöme och ni
slipper frågan sedan.

## 1–2 min: Arkitektur (Can)

Visa skissen från `ARCHITECTURE.md`. Säg de fyra delarna på tjugo sekunder, och lägg
sedan all tid på kön:

> "Den intressanta delen är kön i mitten. Min första skiss lät uppladdningen anropa
> Computer Vision direkt. Jag räknade: ett anrop tar 1–2 sekunder, S1-nivån klarar
> 10 transaktioner per sekund, och vi begär tre features per bild. Med 500 bilder på
> fem minuter hade en stor del av uppladdningarna fått 429 — alltså misslyckade
> uppladdningar för platschefen.
>
> Med kön gör uppladdningen bara två snabba saker: sparar bilden och lägger ett
> meddelande. Den kan inte få 429 från Computer Vision, för den pratar aldrig med
> Computer Vision."

## 2–5 min: Demo (Jakob, Can sköter skärmen)

Ha allt förberett i separata terminalflikar **innan** ni delar skärm.

1. **Swagger på den publika URL:en.** Visa att appen faktiskt är live i Azure.
2. **Ladda upp en bild.** Peka på att svaret kommer direkt — 202 Accepted med ett ID.
3. **Hämta resultatet.** Läs upp taggarna och en varning, och peka på konfidenspoängen.
4. **`GET /inspections?siteId=...`** — visa filtreringen per arbetsplats.
5. **`GET /health/ready`** — visa att alla tre beroenden svarar true.

Var ärliga om bildanalysen i stället för att hoppas att ingen ser det:

> "Jag testade med riktiga byggplatsfoton. Computer Vision hittar personerna, men känner
> inte igen en bygghjälm som hjälm — en arbetare med hjälm flaggas för att sakna den.
> Regelmotorn gör rätt, det är indata som brister. Därför är systemet ett sorteringsstöd
> tills Guardly har en egen tränad modell."

Om något strular: byt till inspelningen utan att be om ursäkt i trettio sekunder.

## 5–6 min: Pipeline, Key Vault och rollback (Jakob)

Visa en grön pipelinekörning i Azure DevOps.

> "Push till main, bygg, test, image till ACR, deploy till Container Apps, och sist ett
> röktest mot /health. Faller testerna byggs ingen image, så produktionen rörs aldrig.
> Pipelinen loggar in i Azure med workload identity federation, som Can satte upp — det
> finns inget lösenord som kan läcka."

Key Vault på trettio sekunder:

> "Kursens Computer Vision ligger i en annan Entra-tenant än vår prenumeration, och en
> managed identity kan bara få tokens i sin egen tenant. Felet var 'Token tenant does
> not match resource tenant'. Jag lade nyckeln i Key Vault, och appen hämtar den med sin
> managed identity. Nyckeln finns aldrig i kod, mall eller git."

Demonstrera rollback om ni hinner — `az containerapp ingress traffic set` tar femton
sekunder. Manus finns i `docs/ROLLBACK.md`. **Obs:** revision `--0000010` har kod från
före Key Vault, så bildanalysen fungerar inte där. Kontrollera att den är aktiv innan,
och växla tillbaka till den senaste revisionen direkt efter demot.

## 6–8 min: Ekonomi (Can)

Ha de här sex siffrorna utantill:

| | |
|---|---|
| Vid lansering | **509 kr/mån** mot 149 900 kr i intäkt = **0,34 %** |
| Tredubblad kundbas | **922 kr/mån** — alltså **1,81 gånger**, inte tre |
| Dyrast vid lansering | **Container Apps, 244 kr** — inte AI-tjänsten |
| Dyrast vid tillväxt | **Computer Vision, 567 kr** |
| 500 bilder på 5 min | **16 kr**, kön tom efter ca 2,5 min |
| Taket | ca **12 000 bilder/timme** — de gör 200 om dagen |

> "Det som överraskade mig var att Container Apps var dyrast vid lansering, inte
> AI-tjänsten. Två replicas dygnet runt är 5,2 miljoner replica-sekunder i månaden.
> Det faktiska arbetet är 13 800 sekunder. **0,3 % av tiden gör systemet något — 99,7 %
> väntar det, och det är väntan vi betalar för.**"

> "Och en fälla: Azure debiterar per feature, inte per bild. Vi begär tre features, så
> varje bild kostar tre transaktioner. Hade jag inte läst prissidan noga hade jag
> presenterat en tredjedel av den verkliga kostnaden för en styrelse."

Avsluta med affärsrisken:

> "Den enda verkliga ekonomiska risken sitter inte i tekniken utan i avtalet.
> 'Obegränsat antal bilder' för 1 499 kr betyder att en kund med en automatisk kamera
> kan göra sitt eget abonnemang olönsamt vid ungefär 48 000 bilder i månaden. Vår
> rekommendation är en rimlighetsgräns på 2 000 bilder — där är marginalen fortfarande
> 96 % och ingen ärlig kund kommer i närheten."

## 8–9 min: Designval (Can)

Välj **ett** och gå på djupet — bättre än att nämna fyra ytligt.

> "Det jämnaste valet var Container Apps mot App Service. App Service hade fungerat och
> är enklare att komma igång med. Jag valde Container Apps av ett skäl: App Service
> skalar på CPU och minne, och det säger ingenting om vår last. Vår last är antal bilder
> som väntar på analys. Container Apps kör KEDA och kan skala på kölängd direkt. Utan
> det hade appen skalat ner mitt i analysen, eftersom HTTP-trafiken då är noll."

Har ni AKS-frågan kvar: "Vi har ingen som kan drifta Kubernetes och vi behöver inget som
bara Kubernetes ger. AKS blir aktuellt om Guardly behöver GPU-noder för egna modeller
eller om en kund kräver drift i sitt eget datacenter."

## 9–10 min: Härnäst (Jakob)

> "Tre saker vi inte hann. API:et saknar autentisering, och byggplatsfoton är
> personuppgifter — det måste göras före lansering. Larmen finns i Bicep men
> kursprenumerationens policy nekar dem; i Guardlys egen prenumeration är det en
> parameter. Och den stora möjligheten: Guardlys ingenjörer sitter på flera års manuellt
> granskade bilder i sitt Google Sheet. Det är exakt träningsmaterialet för en modell som
> faktiskt känner igen hjälm och väst. Kalkylbladet är inte flaskhalsen — det är deras
> värdefullaste tillgång."

---

## Frågor ni bör ha svar på

**"Hur delade ni upp arbetet?"** (den som får frågan svarar)
Can byggde grunden: API, regelmotor, kö, Bicep och pipeline. Jakob tog driftsättningen i
kursprenumerationen — policyerna, Key Vault-lösningen för Vision och att stämma av allt
mot G- och VG-kraven. Prod-deployen felsökte vi tillsammans. Visa tabellen "Vem gjorde
vad" om frågan går djupare.

**"Vad var svårast?"**
Can: att Bicep-deployen skrev över `Vision__UseFake`, som satts med
`az containerapp update`. Mallen beskriver önskat läge, så allt som inte står i den
försvinner vid nästa deploy. Jakob: tenant-problemet med Vision — `/health/ready` svarade
`computerVision: true` hela tiden, eftersom den bara kollar att en token går att hämta,
inte att den duger mot resursen.

**"Varför inte bara synkront? 200 bilder om dagen är ingenting." (Can)**
Stämmer på medelvärdet — 0,002 bilder i sekunden. Men lasten är inte jämn. Fredag
eftermiddag laddar alla upp veckorapporten samtidigt, och styrelseordförandens fråga
handlar just om en topp. Kön kostar ungefär hundra rader kod och tar bort hela den
felkategorin.

**"Vad händer om Computer Vision ligger nere?" (Can)**
Uppladdningar fortsätter fungera — de rör inte Computer Vision. Jobben ligger kvar på
kön och görs om med exponentiell backoff. Efter fem misslyckade försök markeras
inspektionen som Failed med en förklaring som platschefen kan läsa. `/health/ready`
svarar 503 så att driftjouren ser det.

**"Hur vet ni att varningarna stämmer?" (Jakob)**
Det gör vi inte, och vi har testat det: hjälmar känns inte igen. Därför redovisas
konfidenspoängen öppet i svaret, och rapporten rekommenderar stickprovsgranskning de
första månaderna och en egen tränad modell. Trösklarna ligger i konfigurationen, inte i
koden.

**"Varför Blob Storage och inte en databas?" (Can)**
Inspektionerna läses antingen en och en på ID eller som en lista per arbetsplats, och
blob-prefix hanterar båda. Begränsningen är fri sökning — "visa alla där hjälm saknades
i mars" kräver att alla dokument läses. Den dagen frågan ställs läggs Table Storage till
som index och blobbarna behålls.

**"Visa var i koden managed identity används." (Can, Jakob tar Key Vault-delen)**
`Program.cs` registrerar `DefaultAzureCredential`. I `main.bicep` skapas en
user-assigned identity som får AcrPull och Storage Blob/Queue Data Contributor, och
`allowSharedKeyAccess: false` gör att storage-nycklar inte ens fungerar. För Vision läser
identiteten nyckeln från Key Vault (`visionKeyFromKeyVault` i prod); i samma tenant hade
`AzureVisionAnalyzer` hämtat en token direkt med rollen Cognitive Services User.

---

## Checklista kvällen innan

- [ ] Publik URL svarar på `/health` och `/swagger`
- [ ] `/health/ready` ger `true` på alla tre
- [ ] Ett riktigt anrop mot bildanalysen ger taggar (inte fejkad analys)
- [ ] Två eller tre testbilder färdiga, inklusive en där hjälmen inte känns igen
- [ ] Skärminspelning av demot som backup
- [ ] Terminalflikar förberedda med kommandona inklistrade
- [ ] Pipeline visar en grön körning
- [ ] Rollback-revisionen kontrollerad (se 5–6 min)
- [ ] Båda har gått igenom vem som säger vad och kört tidsplanen en gång
- [ ] De sex ekonomisiffrorna utantill
- [ ] Repo-länken skickad till Marcus (deadline torsdag 2 okt 23:59)
