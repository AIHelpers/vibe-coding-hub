#!/usr/bin/env bash
# Builds dist/VibeCodingHub-<version>-x86_64.AppImage from the published desktop app in publish/app.
#
#   dotnet publish src/AiCodeAgent.App -c Release -r linux-x64 --self-contained true \
#     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=1.2.0 -o publish/app
#   bash packaging/linux/make-appimage.sh 1.2.0
#
# Needs bash, curl and sha256sum. desktop-file-validate is used when it is installed.
set -euo pipefail

VERSION="${1:?usage: make-appimage.sh <version>}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.]+)?$ ]] || { echo "Not a version: $VERSION" >&2; exit 1; }

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

APP_EXE="Vibe Coding Hub"
APPDIR="build/AppDir"
OUT="dist/VibeCodingHub-${VERSION}-x86_64.AppImage"

# The packaging tool is pinned and verified: a moving tag would make releases unreproducible.
# To upgrade, change the version and the checksum together (the checksum is listed on the GitHub release page).
APPIMAGETOOL_VERSION="1.9.1"
APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
APPIMAGETOOL="build/tools/appimagetool-${APPIMAGETOOL_VERSION}-x86_64.AppImage"
APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/${APPIMAGETOOL_VERSION}/appimagetool-x86_64.AppImage"

[ -f "publish/app/$APP_EXE" ] || { echo "publish/app/$APP_EXE not found: publish the desktop app first (see the header of this script)." >&2; exit 1; }

if [ ! -f "$APPIMAGETOOL" ]; then
  mkdir -p "$(dirname "$APPIMAGETOOL")"
  curl -fsSL --retry 3 -o "$APPIMAGETOOL" "$APPIMAGETOOL_URL"
fi
echo "$APPIMAGETOOL_SHA256  $APPIMAGETOOL" | sha256sum --check --quiet || { echo "appimagetool checksum mismatch; delete $APPIMAGETOOL and retry." >&2; exit 1; }
chmod +x "$APPIMAGETOOL"

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/lib/vibe-coding-hub" "$APPDIR/usr/share/icons/hicolor/256x256/apps" "$APPDIR/usr/share/icons/hicolor/512x512/apps"
cp -R publish/app/. "$APPDIR/usr/lib/vibe-coding-hub/"
chmod 755 "$APPDIR/usr/lib/vibe-coding-hub/$APP_EXE"
install -m 755 packaging/linux/AppRun "$APPDIR/AppRun"
install -m 644 packaging/linux/vibe-coding-hub.desktop "$APPDIR/vibe-coding-hub.desktop"
install -m 644 packaging/icons/vibe-coding-hub.png "$APPDIR/vibe-coding-hub.png"
install -m 644 packaging/icons/vibe-coding-hub.png "$APPDIR/usr/share/icons/hicolor/256x256/apps/vibe-coding-hub.png"
install -m 644 packaging/icons/vibe-coding-hub-512.png "$APPDIR/usr/share/icons/hicolor/512x512/apps/vibe-coding-hub.png"

if command -v desktop-file-validate >/dev/null 2>&1; then
  desktop-file-validate "$APPDIR/vibe-coding-hub.desktop"
fi

mkdir -p dist
rm -f "$OUT"
# CI runners have no FUSE, so the tool extracts itself instead of mounting.
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$APPIMAGETOOL" "$APPDIR" "$OUT"
chmod 755 "$OUT"
echo "Built $OUT"
