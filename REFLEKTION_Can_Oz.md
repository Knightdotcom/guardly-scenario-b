# Individuell reflektion — Can "Knight" Öz

Scenario B, Guardly AB. .NET Cloud Developer, ITHS Göteborg.

> **Innan du lämnar in:** den här filen är skriven utifrån de vanliga frågorna i
> `exercises/reflektionsfragor.md`. Jämför med den faktiska frågelistan, lägg till det
> som saknas och — viktigast — skriv om formuleringarna så att de låter som du. En
> reflektion som inte låter som personen som skrivit den är inte mycket värd, varken
> för betyget eller för dig själv.

---

## Vad jag gjorde

Jag körde sprinten ensam, så jag gjorde allt: valde arkitektur, byggde API:et i .NET 8,
skrev regelmotorn och dess tester, containeriserade med en multi-stage Dockerfile,
beskrev infrastrukturen i Bicep, satte upp pipelinen i Azure DevOps och skrev
dokumentationen.

Att jobba ensam ändrade hur jag lade upp arbetet. I en grupp hade vi kunnat bygga API
och infrastruktur parallellt. Ensam blev jag tvungen att prioritera hårt och bestämma i
vilken ordning sakerna skulle bli klara. Jag valde att bygga i den ordning en deploy
kräver: först något som kompilerar och svarar på `/health`, sedan Docker, sedan Bicep,
sedan pipeline, och först därefter de riktiga funktionerna. Det gjorde att jag hade en
fungerande kedja tidigt och kunde lägga på funktionalitet ovanpå något som redan gick
att driftsätta, i stället för att sitta med en färdig applikation som inte gick att få
ut.

---

## Det svåraste tekniska problemet

Det jag fastnade längst på var behörigheterna, och det var ett problem jag först inte
förstod att jag hade.

Container App:en ska hämta sin image från ACR med managed identity. Min första version
använde en **system-assigned** identity, vilket kändes som det enklaste. Då hamnade jag
i ett moment 22: identiteten existerar inte förrän Container App:en har skapats, men
Container App:en kan inte skapas eftersom den inte får hämta imagen — den har ju ingen
identitet som har fått AcrPull än. Deployen gick igenom, men appen fastnade i
`ImagePullBackOff` och jag förstod inte varför förrän jag läste loggarna ordentligt.

Lösningen var att byta till en **user-assigned** managed identity. Då skapas identiteten
som en egen resurs först, får sina roller, och både image-hämtningen och applikationen
pekar på samma identitet. Det krävde också att jag lade in `dependsOn` i Bicep-mallen så
att rolltilldelningarna garanterat är klara innan Container App:en skapas.

Det jag tar med mig är inte främst den tekniska lösningen, utan insikten om **ordningen
mellan resurser**. I portalen märker man aldrig det här, för man klickar i ordning av
sig själv. I Bicep skapas saker parallellt om man inte säger något, och då blir beroenden
mellan resurser något man måste tänka på aktivt.

---

## Det viktigaste beslutet jag tog

Att lägga en kö mellan uppladdning och bildanalys.

Min första skiss hade `POST /inspections` som anropade Computer Vision direkt och
returnerade resultatet. Enklare att bygga, enklare att förklara, och fullt tillräckligt
för de 200 bilder om dagen scenariot beskriver. Det var när jag läste
styrelseordförandens fråga — 500 bilder på fem minuter — som jag insåg att den
lösningen skulle gå sönder på ett sätt som kunden skulle märka: uppladdningar som
misslyckas med 429.

Det som gjorde mest intryck på mig var att jag inte kom på det genom att titta på koden,
utan genom att räkna. Ett Computer Vision-anrop tar 1–2 sekunder. Takgränsen på S1 är 10
transaktioner per sekund. Vi begär tre features per bild, alltså tre transaktioner. Tre
siffror, och plötsligt var det uppenbart att synkron analys inte skulle hålla.

Jag har tidigare tänkt på arkitektur som något man gör utifrån mönster man läst om. Den
här gången gjorde jag det utifrån en uträkning, och jag är betydligt säkrare på det
beslutet än på beslut jag tagit för att "så brukar man göra".

---

## Vad jag lärde mig om molnekonomi

Det här var den största överraskningen i hela uppgiften.

Jag hade förväntat mig att bildanalysen skulle dominera kostnaden — det är ju den
"riktiga" AI-tjänsten. När jag räknat färdigt visade det sig att **Container Apps var
dyrast vid lansering**: 244 kr i månaden mot Computer Visions 189 kr. Och när jag tittade
på varför blev det ännu mer lärorikt. Två replicas som står igång dygnet runt är cirka
5,2 miljoner replica-sekunder i månaden. Det faktiska arbetet — 6 000 bilder à ett par
sekunder — är 13 800 sekunder. **0,3 % av tiden gör systemet något. 99,7 % väntar det,
och det är den väntan vi betalar för.**

Två saker som fick mig att tänka om:

**Priset per feature, inte per bild.** Azure debiterar en transaktion per feature, så
`tags,objects,people` är tre transaktioner per bild, inte en. Det tredubblar kostnaden
jämfört med vad jag först räknade. Hade jag inte läst prissidan noga hade jag presenterat
en siffra som var en tredjedel av den verkliga för en styrelse. Det är en obehaglig
tanke.

**Pollningens dolda kostnad.** Min bakgrundsworker pollade kön varje sekund i första
versionen. Azure räknar en replica som "aktiv" om den använder mer än 0,01 kärnor eller
tar emot mer än 1 000 byte per sekund — och aktiv vCPU kostar åtta gånger mer än idle. En
worker som pollar varje sekund hade alltså räknats som aktiv dygnet runt. Jag lade in en
backoff upp till 30 sekunder när kön är tom. Det är storleksordningen 1 500 kr om året,
för en ändring på fem rader kod som inte påverkar funktionen alls.

Min slutsats är att kostnadsoptimering i molnet mer handlar om att förstå hur
debiteringen fungerar än om att skriva effektiv kod. Jag hade kunnat optimera min
C#-kod hur länge som helst utan att komma i närheten av de här två besparingarna.

---

## Vad jag skulle göra annorlunda

**Läsa prissidan innan jag byggde, inte efter.** Jag valde `tags,objects,people` för att
det gav bäst resultat, och upptäckte kostnadskonsekvensen först när jag skulle skriva
ekonomiavsnittet. Det gick bra den här gången eftersom det bara var en miljövariabel att
ändra, men det hade lika gärna kunnat vara ett val som satt djupt i arkitekturen.

**Sätta upp deploy-kedjan ännu tidigare.** Jag byggde ändå "infrastruktur före
funktioner", men jag hade en halvdag där jag byggde API-funktioner utan att ha testat
en riktig deploy. När jag sedan gjorde det dök behörighetsproblemet upp, och då hade jag
redan hunnit bygga en del ovanpå antaganden. Nästa gång vill jag ha `git push` → live
app fungerande med en tom `/health`-endpoint **innan** jag skriver en enda rad
affärslogik.

**Skriva testerna tidigare.** Jag skrev regelmotorn först och testerna efteråt. Testerna
hittade tre riktiga buggar — bland annat att jag matchade på delsträng, så att taggen
"vested" räknades som en varselväst. Hade jag skrivit testerna parallellt hade jag
hittat det direkt i stället för att felsöka bakåt.

**Be om hjälp tidigare.** Jag satt betydligt längre än jag borde med
`ImagePullBackOff`-problemet innan jag började läsa dokumentationen på allvar. En halvtimme
i Microsoft Learn hade sparat mig ett par timmar av gissningar.

---

## Vad jag skulle göra om jag fick en vecka till

I prioritetsordning:

1. **Autentisering.** API:et är öppet, och byggplatsfoton är personuppgifter. Det är den
   största luckan i leveransen och jag är inte helt bekväm med att lämna den så.
2. **Ett riktigt index.** `GET /inspections` listar blobbar, vilket blir långsammare ju
   fler dokument som finns. Azure Table Storage med `siteId` som partitionsnyckel hade
   löst det.
3. **Integrationstester mot riktiga Azure-resurser.** Jag har enhetstester på
   regelmotorn, men ingenting som testar att blob-lagringen och kön faktiskt fungerar
   ihop. Testcontainers med Azurite hade gett det.
4. **En enkel webbvy.** Just nu måste platschefen läsa JSON. En sida som visar bilden
   med varningarna bredvid hade gjort produkten demonstrerbar för en kund.

---

## Vad jag tar med mig

Tre saker.

**Att räkna innan man bygger.** Uträkningen av 500 bilder på fem minuter tog kanske
tjugo minuter och avgjorde hela arkitekturen. Jag har tidigare hoppat över den sortens
överslag för att de känns som pappersarbete. Det gör jag inte igen.

**Att "serverless" inte betyder gratis.** Container Apps marknadsförs som något man
betalar för när man använder det. I praktiken betalade vi för 99,7 % väntan, eftersom vi
behövde två replicas igång. Det är inte fel på tjänsten — det är jag som inte förstod
debiteringsmodellen förrän jag räknade på den.

**Att managed identity är enklare än nycklar, inte svårare.** Jag har tidigare sett
managed identity som det svåra, "riktiga" sättet och connection strings som genvägen.
Efter den här sprinten tycker jag tvärtom. Med managed identity finns det ingenting att
rotera, ingenting att lägga i Key Vault, ingenting som kan hamna i git-historiken. Det
enda jobbiga är att få rollerna rätt, och det gör man en gång i en Bicep-mall.
