# Individuell reflektion — Can Öz

## 1. Min roll i teamet

Jag byggde grunden: API:et i .NET 8, regelmotorn med tester i `SafetyRuleEngineTests.cs`,
bakgrundsworkern med kön, Dockerfilen, `infra/main.bicep` och `azure-pipelines.yml`.
Inför driftsättningen skrev jag `scripts/go.sh`, som kör hela Azure-vägen i sju faser och
avslutar med att verifiera varje G-krav med PASS/FAIL, och `scripts/push.sh`, som stoppar
en push om en nyckel ligger i historiken. Jag satte upp service connection `sc-iths-azure`
med workload identity federation och följde första pipelinekörningen ända till en ny
revision i Container Apps.

Prod-deployen felsökte jag tillsammans med Jakob. Jag stod för skripten, pipelinens
behörigheter och felet där Bicep skrev över appens konfiguration.

## 2. Det svåraste momentet

Analyserna fastnade i `Processing` efter en deploy som hade gått grönt. Under felsökningen
hade jag satt `Vision__UseFake` med `az containerapp update`. Nästa Bicep-deploy skrev över
appens hela konfiguration och tog bort flaggan, utan något felmeddelande. Jag letade först
i workern och i Vision-anropet, eftersom det var där symptomet syntes. Det som löste det
var att jämföra revisionens miljövariabler före och efter deployen. Jag lade in flaggan
som en parameter i `main.bicep` och verifierade med what-if och `go.sh verify` (12/12).

Pipelinen hade en liknande tystnad: den första körningen stod som `notStarted` eftersom
en ny service connection och environment måste godkännas för varje pipeline. Inget var
trasigt, men inget sa det heller.

## 3. Vad jag förstår nu

Varför en managed identity inte fungerar mellan tenants. Jag trodde att managed identity
var en egenskap hos resursen som fungerar mot vilken Azure-tjänst som helst. Nu förstår
jag att den är en service principal i appens Entra-tenant, och att tokens utfärdas av den
tenantens Entra ID. Tjänsten som tar emot anropet litar bara på tokens från sin egen
tenant. Kursens Vision-resurs låg i en annan tenant, så felet blev "Token tenant does not
match resource tenant". Designen är avsiktlig: tenanten är förtroendegränsen. Ett
nyckelbaserat anrop går förbi den gränsen, och därför hamnade nyckeln i Key Vault, där
appen hämtar den med sin identity.

## 4. Vad jag skulle göra annorlunda

Jag skulle deploya en tom app med `/health` till kursprenumerationen redan dag ett, via
pipelinen. Jag körde mot den riktiga miljön först i slutet. Då kom policyerna (ingen ZRS,
inga larm), tenant-problemet och pipelinens godkännanden på samma dag. Var och en tar en
timme att lösa, men tillsammans i slutet av sprinten blir det stress. Tidigt hade de varit
små hinder ovanpå en fungerande kedja.

## 5. Arkitektur och ekonomi

"Lösningen kostar cirka 509 kronor i månaden vid lansering. Den största posten är
Container Apps med 244 kronor, eftersom jag håller två repliker igång så att ingen
uppladdning väntar på en kallstart. Computer Vision kostar 189 kronor. Den debiteras per
funktion, så tre funktioner per bild blir 18 000 transaktioner. Det motsvarar 0,34 % av er
intäkt, och tredubblas kundbasen blir kostnaden 922 kronor, inte tre gånger så mycket.

Mest sårbar är lösningen på två ställen. Det första är att API:et saknar autentisering.
Byggplatsfoton är personuppgifter, så det måste vara på plats före lansering. Det andra är
bildanalysen: i mina tester kände Computer Vision igen personer men inte hjälmar, och
systemet varnade för saknad hjälm hos en arbetare som bar en. Se det som ett
sorteringsstöd tills ni har tränat en egen modell på bilderna i ert kalkylark.

En kund som laddar upp 500 bilder på fem minuter klarar jag, eftersom kön tar emot allt
och appen skalar ut. Taket är Computer Visions gräns på cirka 12 000 bilder i timmen,
långt över era 200 om dagen."
