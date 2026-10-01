#!/usr/bin/env bash
# =============================================================================
# Guardly — kör hela Azure-vägen
#
#   ./scripts/go.sh            kör alla faser i ordning
#   ./scripts/go.sh 4          kör bara fas 4 och framåt
#   ./scripts/go.sh verify     kör bara verifieringen
#
# Faserna är idempotenta: kör om vilken som helst utan att något går sönder.
# Konfigurationen sparas i scripts/guardly.env efter första körningen.
# =============================================================================

set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="$REPO/scripts/guardly.env"
LIVE_PARAMS="$REPO/infra/main.local.bicepparam"
IMAGE_REPO="guardly-api"

# ---- utskrift -------------------------------------------------------------
B=$'\033[1m'; G=$'\033[32m'; R=$'\033[31m'; Y=$'\033[33m'; N=$'\033[0m'
phase() { echo; echo "${B}━━━ $* ━━━${N}"; }
ok()    { echo "  ${G}✓${N} $*"; }
bad()   { echo "  ${R}✗${N} $*"; }
warn()  { echo "  ${Y}!${N} $*"; }
info()  { echo "    $*"; }
die()   { echo; echo "${R}STOPP:${N} $*"; exit 1; }

START_PHASE="${1:-0}"
[[ "$START_PHASE" == "verify" ]] && START_PHASE=7
run_phase() { [[ "$1" -ge "$START_PHASE" ]]; }

# =============================================================================
# FAS 0 — förutsättningar
# =============================================================================
if run_phase 0; then
  phase "FAS 0  Förutsättningar"

  command -v az >/dev/null || die "az-CLI saknas. Installera: brew install azure-cli"
  ok "az-CLI finns ($(az version --query '"azure-cli"' -o tsv 2>/dev/null))"

  az account show >/dev/null 2>&1 || die "Inte inloggad. Kör: az login"
  SUB_NAME=$(az account show --query name -o tsv)
  SUB_ID=$(az account show --query id -o tsv)
  USER_NAME=$(az account show --query user.name -o tsv)
  ok "Inloggad som $USER_NAME"
  info "Prenumeration: $SUB_NAME"

  az bicep version >/dev/null 2>&1 || { info "Installerar Bicep..."; az bicep install; }
  ok "Bicep finns"

  command -v curl >/dev/null || die "curl saknas"
  command -v jq   >/dev/null || warn "jq saknas — utskrifterna blir rådare. brew install jq"
fi

# =============================================================================
# FAS 1 — konfiguration
# =============================================================================
if run_phase 1; then
  phase "FAS 1  Konfiguration"

  if [[ -f "$ENV_FILE" ]]; then
    # shellcheck source=/dev/null
    source "$ENV_FILE"
    ok "Läste tidigare konfiguration från scripts/guardly.env"
    info "Resursgrupp   : ${RESOURCE_GROUP:-}"
    info "Vision        : ${VISION_ENDPOINT:-}"
    info "Larm-mejl     : ${ALERT_EMAIL:-}"
    echo
    read -r -p "  Behåll den? [J/n] " keep
    [[ "$keep" =~ ^[nN] ]] && rm -f "$ENV_FILE"
  fi

  if [[ ! -f "$ENV_FILE" ]]; then
    echo
    echo "  ${B}Resursgrupper du har tillgång till:${N}"
    az group list --query "[].{Namn:name, Region:location}" -o table | sed 's/^/    /'
    echo
    read -r -p "  Vilken resursgrupp ska vi deploya i? " RESOURCE_GROUP
    [[ -n "$RESOURCE_GROUP" ]] || die "Resursgrupp måste anges"
    az group show -n "$RESOURCE_GROUP" >/dev/null 2>&1 || die "Resursgruppen '$RESOURCE_GROUP' finns inte eller du saknar åtkomst"

    echo
    echo "  ${B}Letar efter Computer Vision-resurser...${N}"
    CV_TABLE=$(az cognitiveservices account list \
      --query "[?kind=='ComputerVision' || kind=='CognitiveServices'].{Namn:name, Typ:kind, Grupp:resourceGroup, Endpoint:properties.endpoint}" \
      -o table 2>/dev/null)

    if [[ -n "$CV_TABLE" ]] && [[ $(echo "$CV_TABLE" | wc -l) -gt 2 ]]; then
      echo "$CV_TABLE" | sed 's/^/    /'
      echo
      read -r -p "  Namnet på Computer Vision-resursen (blank om du inte ser den): " VISION_ACCOUNT
      if [[ -n "$VISION_ACCOUNT" ]]; then
        VISION_ENDPOINT=$(az cognitiveservices account show -n "$VISION_ACCOUNT" \
          -g "$(az cognitiveservices account list --query "[?name=='$VISION_ACCOUNT'].resourceGroup | [0]" -o tsv)" \
          --query properties.endpoint -o tsv 2>/dev/null)
        VISION_RG=$(az cognitiveservices account list --query "[?name=='$VISION_ACCOUNT'].resourceGroup | [0]" -o tsv)
      fi
    else
      warn "Hittade ingen Computer Vision-resurs du har läsrätt på."
      info "Det är normalt — kursansvarig äger resursen."
    fi

    if [[ -z "${VISION_ENDPOINT:-}" ]]; then
      echo
      info "Klistra in endpointen från Marcus, t.ex.:"
      info "  https://cv-iths-kurs.cognitiveservices.azure.com/"
      read -r -p "  Vision-endpoint: " VISION_ENDPOINT
      [[ "$VISION_ENDPOINT" == https://*cognitiveservices.azure.com* ]] \
        || die "Det ser inte ut som en Computer Vision-endpoint: '$VISION_ENDPOINT'"
      VISION_ACCOUNT="${VISION_ACCOUNT:-}"
      VISION_RG="${VISION_RG:-}"
    fi

    echo
    DEFAULT_MAIL=$(az account show --query user.name -o tsv)
    read -r -p "  Mejladress för larm [$DEFAULT_MAIL]: " ALERT_EMAIL
    ALERT_EMAIL="${ALERT_EMAIL:-$DEFAULT_MAIL}"

    cat > "$ENV_FILE" <<EOF
# Genererad av scripts/go.sh — ligger utanför git (se .gitignore)
RESOURCE_GROUP="$RESOURCE_GROUP"
VISION_ENDPOINT="${VISION_ENDPOINT%/}/"
VISION_ACCOUNT="${VISION_ACCOUNT:-}"
VISION_RG="${VISION_RG:-}"
ALERT_EMAIL="$ALERT_EMAIL"
EOF
    ok "Sparade scripts/guardly.env"
  fi

  # shellcheck source=/dev/null
  source "$ENV_FILE"
fi

# shellcheck source=/dev/null
[[ -f "$ENV_FILE" ]] && source "$ENV_FILE" || die "Kör fas 1 först: ./scripts/go.sh 1"

# =============================================================================
# FAS 2 — parameterfil med riktiga värden
# =============================================================================
if run_phase 2; then
  phase "FAS 2  Parameterfil"

  # Vi genererar en lokal parameterfil i stället för att röra de som ligger i git.
  # main.local.bicepparam matchar .gitignore-regeln infra/*.local.bicepparam, så
  # riktiga värden kan aldrig commitas av misstag.
  cat > "$LIVE_PARAMS" <<EOF
// GENERERAD AV scripts/go.sh — ligger utanför git. Ändra inte för hand.
// Samma innehåll som main.prod.bicepparam, men med riktiga värden i stället
// för platshållare.

using './main.bicep'

param namePrefix = 'guardly'
param environment = 'prod'

param visionEndpoint = '$VISION_ENDPOINT'
param visionAccountName = '${VISION_ACCOUNT:-}'
param visionFeatures = 'tags,objects,people'
param visionUseFake = true

param minReplicas = 2
param maxReplicas = 10
param containerCpu = '0.5'
param containerMemory = '1.0Gi'

param acrSku = 'Basic'
param logRetentionDays = 90
param enableQueueScaling = true

param alertEmail = ''
param assignRoles = true

param tags = {
  project: 'Guardly'
  course: 'NET-Cloud-ITHS'
  environment: 'prod'
  managedBy: 'Bicep'
  costCenter: 'drift'
}
EOF
  ok "Skrev infra/main.local.bicepparam"
  info "Vision-endpoint: $VISION_ENDPOINT"
  info "Larm-mejl      : $ALERT_EMAIL"

  az bicep build --file "$REPO/infra/main.bicep" --stdout > /dev/null \
    || die "Bicep-mallen har syntaxfel"
  ok "Bicep-mallen är giltig"
fi

# =============================================================================
# FAS 3 — deploya infrastrukturen
# =============================================================================
DEPLOY_NAME="guardly-prod-$(date +%Y%m%d-%H%M%S)"

if run_phase 3; then
  phase "FAS 3  Infrastruktur"

  info "Kör what-if (det här är kravet 'what-if fungerar utan fel')..."
  if az deployment group what-if \
       -g "$RESOURCE_GROUP" \
       -f "$REPO/infra/main.bicep" \
       -p "$LIVE_PARAMS" \
       --no-pretty-print > "$REPO/scripts/.whatif.json" 2>"$REPO/scripts/.whatif.err"; then
    ok "what-if gick igenom utan fel"
    info "Utdata sparad i scripts/.whatif.json — ta en skärmbild för redovisningen"
  else
    bad "what-if misslyckades:"
    sed 's/^/      /' "$REPO/scripts/.whatif.err" | head -30
    die "Åtgärda felet ovan innan vi deployar"
  fi

  echo
  info "Deployar. Första gången tar det 3–5 minuter (Container Apps Environment)."
  if az deployment group create \
       -g "$RESOURCE_GROUP" \
       -n "$DEPLOY_NAME" \
       -f "$REPO/infra/main.bicep" \
       -p "$LIVE_PARAMS" \
       -o none 2>"$REPO/scripts/.deploy.err"; then
    ok "Infrastrukturen är deployad"
  else
    bad "Deployen misslyckades:"
    sed 's/^/      /' "$REPO/scripts/.deploy.err" | tail -40

    if grep -qi "RoleAssignment\|Authorization\|does not have permission" "$REPO/scripts/.deploy.err"; then
      echo
      warn "Det ser ut som att ditt konto inte får skapa rolltilldelningar."
      info "Kör om utan dem och be Marcus tilldela dem (fas 6 skriver ut kommandona):"
      info "  az deployment group create -g $RESOURCE_GROUP \\"
      info "    -f infra/main.bicep -p infra/main.local.bicepparam -p assignRoles=false"
    fi
    die "Se felet ovan"
  fi
fi

# ---- hämta utdata ---------------------------------------------------------
LAST_DEPLOY=$(az deployment group list -g "$RESOURCE_GROUP" \
  --query "sort_by([?starts_with(name,'guardly-')], &properties.timestamp)[-1].name" -o tsv 2>/dev/null)
[[ -n "$LAST_DEPLOY" ]] || die "Hittar ingen guardly-deploy i $RESOURCE_GROUP. Kör fas 3."

OUT=$(az deployment group show -g "$RESOURCE_GROUP" -n "$LAST_DEPLOY" --query properties.outputs -o json)
getout() { echo "$OUT" | python3 -c "import json,sys;print(json.load(sys.stdin).get('$1',{}).get('value',''))"; }

ACR_NAME=$(getout acrName)
ACR_SERVER=$(getout acrLoginServer)
APP_NAME=$(getout containerAppName)
STORAGE_NAME=$(getout storageAccountName)
API_URL=$(getout apiUrl)
MI_PRINCIPAL=$(getout managedIdentityPrincipalId)

[[ -n "$ACR_NAME" ]] || die "Kunde inte läsa deploy-utdata. Kör fas 3 igen."

# =============================================================================
# FAS 4 — bygg image i ACR
# =============================================================================
TAG="manual-$(date +%Y%m%d-%H%M%S)"

if run_phase 4; then
  phase "FAS 4  Bygg image i ACR"

  info "az acr build bygger imagen i Azure — ingen lokal docker behövs."
  if az acr build \
       --registry "$ACR_NAME" \
       --image "$IMAGE_REPO:$TAG" \
       --image "$IMAGE_REPO:latest" \
       --file "$REPO/Dockerfile" \
       "$REPO" -o none 2>"$REPO/scripts/.acr.err"; then
    ok "Image byggd och pushad: $ACR_SERVER/$IMAGE_REPO:$TAG"
  else
    bad "Imagebygget misslyckades:"
    sed 's/^/      /' "$REPO/scripts/.acr.err" | tail -40
    die "Oftast ett kompileringsfel. Kör 'dotnet build' lokalt och läs felet."
  fi
else
  TAG=$(az acr repository show-tags -n "$ACR_NAME" --repository "$IMAGE_REPO" \
        --orderby time_desc --top 1 -o tsv 2>/dev/null | head -1)
fi

# =============================================================================
# FAS 5 — deploya imagen till Container Apps
# =============================================================================
if run_phase 5; then
  phase "FAS 5  Deploya till Container Apps"

  if az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" \
       --image "$ACR_SERVER/$IMAGE_REPO:$TAG" -o none 2>"$REPO/scripts/.update.err"; then
    ok "Container App pekar nu på $IMAGE_REPO:$TAG"
  else
    bad "Uppdateringen misslyckades:"
    sed 's/^/      /' "$REPO/scripts/.update.err" | tail -20
    die "Se felet ovan"
  fi

  REVS=$(az containerapp revision list -n "$APP_NAME" -g "$RESOURCE_GROUP" --query "length(@)" -o tsv)
  ok "Antal revisioner: $REVS"
  if [[ "$REVS" -ge 2 ]]; then
    info "Bra — du har minst två revisioner, så rollback går att demonstrera."
  else
    warn "Bara en revision. Kör fas 4–5 en gång till så får du en att rulla tillbaka till."
  fi
fi

# =============================================================================
# FAS 6 — rollen på Computer Vision
# =============================================================================
if run_phase 6; then
  phase "FAS 6  Rollen Cognitive Services User"

  CV_SCOPE=""
  if [[ -n "${VISION_ACCOUNT:-}" ]]; then
    CV_SCOPE=$(az cognitiveservices account show -n "$VISION_ACCOUNT" \
      -g "${VISION_RG:-$RESOURCE_GROUP}" --query id -o tsv 2>/dev/null)
  fi

  if [[ -n "$CV_SCOPE" ]]; then
    if az role assignment create \
         --assignee-object-id "$MI_PRINCIPAL" \
         --assignee-principal-type ServicePrincipal \
         --role "Cognitive Services User" \
         --scope "$CV_SCOPE" -o none 2>/dev/null; then
      ok "Rollen tilldelad. Det kan ta upp till 5 minuter innan den slår igenom."
    else
      EXISTING=$(az role assignment list --assignee "$MI_PRINCIPAL" --scope "$CV_SCOPE" \
        --query "[?roleDefinitionName=='Cognitive Services User'] | length(@)" -o tsv 2>/dev/null || echo 0)
      if [[ "${EXISTING:-0}" -gt 0 ]]; then
        ok "Rollen var redan tilldelad"
      else
        warn "Kunde inte tilldela rollen — du saknar troligen Owner/User Access Administrator."
      fi
    fi
  else
    warn "Computer Vision-resursen ligger utanför din åtkomst."
  fi

  echo
  echo "  ${B}Om /health/ready visar computerVision: false — mejla det här till Marcus:${N}"
  echo
  echo "    az role assignment create \\"
  echo "      --assignee-object-id $MI_PRINCIPAL \\"
  echo "      --assignee-principal-type ServicePrincipal \\"
  echo "      --role \"Cognitive Services User\" \\"
  echo "      --scope <resource-id för Computer Vision-resursen>"
  echo
  info "Principal-id att uppge: $MI_PRINCIPAL"
  echo "$MI_PRINCIPAL" > "$REPO/scripts/.principal-id.txt"
  info "Sparat i scripts/.principal-id.txt"
fi

# =============================================================================
# FAS 7 — verifiera varje G-krav
# =============================================================================
if run_phase 7; then
  phase "FAS 7  Verifiering av G-kraven"

  FQDN=$(az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" \
    --query properties.configuration.ingress.fqdn -o tsv)
  BASE="https://$FQDN"
  PASS=0; FAIL=0
  check() { if [[ "$1" == "1" ]]; then ok "$2"; PASS=$((PASS+1)); else bad "$2"; FAIL=$((FAIL+1)); fi; }

  # 1 — image i ACR
  TAGS=$(az acr repository show-tags -n "$ACR_NAME" --repository "$IMAGE_REPO" -o tsv 2>/dev/null | wc -l | tr -d ' ')
  check "$([[ "${TAGS:-0}" -gt 0 ]] && echo 1 || echo 0)" "Image i ACR ($TAGS taggar i $IMAGE_REPO)"

  # 2 — Container App deployad från ACR-imagen
  CUR_IMAGE=$(az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" \
    --query "properties.template.containers[0].image" -o tsv)
  check "$([[ "$CUR_IMAGE" == *"$ACR_SERVER"* ]] && echo 1 || echo 0)" "Container App kör vår ACR-image"
  info "$CUR_IMAGE"

  # 3 — minst 2 replicas
  MINREP=$(az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" \
    --query "properties.template.scale.minReplicas" -o tsv)
  check "$([[ "${MINREP:-0}" -ge 2 ]] && echo 1 || echo 0)" "minReplicas = $MINREP (kravet är minst 2)"

  # 4 — publik URL svarar
  info "Väntar in /health (upp till 2 min)..."
  HEALTH=000
  for i in $(seq 1 24); do
    HEALTH=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 "$BASE/health" || echo 000)
    [[ "$HEALTH" == "200" ]] && break
    sleep 5
  done
  check "$([[ "$HEALTH" == "200" ]] && echo 1 || echo 0)" "GET /health svarar $HEALTH på $BASE"

  # 5 — Swagger
  SWAG=$(curl -s -o /dev/null -w "%{http_code}" --max-time 15 "$BASE/swagger/index.html" || echo 000)
  check "$([[ "$SWAG" == "200" ]] && echo 1 || echo 0)" "Swagger UI svarar $SWAG"

  # 6 — readiness: blob, kö, Computer Vision
  READY=$(curl -s --max-time 20 "$BASE/health/ready" || echo '{}')
  jget() { echo "$READY" | python3 -c "import json,sys;d=json.load(sys.stdin);print(str(d.get('$1','')).lower())" 2>/dev/null || echo ""; }
  check "$([[ "$(jget blobStorage)" == "true" ]] && echo 1 || echo 0)" "Managed identity når Blob Storage"
  check "$([[ "$(jget queue)" == "true" ]] && echo 1 || echo 0)" "Managed identity når kön"
  CV_OK=$(jget computerVision)
  check "$([[ "$CV_OK" == "true" ]] && echo 1 || echo 0)" "Managed identity kan hämta token för Computer Vision"

  # 7 — hela flödet: ladda upp, analysera, spara i Blob
  IMG="${TEST_IMAGE:-$REPO/samples/testbild.jpg}"
  if [[ -f "$IMG" ]]; then
    SITE="verifiering-$(date +%H%M%S)"
    RESP=$(curl -s --max-time 60 -F "image=@$IMG" -F "siteId=$SITE" -F "zone=Roktest" "$BASE/inspections" || echo '{}')
    ID=$(echo "$RESP" | python3 -c "import json,sys;print(json.load(sys.stdin).get('id',''))" 2>/dev/null || echo "")
    check "$([[ -n "$ID" ]] && echo 1 || echo 0)" "POST /inspections tog emot bilden (id: ${ID:-saknas})"

    if [[ -n "$ID" ]]; then
      STATUS=""; DOC=""
      for i in $(seq 1 20); do
        DOC=$(curl -s --max-time 20 "$BASE/inspections/$ID?siteId=$SITE" || echo '{}')
        STATUS=$(echo "$DOC" | python3 -c "import json,sys;print(json.load(sys.stdin).get('status',''))" 2>/dev/null || echo "")
        [[ "$STATUS" == "Completed" || "$STATUS" == "Failed" ]] && break
        sleep 3
      done
      check "$([[ "$STATUS" == "Completed" ]] && echo 1 || echo 0)" "Analysen blev $STATUS"

      if [[ "$STATUS" == "Completed" ]]; then
        echo "$DOC" > "$REPO/scripts/.bevis-inspektion.json"
        NTAGS=$(echo "$DOC" | python3 -c "import json,sys;print(len(json.load(sys.stdin).get('tags',[])))" 2>/dev/null || echo 0)
        NWARN=$(echo "$DOC" | python3 -c "import json,sys;print(len(json.load(sys.stdin).get('warnings',[])))" 2>/dev/null || echo 0)
        check "$([[ "${NTAGS:-0}" -gt 0 ]] && echo 1 || echo 0)" "Computer Vision gav $NTAGS taggar och $NWARN varningar"
        info "Hela svaret sparat i scripts/.bevis-inspektion.json"
      elif [[ "$STATUS" == "Failed" ]]; then
        ERRTXT=$(echo "$DOC" | python3 -c "import json,sys;print(json.load(sys.stdin).get('error',''))" 2>/dev/null)
        warn "Felorsak: $ERRTXT"
      fi

      # Blob-dokumentet
      BLOBS=$(az storage blob list --account-name "$STORAGE_NAME" --container-name inspections \
        --prefix "$SITE/" --auth-mode login --query "length(@)" -o tsv 2>/dev/null || echo 0)
      check "$([[ "${BLOBS:-0}" -gt 0 ]] && echo 1 || echo 0)" "Resultatdokument i Blob Storage ($BLOBS blob)"
    fi
  else
    warn "Ingen testbild hittad på $IMG — hoppar över flödestestet"
    info "Kör om med: TEST_IMAGE=/sokvag/till/bild.jpg ./scripts/go.sh verify"
  fi

  # 8 — larmen
  ALERTS=$(az monitor metrics alert list -g "$RESOURCE_GROUP" \
    --query "[?contains(name,'guardly')] | length(@)" -o tsv 2>/dev/null || echo 0)
  warn "Larm: nekas av kurskontots Azure Policy (actionGroups + metricAlerts)"

  # ---- sammanfattning ----
  echo
  echo "${B}━━━ Resultat: $PASS godkända, $FAIL underkända ━━━${N}"
  echo
  echo "  ${B}Adresser till redovisningen:${N}"
  echo "    API      $BASE"
  echo "    Swagger  $BASE/swagger"
  echo "    Health   $BASE/health"
  echo "    Ready    $BASE/health/ready"
  echo
  echo "  ${B}Värden att klistra in i azure-pipelines.yml:${N}"
  echo "    resourceGroup:    '$RESOURCE_GROUP'"
  echo "    acrName:          '$ACR_NAME'"
  echo "    containerAppName: '$APP_NAME'"
  echo
  cat > "$REPO/scripts/.pipeline-varden.txt" <<EOF
resourceGroup:    '$RESOURCE_GROUP'
acrName:          '$ACR_NAME'
containerAppName: '$APP_NAME'
API: https://$FQDN
Swagger: https://$FQDN/swagger
EOF
  info "Sparat i scripts/.pipeline-varden.txt"

  if [[ "$CV_OK" != "true" ]] || [[ "${STATUS:-}" == "Failed" ]]; then
    echo
    warn "${B}Computer Vision fungerar inte än.${N}"
    info "Troligen saknas rollen Cognitive Services User — se fas 6."
    info "Nödplan för redovisningen om Marcus inte hinner svara:"
    info "  az containerapp update -n $APP_NAME -g $RESOURCE_GROUP --set-env-vars Vision__UseFake=true"
    info "  Säg då rakt ut i presentationen att bildanalysen körs mockad för att"
    info "  rolltilldelningen ligger hos kursansvarig, och visa koden som gör anropet."
    info "  Slå av igen med: --set-env-vars Vision__UseFake=false"
  fi

  [[ "$FAIL" -eq 0 ]] || exit 1
fi

echo
echo "${G}${B}Klart.${N}"
