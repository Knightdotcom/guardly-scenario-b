# Individuell reflektion — Jakob El Saidi

Scenario B, Guardly AB.

## 1. Min roll i teamet

Can byggde grunden: API:et, regelmotorn, Bicep-mallen och pipelinen. Min del var att få
det att fungera i kursprenumerationen och att stämma av allt mot kraven. Jag gick igenom
repot mot G- och VG-listan och hittade varför Cans första prod-deploy hade fallerat:
köskalningsregeln använde `identity`, som inte stöds i Container Apps API `2024-03-01`.
Jag bytte till `2025-01-01` i `infra/main.bicep` och anpassade mallen till kurspolicyn,
som nekar både ZRS-lagring och alla larmtyper. Jag byggde Key Vault-lösningen för
Computer Vision — ändringar i `AzureVisionAnalyzer.cs`, `GuardlyOptions.cs`, `main.bicep`
och prod-parameterfilen — deployade med `az acr build` och följde pipeline-körning 4 och
5 till grönt. Till sist skrev jag om `RAPPORT.md` enligt kursens mall.

## 2. Det svåraste momentet

Bildanalysen mot Computer Vision. Först fick vi 503 "Kunde inte nå Computer Vision". Jag
trodde det var en saknad roll, men det visade sig att endpointen `cv-iths-kurs` inte
fanns — DNS-uppslaget gav ingenting. När vi fick rätt endpoint kom nästa fel: *"Token
tenant does not match resource tenant"*. Kursens Vision-resurs ligger i en annan
Entra-tenant än vår prenumeration, och då hjälper ingen rolltilldelning. Det som gjorde
det lurigt var att `/health/ready` svarade `computerVision: true` hela tiden. Den kollar
bara att managed identity kan hämta en token, inte att tokenen duger mot resursen.
Lösningen blev att lägga nyckeln i Key Vault och låta Container App:en hämta den med sin
managed identity, så att nyckeln aldrig hamnar i kod eller git.

## 3. Vad jag förstår nu som jag inte förstod innan

Att en Bicep-mall beskriver önskat läge, inte en lista med ändringar. Det lärde jag mig
den hårda vägen. Någon hade slagit på `Vision__UseFake` direkt på Container App:en med
`az containerapp update`, och nästa Bicep-deploy tog bort den, eftersom den inte stod i
mallen. Samma sak gäller imagen: deployar man mallen utan att ange `containerImage` sätts
appen tillbaka till Microsofts quickstart-image. Det är precis det idempotens betyder —
kör man samma mall får man samma resultat, oavsett vad som ändrats för hand däremellan.
Det är en styrka, eftersom miljön inte kan glida isär från koden, men det betyder också
att varje ändring i live-miljön måste in i mallen, annars försvinner den.

## 4. Vad jag skulle göra annorlunda

Jag skulle verifiera de externa beroendena dag ett: slå upp Vision-endpointen, göra ett
riktigt anrop och köra `what-if` mot kursprenumerationen innan resten byggs. Både
tenant-problemet och policyerna mot ZRS och larm upptäcktes sent, när mycket redan var
designat utifrån antaganden som inte höll. Ett enkelt anrop med `curl` och en what-if
första dagen hade visat alla tre på en kvart.

## 5. Arkitektur och ekonomi

Lösningen kostar cirka 509 kronor i månaden vid lansering med 100 arbetsplatser och 6 000
bilder i månaden — 0,34 procent av er intäkt. Den största posten är faktiskt inte
AI-tjänsten utan Container Apps, 244 kronor, eftersom två repliker står redo dygnet runt.
Computer Vision kostar 189 kronor och debiterar per funktion, inte per bild: jag begär
tre, alltså tre transaktioner per bild. Tredubblar ni kundbasen blir det 922 kronor, och
då är bildanalysen dyrast eftersom den är helt rörlig.

Mest sårbar är den inte tekniskt, utan i vad den lovar. Jag testade med riktiga
byggplatsfoton, och Computer Vision hittade personerna men kände inte igen bygghjälmen —
en arbetare med hjälm flaggades för att sakna hjälm. Systemet fungerar som sortering men
inte som beslut, tills ni tränar en egen modell på era egna granskade bilder. Näst
viktigast: API:et har ingen autentisering, och byggplatsfoton är personuppgifter.
