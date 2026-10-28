#!/usr/bin/env bash
# Monta o pacote .deb do Confluir a partir do resultado do `dotnet publish` para linux-x64.
# Uso: packaging/linux/build-deb.sh <pasta-do-publish> <versao> <arquivo-de-saida.deb>
set -euo pipefail

PUBLISH_DIR="$1"
VERSION="$2"
OUTPUT="$3"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PKG="$(mktemp -d)/confluir"

mkdir -p "$PKG/DEBIAN" "$PKG/opt/confluir" "$PKG/usr/bin" "$PKG/usr/share/applications"
cp -r "$PUBLISH_DIR"/. "$PKG/opt/confluir/"
chmod 755 "$PKG/opt/confluir/Confluir"
ln -s /opt/confluir/Confluir "$PKG/usr/bin/confluir"
install -m 644 "$ROOT/packaging/linux/confluir.desktop" "$PKG/usr/share/applications/confluir.desktop"

for size in 16 24 32 48 64 128 256 512; do
  dir="$PKG/usr/share/icons/hicolor/${size}x${size}/apps"
  mkdir -p "$dir"
  install -m 644 "$ROOT/assets/icone/png/confluir-${size}.png" "$dir/confluir.png"
done

INSTALLED_KB=$(du -sk "$PKG/opt" | cut -f1)

# ICU: a interface usa formatos de data e número de cada idioma (o .NET carrega a versão que existir).
# libsecret: cofre de senhas. WebKitGTK: exibe emails em HTML (sem ele o leitor usa texto puro).
cat > "$PKG/DEBIAN/control" <<EOF
Package: confluir
Version: ${VERSION}
Section: mail
Priority: optional
Architecture: amd64
Installed-Size: ${INSTALLED_KB}
Maintainer: Confluir <https://github.com/igoreusttaquio/Confluir>
Homepage: https://github.com/igoreusttaquio/Confluir
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl1.1, libfontconfig1, libsecret-1-0, libicu76 | libicu75 | libicu74 | libicu72 | libicu70 | libicu67 | libicu66
Recommends: libwebkit2gtk-4.1-0 | libwebkit2gtk-4.0-37
Description: All your email accounts in one place
 Confluir is a desktop email client for Gmail, Outlook, Yahoo and any
 IMAP server, with a unified inbox, offline cache, safe HTML rendering,
 rules and filters. Interface in Portuguese, English and Spanish.
EOF

cat > "$PKG/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
exit 0
EOF
cp "$PKG/DEBIAN/postinst" "$PKG/DEBIAN/postrm"
chmod 755 "$PKG/DEBIAN/postinst" "$PKG/DEBIAN/postrm"

mkdir -p "$(dirname "$OUTPUT")"
dpkg-deb --build --root-owner-group "$PKG" "$OUTPUT"
echo "Pacote: $OUTPUT"
