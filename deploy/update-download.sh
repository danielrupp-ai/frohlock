#!/usr/bin/env bash
# Holt eine Release-Version auf den Server und stellt sie unter /download bereit.
# Aufruf auf dem Server:  bash update-download.sh 0.4.0
set -euo pipefail
VER="${1:?Version angeben, z. B. 0.4.0}"
DL_DIR="${FROHLOCK_DOWNLOAD_DIR:-$HOME/apps/frohlock/data/download}"
URL="https://github.com/danielrupp-ai/frohlock/releases/download/v${VER}/FrohLockSetup-${VER}.exe"

mkdir -p "$DL_DIR"
echo "Lade $URL ..."
curl -fsSL -o "$DL_DIR/FrohLockSetup.exe.tmp" "$URL"
mv "$DL_DIR/FrohLockSetup.exe.tmp" "$DL_DIR/FrohLockSetup.exe"
printf "%s" "$VER" > "$DL_DIR/version.txt"
echo "Bereitgestellt: $(ls -lh "$DL_DIR/FrohLockSetup.exe" | awk '{print $5}') (v$VER)"
echo "Live unter: https://frohlock.froehlichdienste.de/download"
