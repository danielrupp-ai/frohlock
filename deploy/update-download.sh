#!/usr/bin/env bash
# Holt eine Release-Version auf den Server und stellt sie zum Download bereit.
# Aufruf auf dem Server:
#   bash update-download.sh 0.28.0            # Windows-Setup  -> /download
#   bash update-download.sh android 1.0.0     # Android-APK    -> /download/android (+ QR-Code, Fern-Update)
set -euo pipefail
DL_DIR="${FROHLOCK_DOWNLOAD_DIR:-$HOME/apps/frohlock/data/download}"
REPO="https://github.com/danielrupp-ai/frohlock/releases/download"
mkdir -p "$DL_DIR"

if [ "${1:-}" = "android" ]; then
  VER="${2:?Version angeben, z. B. android 1.0.0}"
  BASE="$REPO/android-v${VER}"
  echo "Lade $BASE/FrohLock-${VER}.apk ..."
  curl -fsSL -o "$DL_DIR/FrohLock.apk.tmp" "$BASE/FrohLock-${VER}.apk"
  curl -fsSL -o "$DL_DIR/android-cert.txt.tmp" "$BASE/android-cert.txt"
  curl -fsSL -o "$DL_DIR/android-sums.tmp" "$BASE/SHA256SUMS.txt"
  want=$(awk '{print $1}' "$DL_DIR/android-sums.tmp")
  have=$(sha256sum "$DL_DIR/FrohLock.apk.tmp" | awk '{print $1}')
  [ "$want" = "$have" ] || { echo "Prüfsumme falsch ($have statt $want) – abgebrochen"; rm -f "$DL_DIR"/*.tmp; exit 1; }
  # Zertifikat darf sich NIE ändern – sonst passen QR-Code und Updates nicht mehr.
  if [ -f "$DL_DIR/android-cert.txt" ] && ! cmp -s "$DL_DIR/android-cert.txt" "$DL_DIR/android-cert.txt.tmp"; then
    echo "⚠ Signaturzertifikat hat sich geändert – abgebrochen (falscher Schlüssel?)"; rm -f "$DL_DIR"/*.tmp; exit 1
  fi
  mv "$DL_DIR/FrohLock.apk.tmp" "$DL_DIR/FrohLock.apk"
  mv "$DL_DIR/android-cert.txt.tmp" "$DL_DIR/android-cert.txt"
  rm -f "$DL_DIR/android-sums.tmp"
  # Version zuletzt schreiben: erst jetzt bieten Server/Manifest das Update an.
  printf "%s" "$VER" > "$DL_DIR/android-version.txt"
  echo "Bereitgestellt: $(ls -lh "$DL_DIR/FrohLock.apk" | awk '{print $5}') (Android v$VER) – kein Neustart nötig"
  echo "Live unter: https://frohlock.froehlichdienste.de/download/android"
  exit 0
fi

VER="${1:?Version angeben, z. B. 0.4.0}"
URL="$REPO/v${VER}/FrohLockSetup-${VER}.exe"
echo "Lade $URL ..."
curl -fsSL -o "$DL_DIR/FrohLockSetup.exe.tmp" "$URL"
mv "$DL_DIR/FrohLockSetup.exe.tmp" "$DL_DIR/FrohLockSetup.exe"
printf "%s" "$VER" > "$DL_DIR/version.txt"
echo "Bereitgestellt: $(ls -lh "$DL_DIR/FrohLockSetup.exe" | awk '{print $5}') (v$VER)"
echo "Live unter: https://frohlock.froehlichdienste.de/download"
