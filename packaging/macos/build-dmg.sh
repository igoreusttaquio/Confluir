#!/usr/bin/env bash
# Monta Confluir.app e o .dmg a partir do resultado do `dotnet publish` para osx-arm64 ou osx-x64.
# Uso: packaging/macos/build-dmg.sh <pasta-do-publish> <versao> <arquivo-de-saida.dmg>
set -euo pipefail

PUBLISH_DIR="$1"
VERSION="$2"
OUTPUT="$3"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
STAGE="$(mktemp -d)"
APP="$STAGE/Confluir.app"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/Confluir"
cp "$ROOT/assets/icone/confluir.icns" "$APP/Contents/Resources/confluir.icns"

# CFBundleShortVersionString aceita só números (1.2.3); a versão completa vai em CFBundleVersion.
SHORT_VERSION="${VERSION%%-*}"
sed -e "s/__SHORT_VERSION__/${SHORT_VERSION}/" -e "s/__VERSION__/${VERSION}/" \
  "$ROOT/packaging/macos/Info.plist" > "$APP/Contents/Info.plist"

# Assinatura ad hoc: sem ela o macOS não executa o app em Apple Silicon.
# (Para distribuir sem aviso do Gatekeeper é preciso um Developer ID e notarização.)
codesign --force --deep --sign - "$APP"

ln -s /Applications "$STAGE/Applications"
mkdir -p "$(dirname "$OUTPUT")"
hdiutil create -volname "Confluir" -srcfolder "$STAGE" -ov -format UDZO "$OUTPUT"
echo "Imagem: $OUTPUT"
