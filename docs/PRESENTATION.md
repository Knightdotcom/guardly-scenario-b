# Presentationsupplägg — 10 minuter, ensam

Redovisning fredag 3 oktober. Upplägget nedan är ett **talmanus, inte en slide-mall** —
det viktiga är ordningen och de siffror du ska ha i huvudet.

**Grundregel:** kör demot live om nätet håller, men **spela in en skärminspelning som
backup kvällen innan**. Ett demo som hänger äter fyra minuter av tio.

---

## Tidsplan

| Min | Vad | Poäng du ska landa |
|---:|---|---|
| 0–1 | Problemet och lösningen | Tre ingenjörer i ett Google Sheet hinner inte |
| 1–2 | Arkitekturskiss | Kön är det intressanta valet |
| 2–5 | **Live-demo** | Det fungerar på riktigt |
| 5–6 | Pipeline och rollback | Grön pipeline = live app |
| 6–8 | **Ekonomi** | Siffrorna — det här är din starkaste del |
| 8–9 | Designval | Varför Container Apps och inte App Service/AKS |
| 9–10 | Vad jag skulle göra härnäst | Ärlighet om luckorna |

---

## 0–1 min: Problemet

> "Guardly säljer säkerhetsinspektion som tjänst. I dag sitter tre ingenjörer och
> granskar byggplatsfoton manuellt i ett Google Sheet åt tolv kunder, och de hinner
> inte. De kan inte ta in fler kunder utan att anställa.
>
> Jag har byggt en plattform som gör den första granskningen automatiskt: platschefen
> laddar upp ett foto, systemet svarar med taggar, konfidenspoäng och konkreta
> varningar — saknas hjälm, saknas väst, finns det en stege som inte är säkrad."

Säg tidigt att systemet är ett **stöd**, inte en ersättning. Det visar omdöme och du
slipper frågan sedan.

## 1–2 min: Arkitektur

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

## 2–5 min: Demo

Ha allt förberett i separata terminalflikar **innan** du delar skärm.

1. **Swagger på den publika URL:en.** Visa att appen faktiskt är live i Azure.
2. **Ladda upp en bild** med varningar (en bild på någon utan hjälm). Peka på att svaret
   kommer direkt — 202 Accepted med ett ID.
3. **Hämta resultatet.** Läs upp en varning och peka på konfidenspoängen.
4. **`GET /inspections?siteId=...`** — visa filtreringen per arbetsplats.
5. **`GET /health/ready`** — visa att alla tre beroenden svarar true.

Om något strular: byt till inspelningen utan att be om ursäkt i trettio sekunder.

## 5–6 min: Pipeline och rollback

Visa en grön pipelinekörning i Azure DevOps.

> "Push till main, bygg, test, image till ACR, deploy till Container Apps, och sist ett
> röktest mot /health. Faller testerna byggs ingen image, så produktionen rörs aldrig.
> Det värsta som händer är att en ny funktion inte kommer ut."

Demonstrera rollback om du hinner — `az containerapp ingress traffic set` tar femton
sekunder och ser imponerande ut. Manus finns i `docs/ROLLBACK.md`.

## 6–8 min: Ekonomi — din starkaste del

Ha de här sex siffrorna utantill:

| | |
|---|---|
| Vid lansering | **509 kr/mån** mot 149 900 kr i intäkt = **0,34 %** |
| Tredubblad kundbas | **922 kr/mån** — alltså **1,81 gånger**, inte tre |
| Dyrast vid lansering | **Container Apps, 244 kr** — inte AI-tjänsten |
| Dyrast vid tillväxt | **Computer Vision, 567 kr** |
| 500 bilder på 5 min | **16 kr**, kön tom efter ca 2,5 min |
| Taket | ca **12 000 bilder/timme** — de gör 200 om dagen |

Två poänger som visar att du förstått och inte bara räknat:

> "Det som överraskade mig var att Container Apps var dyrast vid lansering, inte
> AI-tjänsten. Två replicas dygnet runt är 5,2 miljoner replica-sekunder i månaden.
> Det faktiska arbetet är 13 800 sekunder. **0,3 % av tiden gör systemet något — 99,7 %
> väntar det, och det är väntan vi betalar för.**"

> "Och en fälla: Azure debiterar per feature, inte per bild. Vi begär tre features, så
> varje bild kostar tre transaktioner. Hade jag inte läst prissidan noga hade jag
> presenterat en tredjedel av den verkliga kostnaden för en styrelse."

Avsluta med affärsrisken — den visar att du tänker som konsult, inte bara utvecklare:

> "Den enda verkliga ekonomiska risken sitter inte i tekniken utan i avtalet.
> 'Obegränsat antal bilder' för 1 499 kr betyder att en kund med en automatisk kamera
> kan göra sitt eget abonnemang olönsamt vid ungefär 48 000 bilder i månaden. Min
> rekommendation till ledningen är en rimlighetsgräns på 2 000 bilder — där är
> marginalen fortfarande 96 % och ingen ärlig kund kommer i närheten."

## 8–9 min: Designval

Välj **ett** och gå på djupet — bättre än att nämna fyra ytligt.

> "Det jämnaste valet var Container Apps mot App Service. App Service hade fungerat och
> är enklare att komma igång med. Jag valde Container Apps av ett skäl: App Service
> skalar på CPU och minne, och det säger ingenting om min last. Min last är antal bilder
> som väntar på analys. Container Apps kör KEDA och kan skala på kölängd direkt. Utan
> det hade appen skalat ner mitt i analysen, eftersom HTTP-trafiken då är noll."

Har du AKS-frågan kvar: "Vi har ingen som kan drifta Kubernetes och vi behöver inget som
bara Kubernetes ger. Jag hade valt AKS om vi behövde GPU-noder för egna modeller eller
om en kund krävde drift i sitt eget datacenter."

## 9–10 min: Härnäst

Var ärlig — det ger förtroende, och du styr frågestunden dit du vill.

> "Tre saker jag inte hann. API:et saknar autentisering, och byggplatsfoton är
> personuppgifter — det är den viktigaste luckan och måste göras före lansering.
> Listningen skulle behöva ett riktigt index i stället för blob-listning. Och den
> stora möjligheten: Guardlys tre ingenjörer sitter på flera års manuellt granskade
> bilder i sitt Google Sheet. Det är exakt träningsmaterialet för en egen modell.
> Kalkylbladet är inte flaskhalsen — det är deras värdefullaste tillgång."

---

## Frågor du bör ha svar på

**"Varför inte bara synkront? 200 bilder om dagen är ingenting."**
Stämmer på medelvärdet — 0,002 bilder i sekunden. Men lasten är inte jämn. Fredag
eftermiddag laddar alla upp veckorapporten samtidigt, och styrelseordförandens fråga
handlar just om en topp. Kön kostar mig ungefär hundra rader kod och tar bort hela den
felkategorin.

**"Vad händer om Computer Vision ligger nere?"**
Uppladdningar fortsätter fungera — de rör inte Computer Vision. Jobben ligger kvar på
kön och görs om med exponentiell backoff. Efter fem misslyckade försök markeras
inspektionen som Failed med en förklaring som platschefen kan läsa. `/health/ready`
svarar 503 så att driftjouren ser det.

**"Hur vet du att varningarna stämmer?"**
Det vet jag inte, och det är därför konfidenspoängen redovisas öppet i svaret. Computer
Vision är tränad på bilder från hela världen, inte på svenska byggarbetsplatser. Jag
rekommenderar i kundrapporten att ingenjörerna stickprovsgranskar de första månaderna
och justerar trösklarna — de ligger i konfigurationen, inte i koden.

**"Varför Blob Storage och inte en databas?"**
Delvis för att kravspecen sa det, men det passar: inspektionerna läses antingen en och
en på ID eller som en lista per arbetsplats, och blob-prefix hanterar båda. Begränsningen
är fri sökning — "visa alla där hjälm saknades i mars" kräver att jag läser alla dokument.
Den dagen frågan ställs lägger jag till Table Storage som index och behåller blobbarna.

**"Du körde ensam — vad hade du gjort annorlunda i grupp?"**
Byggt API och infrastruktur parallellt. Ensam blev jag tvungen att bygga i den ordning en
deploy kräver: först något som svarar på /health, sedan Docker, Bicep och pipeline, och
först därefter funktionerna. Det var faktiskt nyttigt — jag hade en fungerande
deploy-kedja tidigt i stället för en färdig app som inte gick att få ut.

**"Visa var i koden managed identity används."**
`Program.cs` registrerar `DefaultAzureCredential`. `AzureVisionAnalyzer.GetTokenAsync`
hämtar en token för scopet `https://cognitiveservices.azure.com/.default`. I
`main.bicep` skapas en user-assigned identity som får AcrPull, Storage Blob/Queue Data
Contributor och Cognitive Services User. Och `allowSharedKeyAccess: false` på
storage-kontot gör att nycklar inte ens fungerar om någon fick tag i dem.

---

## Checklista kvällen innan

- [ ] Publik URL svarar på `/health` och `/swagger`
- [ ] `/health/ready` ger `true` på alla tre
- [ ] Två eller tre testbilder färdiga: en med komplett utrustning, en utan hjälm, en med stege
- [ ] Skärminspelning av demot som backup
- [ ] Terminalflikar förberedda med kommandona inklistrade
- [ ] Pipeline visar en grön körning
- [ ] De sex ekonomisiffrorna utantill
- [ ] Repo-länken skickad till Marcus (deadline torsdag 2 okt 23:59)
