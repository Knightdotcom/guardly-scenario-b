#!/usr/bin/env bash
# =============================================================================
# Guardly — deploy av infrastruktur
#
# Körs manuellt första gången. Därefter sköter pipelinen deployen.
#
#   ./infra/deploy.sh <resursgrupp> <dev|prod> [what-if]
#
# Exempel:
#   ./infra/deploy.sh rg-iths-delad dev what-if     # visa vad som skulle ändras
#   ./infra/deploy.sh rg-iths-delad dev             # kör deployen
# =============================================================================

set -euo pipefail

RESOURCE_GROUP="${1:-}"
ENVIRONMENT="${2:-dev}"
MODE="${3:-create}"

if [[ -z "$RESOURCE_GROUP" ]]; then
  echo "Användning: $0 <resursgrupp> <dev|prod> [what-if]" >&2
  exit 1
fi

if [[ "$ENVIRONMENT" != "dev" && "$ENVIRONMENT" != "prod" ]]; then
  echo "Miljön måste vara 'dev' eller 'prod', inte '$ENVIRONMENT'." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE="$SCRIPT_DIR/main.bicep"
PARAMS="$SCRIPT_DIR/main.$ENVIRONMENT.bicepparam"

echo "Resursgrupp : $RESOURCE_GROUP"
echo "Miljö       : $ENVIRONMENT"
echo "Mall        : $TEMPLATE"
echo "Parametrar  : $PARAMS"
echo

# Bygg mallen först — fångar syntaxfel innan vi pratar med Azure.
echo "==> Validerar Bicep-syntax"
az bicep build --file "$TEMPLATE" --stdout > /dev/null
echo "    OK"
echo

if [[ "$MODE" == "what-if" ]]; then
  echo "==> What-if: så här skulle miljön förändras"
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$TEMPLATE" \
    --parameters "$PARAMS"
  exit 0
fi

echo "==> Deployar"
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --name "guardly-$ENVIRONMENT-$(date +%Y%m%d-%H%M%S)" \
  --template-file "$TEMPLATE" \
  --parameters "$PARAMS" \
  --output table

echo
echo "==> Klart. Adresser:"
az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$(az deployment group list --resource-group "$RESOURCE_GROUP" --query "[0].name" -o tsv)" \
  --query "properties.outputs" \
  --output json
