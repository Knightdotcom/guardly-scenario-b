# Rollback — återställ föregående version

Container App:en kör i **Single revision mode** (`activeRevisionsMode: 'Single'` i
`infra/main.bicep`). Det betyder att bara en revision är aktiv åt gången: när en ny
revision blir frisk avaktiveras den gamla automatiskt. Trafik kan alltså inte delas
eller flyttas mellan revisioner med `az containerapp ingress traffic set` — det kräver
Multiple-läge.

Rollback görs i stället genom att deploya **föregående image** igen. Pipelinen taggar
varje image med `$(Build.BuildId)`, så varje körning har en egen tagg i ACR och den gamla
versionen är garanterat samma bytes som körde innan. Det tar ungefär en minut.

Det här dokumentet är skrivet för att kunna följas klockan 16:55 en fredag av någon som
är stressad. Kommandona går att klippa ut.

---

## Steg 1 — Ta reda på vilken image som körs och vilka som finns

```bash
RG=RG-Can-Oz-97d5c9-DotNetCloudDeveloper-VT-Mars-Goteborg
APP=ca-guardly-api-prod
ACR=acrguardlyq2xi25

# Vilken image kör appen nu?
az containerapp show --name "$APP" --resource-group "$RG" \
  --query "properties.template.containers[0].image" -o tsv

# Vilka taggar finns? Nyast först.
az acr repository show-tags --name "$ACR" --repository guardly-api \
  --orderby time_desc --top 5 --output table
```

Exempel: appen kör `guardly-api:10`, och taggarna är `10`, `9`, `8`. Då är `9` den
föregående versionen.

---

## Steg 2 — Deploya föregående image

```bash
GOOD_TAG=9

az containerapp update \
  --name "$APP" \
  --resource-group "$RG" \
  --image "$ACR.azurecr.io/guardly-api:$GOOD_TAG" \
  --output table
```

Det skapar en ny revision med den gamla koden. När den är frisk tar den all trafik och
den trasiga revisionen avaktiveras. Konfiguration och hemligheter (till exempel
Vision-nyckeln från Key Vault) följer med, eftersom bara imagen byts.

---

## Steg 3 — Verifiera

```bash
FQDN=$(az containerapp show --name "$APP" --resource-group "$RG" \
  --query "properties.configuration.ingress.fqdn" -o tsv)

az containerapp show --name "$APP" --resource-group "$RG" \
  --query "{revision:properties.latestRevisionName, image:properties.template.containers[0].image}" -o table

curl -s "https://$FQDN/health" | jq
curl -s "https://$FQDN/health/ready" | jq
```

Imagen ska vara den gamla taggen. `/health` ska svara 200 med `"status": "Healthy"`, och
`/health/ready` ska visa `true` på alla tre beroenden.

---

## Steg 4 — Stoppa nästa deploy

**Det här steget glöms lättast och är det viktigaste.** Appen kör en fungerande version,
men koden i `main` är fortfarande trasig. Nästa push deployar samma fel igen.

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

## Varför inte Multiple revision mode?

I Multiple-läge ligger gamla revisioner kvar aktiva, och rollback blir en
trafikomläggning på några sekunder. Det går också att dela trafiken, till exempel 90/10,
och jämföra felfrekvensen per revision i Application Insights.

Vi valde Single eftersom det är enklare och billigare: varje aktiv revision kör sina egna
repliker, och med `minReplicas = 2` skulle en kvarliggande gammal revision dubblera
grundkostnaden. Rollback via image tar en minut i stället för sekunder, och det räcker
för Guardlys behov. Vill man byta är det en ändring av `activeRevisionsMode` i Bicep.

---

## Att demonstrera rollback vid redovisningen

1. Visa vilken image och revision som körs (steg 1). Notera taggen.
2. Kör steg 2 med föregående tagg. Det tar ungefär en minut — prata under tiden om
   varför varje pipelinekörning har en egen tagg.
3. Visa att imagen har bytts och att `/health` svarar 200 (steg 3).
4. **Rulla framåt igen** till den senaste taggen med samma kommando, så att prod kör
   aktuell kod efter demot.

Kontrollera taggarna samma dag som redovisningen. En äldre tagg kan sakna senare
ändringar — till exempel har images från före Key Vault-ändringen ingen fungerande
bildanalys.
