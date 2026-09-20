# Rollback — återställ föregående version

Container Apps skapar en **ny revision** varje gång imagen byts. Den gamla revisionen
tas inte bort, den slutar bara ta emot trafik. Det gör rollback till en trafikomläggning
i stället för en omdeploy — det tar sekunder, och den gamla versionen är garanterat
samma bytes som körde innan.

Det här dokumentet är skrivet för att kunna följas klockan 16:55 en fredag av någon som
är stressad. Kommandona går att klippa ut.

---

## Steg 1 — Ta reda på vilka revisioner som finns

```bash
RG=rg-iths-delad
APP=ca-guardly-api-prod

az containerapp revision list \
  --name "$APP" \
  --resource-group "$RG" \
  --query "sort_by(@, &properties.createdTime)[].{Revision:name, Skapad:properties.createdTime, Aktiv:properties.active, Trafik:properties.trafficWeight, Image:properties.template.containers[0].image}" \
  --output table
```

Exempel på svar:

```
Revision                     Skapad                Aktiv   Trafik  Image
---------------------------  --------------------  ------  ------  --------------------------------------
ca-guardly-api-prod--0000014 2026-10-02T09:14:22Z  True    0       acrguardlyab12cd.azurecr.io/guardly-api:412
ca-guardly-api-prod--0000015 2026-10-03T14:51:07Z  True    100     acrguardlyab12cd.azurecr.io/guardly-api:418
```

Här är `...0000015` den trasiga nya, och `...0000014` den som fungerade.

---

## Steg 2 — Flytta tillbaka trafiken

```bash
GOOD=ca-guardly-api-prod--0000014

az containerapp ingress traffic set \
  --name "$APP" \
  --resource-group "$RG" \
  --revision-weight "$GOOD=100" \
  --output table
```

Trafiken flyttas omedelbart. Är den gamla revisionen avaktiverad, aktivera den först:

```bash
az containerapp revision activate \
  --name "$APP" \
  --resource-group "$RG" \
  --revision "$GOOD"
```

---

## Steg 3 — Verifiera

```bash
FQDN=$(az containerapp show --name "$APP" --resource-group "$RG" \
  --query "properties.configuration.ingress.fqdn" -o tsv)

curl -s "https://$FQDN/health" | jq
curl -s "https://$FQDN/health/ready" | jq
```

`/health` ska svara 200 med `"status": "Healthy"`. `/health/ready` ska visa `true` på
alla tre beroenden.

---

## Steg 4 — Stoppa nästa deploy

**Det här steget glöms lättast och är det viktigaste.** Trafiken är tillbaka på en
fungerande version, men koden i `main` är fortfarande trasig. Nästa push deployar
samma fel igen.

Välj ett av två:

**Alternativ A — revertera commiten** (att föredra, historiken blir läsbar):

```bash
git revert <commit-sha>
git push origin main
```

**Alternativ B — pausa pipelinen** medan felet utreds:
Azure DevOps → Pipelines → välj pipelinen → **Settings** → **Disable pipeline**.
Kom ihåg att slå på den igen.

---

## Alternativ: rulla tillbaka via imagen i stället

Om revisionen av någon anledning inte går att återaktivera går det att deploya om en
tidigare image. Taggarna finns kvar i ACR — det är därför pipelinen taggar med
`$(Build.BuildId)` och inte bara `latest`.

```bash
# Vilka taggar finns?
az acr repository show-tags \
  --name acrguardlyab12cd \
  --repository guardly-api \
  --orderby time_desc \
  --output table

# Deploya en känd fungerande tagg
az containerapp update \
  --name "$APP" \
  --resource-group "$RG" \
  --image acrguardlyab12cd.azurecr.io/guardly-api:412
```

Det här skapar en *ny* revision med *gammal* kod, vilket är långsammare än att flytta
trafiken (någon minut i stället för sekunder) men fungerar alltid.

---

## Gradvis återgång

Är man osäker på om den nya versionen verkligen är boven går det att dela trafiken och
mäta:

```bash
az containerapp ingress traffic set \
  --name "$APP" \
  --resource-group "$RG" \
  --revision-weight \
      ca-guardly-api-prod--0000014=90 \
      ca-guardly-api-prod--0000015=10
```

90 % av användarna är då på den säkra versionen medan 10 % fortsätter på den nya, och
Application Insights visar felfrekvensen per revision. Samma mekanism kan användas
framåt för blå/grön-deploy.

---

## Att demonstrera rollback vid redovisningen

Ett förslag på hur det blir tydligt på skärmen:

1. Visa `/health` och notera `version` och aktuell revision.
2. Pusha en ändring som bryter något synligt — enklast är att ändra texten i
   `HealthResponse.Service` till något uppenbart, så syns bytet i svaret.
3. Låt pipelinen deploya. Visa den nya revisionen i `revision list`.
4. Kör kommandot i steg 2 och visa att `/health` svarar med den gamla texten igen, inom
   några sekunder.
5. Poängtera att ingen omdeploy behövdes — den gamla revisionen låg kvar hela tiden.

Hela demonstrationen tar under två minuter och visar konkret varför revisioner är värda
mer än en `latest`-tagg.
