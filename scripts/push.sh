#!/usr/bin/env bash
# =============================================================================
# Guardly — skapa remote och pusha
#
#   ./scripts/push.sh                       # interaktivt
#   ./scripts/push.sh <git-url>             # med färdig URL
#
# Fungerar mot både Azure DevOps och GitHub. Repot måste finnas på andra sidan
# först — skriptet skapar inget åt dig, men säger exakt var du klickar.
# =============================================================================

set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO"

B=$'\033[1m'; G=$'\033[32m'; R=$'\033[31m'; Y=$'\033[33m'; N=$'\033[0m'
ok()   { echo "  ${G}✓${N} $*"; }
bad()  { echo "  ${R}✗${N} $*"; }
warn() { echo "  ${Y}!${N} $*"; }
die()  { echo; echo "${R}STOPP:${N} $*"; exit 1; }

echo "${B}━━━ Pusha repot ━━━${N}"
echo

# ---- 1. Hemligheter i historiken? ----------------------------------------
echo "  Kollar att inga hemligheter ligger i historiken..."
HITS=$(git grep -In -E \
  "AccountKey=|DefaultEndpointsProtocol=.*AccountKey|Ocp-Apim-Subscription-Key *[:=] *[A-Za-z0-9]{20,}|\"(client_secret|clientSecret)\" *: *\"[^\"]{10,}" \
  $(git rev-list --all) -- 2>/dev/null | head -5)
if [[ -n "$HITS" ]]; then
  bad "Möjlig hemlighet i historiken:"
  echo "$HITS" | sed 's/^/      /'
  die "Rotera nyckeln och städa historiken innan du pushar. Se ARCHITECTURE.md → Säkerhet."
fi
ok "Inga nycklar hittade i historiken"

# ---- 2. Ligger lokala filer utanför git? ---------------------------------
for f in infra/main.local.bicepparam scripts/guardly.env; do
  if git ls-files --error-unmatch "$f" >/dev/null 2>&1; then
    die "$f är spårad av git men innehåller riktiga värden. Kör: git rm --cached $f"
  fi
done
ok "Lokala konfigfiler ligger utanför git"

# ---- 3. Allt commitat? ---------------------------------------------------
if [[ -n "$(git status --porcelain)" ]]; then
  warn "Du har ocommitade ändringar:"
  git status --short | sed 's/^/      /'
  echo
  read -r -p "  Commita dem nu? [J/n] " yn
  if [[ ! "$yn" =~ ^[nN] ]]; then
    git add -A
    read -r -p "  Commit-meddelande: " msg
    git commit -q -m "${msg:-Justeringar inför inlämning}"
    ok "Commitat"
  fi
fi

# ---- 4. Remote -----------------------------------------------------------
URL="${1:-}"
if [[ -z "$URL" ]]; then
  if git remote get-url origin >/dev/null 2>&1; then
    URL=$(git remote get-url origin)
    ok "Remote finns redan: $URL"
  else
    echo
    echo "  ${B}Skapa ett tomt repo först:${N}"
    echo "    Azure DevOps : dev.azure.com → ditt projekt → Repos → Files → New repository"
    echo "                   (kryssa INTE i 'Add a README')"
    echo "    GitHub       : github.com/new → Private → skapa utan README"
    echo
    read -r -p "  Klistra in repo-URL:en: " URL
    [[ -n "$URL" ]] || die "Ingen URL angiven"
  fi
fi

if git remote get-url origin >/dev/null 2>&1; then
  git remote set-url origin "$URL"
else
  git remote add origin "$URL"
fi
ok "origin = $URL"

# ---- 5. Pusha ------------------------------------------------------------
echo
echo "  Pushar main..."
if git push -u origin main 2>&1 | sed 's/^/      /'; then
  echo
  ok "Pushat"
else
  echo
  bad "Pushen misslyckades."
  echo "      Azure DevOps vill oftast ha en Personal Access Token som lösenord:"
  echo "        dev.azure.com → User settings → Personal access tokens → New token"
  echo "        Scope: Code (Read & write)"
  echo "      GitHub: samma sak, eller 'gh auth login'."
  exit 1
fi

echo
echo "  ${B}Länken du skickar till Marcus:${N}"
echo "    ${URL%.git}"
echo
echo "  Nästa steg: ./scripts/go.sh"
