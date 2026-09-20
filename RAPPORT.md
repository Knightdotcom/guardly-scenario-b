# Kundrapport — Guardly AB

**Till:** Guardly AB, ledningsgruppen och styrelsen
**Från:** Can "Knight" Öz, konsult
**Datum:** oktober 2026
**Ärende:** Leverans av molnbaserad plattform för säkerhetsinspektion av byggplatsfoton

> **Om mallen:** Den här rapporten följer strukturen i `exercises/kundrapport_mall.md`.
> Stämmer inte rubrikerna exakt med kursens mall, anpassa ordningen — innehållet
> nedan täcker de frågor mallen ställer.

---

## 1. Sammanfattning

Guardly har i dag tre ingenjörer som manuellt granskar byggplatsfoton i ett Google Sheet
åt 12 betalande kunder. Ni når inte längre takten, och ni kan inte ta in fler kunder utan
att anställa.

Vi har byggt en plattform som gör den första granskningen åt er automatiskt. Platschefen
laddar upp ett foto, systemet analyserar bilden med Azure Computer Vision och returnerar
taggar, konfidenspoäng och konkreta varningar: saknas hjälm, väst eller skyddsskor, och
finns riskindikatorer som stegar eller ställningar i bild.

**Tre saker att ta med sig:**

1. **Kapaciteten är inte längre ert problem.** Systemet klarar i dag omkring 12 000
   bilder i timmen. Ni gör 200 om dagen. Ni kan tiodubbla kundbasen utan att röra
   infrastrukturen.
2. **Kostnaden är försumbar mot intäkten.** Vid lansering med 100 arbetsplatser kostar
   drift cirka **509 kr i månaden** mot en intäkt på 149 900 kr — 0,34 %. Tredubblad
   kundbas ger inte tredubblad kostnad, utan 922 kr.
3. **Ingenjörerna byter roll, de försvinner inte.** Systemet ersätter inte deras
   omdöme. Det sorterar bort de bilder som uppenbart är i sin ordning så att de tre kan
   lägga tiden på de bilder där det faktiskt är något.

Det finns en sak vi vill att ni läser innan ni skriver på nästa kundavtal: formuleringen
"obegränsat antal bilder" är den enda verkliga ekonomiska risken i affärsmodellen.
Se avsnitt 6.

---

## 2. Vad vi har levererat

### Ett API med sju endpoints

| Anrop | Vad platschefen får |
|---|---|
| `POST /inspections` | Laddar upp ett foto, får tillbaka ett inspektions-ID direkt |
| `GET /inspections/{id}` | Analysresultat: taggar, konfidenspoäng, varningar |
| `GET /inspections` | Alla inspektioner, filtrerbart per arbetsplats |
| `GET /inspections/{id}/image` | Originalbilden |
| `GET /stats` | Sammanställning per arbetsplats — underlaget till veckorapporten |
| `GET /health`, `GET /health/ready` | Driftövervakning |

API:et är dokumenterat och testbart direkt i webbläsaren på `/swagger`. Er kommande
mobilapp eller integratör behöver ingen separat dokumentation — den ligger i systemet.

### Ett exempel på vad ni får tillbaka

```json
{
  "id": "8f3a2b1c9d4e5f6a7b8c9d0e1f2a3b4c",
  "siteId": "kvarteret-vallgatan",
  "zone": "Plan 3, östra gaveln",
  "status": "Completed",
  "createdAt": "2026-10-03T13:42:18Z",
  "peopleCount": 2,
  "tags": [
    { "name": "construction site", "confidence": 0.961 },
    { "name": "scaffolding",       "confidence": 0.874 },
    { "name": "safety vest",       "confidence": 0.831 },
    { "name": "outdoor",           "confidence": 0.792 }
  ],
  "warnings": [
    {
      "code": "MISSING_HELMET",
      "message": "Ingen hjälm syns på någon av de 2 personerna i bilden.",
      "severity": "High",
      "confidence": 0.7
    },
    {
      "code": "ZONE_HAZARD",
      "message": "Riskindikator i bilden: scaffolding. Kontrollera avspärrning och fallskydd i zonen.",
      "severity": "Medium",
      "confidence": 0.874
    }
  ],
  "analysisDurationMs": 1240,
  "visionTransactions": 3
}
```

Varje varning har en **allvarlighetsgrad** (`High`, `Medium`, `Info`) och en
**konfidenspoäng**. Det gör att ni kan bygga en arbetslista där de allvarligaste
avvikelserna hamnar överst, och att era ingenjörer kan ställa in hur känsligt systemet
ska vara utan att någon behöver skriva om kod.

### Så här känns det för platschefen

Uppladdningen svarar på ungefär **två tiondels sekund**. Analysen är klar några sekunder
senare. Det är medvetet byggt så: platschefen ska kunna ta tjugo bilder i rad utan att
stå och vänta på telefonen mellan varje. Bilderna läggs i en kö och betas av i bakgrunden.

---

## 3. Hur systemet är byggt

```
Platschefens mobil  →  API i Azure Container Apps  →  Azure Computer Vision
                              ↓
                       Azure Storage
                    (bilder + resultat + kö)
```

Fyra byggstenar:

- **Azure Container Apps** kör applikationen. Den startar fler kopior automatiskt när
  många laddar upp samtidigt och drar ner igen när det lugnar sig.
- **Azure Computer Vision** gör bildanalysen. Det är Microsofts tjänst, samma teknik
  som används i stor skala världen över.
- **Azure Blob Storage** lagrar originalbilderna och ett resultatdokument per
  inspektion. Inget kastas.
- **En kö** mellan uppladdning och analys. Det är den som gör att systemet aldrig säger
  nej till en uppladdning, oavsett hur många som kommer samtidigt.

Allt är beskrivet som kod (Bicep) och driftsätts automatiskt via en pipeline. Vill ni i
framtiden flytta till en annan region eller sätta upp en separat miljö för en stor kund
är det ett kommando, inte ett projekt.

---

## 4. Vad systemet klarar — och styrelsens fråga

> *"Vad händer om en kund laddar upp 500 bilder på fem minuter? Skalas systemet — och vad kostar det?"*

**Systemet skalar. Ingen uppladdning nekas. Samtliga 500 resultat är klara ungefär två
och en halv minut efter att sista bilden laddats upp. Det kostar 16 kronor.**

Så här går det till: uppladdningarna hanteras omedelbart eftersom de bara sparar bilden
och lägger ett meddelande på kön — de väntar aldrig in bildanalysen. Systemet startar
automatiskt fler kopior av applikationen när trycket ökar. Analysen betar sedan av kön
i den takt Microsofts tjänst tillåter, cirka 3,3 bilder per sekund.

Samma sak gäller fredagseftermiddagarna. Med 100 arbetsplatser som var och en laddar upp
50 bilder mellan 15 och 17 blir det 5 000 bilder på två timmar. Det ligger väl inom vad
systemet klarar, och kön gör att toppen jämnas ut av sig själv.

**Var taket går:** cirka 12 000 bilder i timmen med dagens inställningar. Ni gör i dag
200 om dagen. Skulle ni närma er taket finns tre åtgärder, varav den första är gratis
och tar fem minuter — se avsnitt 8 i den tekniska dokumentationen.

---

## 5. Vad systemet kostar

| | Vid lansering<br>100 arbetsplatser | Om kundbasen tredubblas<br>300 arbetsplatser |
|---|---:|---:|
| Drift av applikationen | 244 kr | 243 kr |
| Bildanalys (Computer Vision) | 189 kr | 567 kr |
| Lagring av bilder och resultat | 18 kr | 55 kr |
| Övrigt (registry, övervakning, trafik) | 57 kr | 57 kr |
| **Totalt per månad** | **509 kr** | **922 kr** |
| Intäkt per månad | 149 900 kr | 449 700 kr |
| **Infrastruktur som andel av intäkt** | **0,34 %** | **0,21 %** |
| Kostnad per arbetsplats | 5,09 kr | 3,07 kr |

**Lägg märke till att kostnaden inte tredubblas när kundbasen gör det** — den ökar med
81 %. Det beror på att en stor del av kostnaden är fast: applikationen måste vara igång
dygnet runt oavsett om den betjänar 100 eller 300 arbetsplatser. Ju fler kunder, desto
billigare per kund. Det är en bra egenskap för en affärsmodell som er.

Alla priser är från Azures prissida i september 2026, region Sweden Central, växelkurs
10,50 kr per dollar. Detaljerad uträkning finns i `ARCHITECTURE.md`.

---

## 6. Risker vi vill att ni känner till

### Den viktigaste: "obegränsat antal bilder"

Ert avtal säger obegränsat antal bilder för 1 499 kr per arbetsplats och månad. Vår
kostnad per bild är fast — ungefär 3 öre. Det betyder:

| Bilder per arbetsplats och månad | Vår kostnad | Andel av 1 499 kr |
|---:|---:|---:|
| 60 (dagens nivå) | 2 kr | 0,1 % |
| 2 000 | 63 kr | 4 % |
| 20 000 | 630 kr | 42 % |
| 50 000 | 1 575 kr | **105 % — förlust** |

En enda kund som sätter upp en kamera som fotograferar automatiskt varje minut skulle
alltså kunna göra sitt eget abonnemang olönsamt. **Vår rekommendation:** skriv in en
rimlighetsgräns i avtalet, förslagsvis 2 000 bilder per arbetsplats och månad med rörlig
debitering därutöver. Vid 2 000 bilder är marginalen fortfarande 96 %, och ingen ärlig
kund kommer i närheten av gränsen.

### Systemet ser inte allt

Computer Vision är tränad på bilder från hela världen, inte på svenska byggarbetsplatser.
Den känner igen en hjälm, men den vet inte om det är rätt sorts hjälm, om hakbandet är
spänt eller om den är CE-märkt. Tre konsekvenser:

- **Falska varningar förekommer.** En person som står med ryggen till kan flaggas för
  att sakna väst fast den syns framifrån.
- **Missade avvikelser förekommer.** Dålig belysning, motljus och skymda personer gör
  analysen osäkrare. Systemet flaggar `LOW_IMAGE_QUALITY` när det märker det, men det
  fångar inte allt.
- **Systemet är ett stöd, inte ett beslut.** Rapporten ska läsas av någon som kan
  byggarbetsplatser. Det är därför konfidenspoängen redovisas öppet — ni ska kunna se
  hur säkert systemet är, inte bara vad det tycker.

Vi rekommenderar att ni de första månaderna låter era ingenjörer stickprovsgranska
bilder som systemet godkänt. Det ger er både en kvalitetssiffra att visa kund och
underlag för att justera känsligheten.

### Personuppgifter

Byggplatsfoton innehåller identifierbara personer, vilket gör dem till personuppgifter
enligt GDPR. Tre saker behöver på plats innan ni går skarpt:

1. **Autentisering på API:et.** Det är i dag öppet. Det här är den enskilt viktigaste
   åtgärden och den bör göras före lansering.
2. **Personuppgiftsbiträdesavtal** med era kunder — ni behandlar deras anställdas
   bilder.
3. **En gallringsrutin.** Hur länge sparas bilderna? Vi har byggt in möjlighet att
   automatiskt flytta och radera, men själva beslutet är ert.

### Beroendet av en leverantör

Lösningen ligger på Azure och använder Azure Computer Vision. Byter Microsoft pris eller
lägger ner tjänsten påverkar det er direkt. Bedömningen är att risken är låg på kort
sikt, och vi har begränsat exponeringen genom att bildanalysen ligger bakom ett eget
gränssnitt i koden — att byta till en annan leverantör är en avgränsad ändring, inte en
omskrivning.

---

## 7. Vad vi rekommenderar härnäst

**Före lansering (måste göras):**

1. Lägg på autentisering på API:et.
2. Skriv in en rimlighetsgräns för antal bilder i kundavtalet.
3. Bestäm och dokumentera gallringstid för bilderna.

**De första tre månaderna:**

4. Låt ingenjörerna stickprovsgranska och justera känsligheten utifrån verkliga data.
5. Bygg en enkel webbvy ovanpå API:et så att platschefen slipper läsa JSON.
6. Sätt upp automatisk flytt av gamla bilder till billigare lagring.

**På sikt — och det här är den stora möjligheten:**

7. **Träna en egen modell på era egna bilder.** Ni sitter på flera års manuellt granskade
   byggplatsfoton i ert Google Sheet. Det är exakt det träningsmaterial som behövs för
   att gå från en generell bildanalys till en som är tränad på svenska
   byggarbetsplatser. Det skulle minska de falska varningarna kraftigt och göra
   produkten till något konkurrenter inte kan kopiera. **Det där kalkylbladet är inte
   en flaskhals — det är er värdefullaste tillgång.** Börja med att inte kasta det.

---

## 8. Leverans

| | |
|---|---|
| **Källkod** | Git-repo med full historik |
| **API** | Publik URL, Swagger på `/swagger` |
| **Infrastruktur** | Bicep-mallar med separata inställningar för test och produktion |
| **Driftsättning** | Automatisk pipeline: kodändring → test → driftsättning |
| **Teknisk dokumentation** | `ARCHITECTURE.md` |
| **Kom-igång-guide** | `README.md` |
| **Återställningsrutin** | `docs/ROLLBACK.md` |

Systemet är driftsatt och svarar på sin publika adress. Kodändringar går live automatiskt
efter att testerna passerat, och går något fel finns föregående version kvar och kan
återställas på under en minut.
