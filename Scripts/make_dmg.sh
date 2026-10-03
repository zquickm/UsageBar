#!/usr/bin/env bash
# UsageBar guided-install DMG: app icon + Applications drop link on a
# background that also carries the Gatekeeper hint. Output: UsageBar-v<ver>.dmg
# Note: create-dmg icon coordinates are from the window's TOP-left.
set -euo pipefail
cd "$(dirname "$0")/.."
VER=$(grep MARKETING_VERSION version.env | cut -d= -f2)
OUT="UsageBar-v${VER}.dmg"
command -v create-dmg >/dev/null 2>&1 || brew install create-dmg
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
cp -R UsageBar.app "$STAGE/"
rm -f "$OUT"
create-dmg \
  --volname "UsageBar" \
  --background Scripts/dmg-background.png \
  --window-pos 240 150 --window-size 660 400 \
  --icon-size 96 \
  --icon "UsageBar.app" 165 232 \
  --app-drop-link 495 232 \
  --hide-extension "UsageBar.app" \
  "$OUT" "$STAGE" >/dev/null 2>&1
echo "✓ $OUT"
