#!/usr/bin/env bash
# Builds dist/vibe-coding-hub_<version>_amd64.deb from the published desktop app (publish/app) and CLI (publish/cli).
#
#   dotnet publish ... -o publish/app      (desktop app) and      dotnet publish ... -o publish/cli      (CLI)
#   bash packaging/linux/make-deb.sh 1.2.0
#
# Installs to /opt/vibe-coding-hub and puts `vibe-coding-hub` and `aiagent` on the PATH.
# Set MAINTAINER="Name <address>" to override the Maintainer field.
set -euo pipefail

VERSION="${1:?usage: make-deb.sh <version>}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.]+)?$ ]] || { echo "Not a version: $VERSION" >&2; exit 1; }
# Debian sorts "1.2.0~dev.5" before "1.2.0"; a hyphen would be read as the start of the packaging revision.
DEB_VERSION="${VERSION//-/\~}"   # the backslash stops bash from expanding ~ to the home directory

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

APP_EXE="Vibe Coding Hub"
ROOT="build/deb"
OUT="dist/vibe-coding-hub_${DEB_VERSION}_amd64.deb"
MAINTAINER="${MAINTAINER:-AIHelpers <noreply@github.com>}"

[ -f "publish/app/$APP_EXE" ] || { echo "publish/app/$APP_EXE not found: publish the desktop app first." >&2; exit 1; }
[ -f "publish/cli/aiagent" ] || { echo "publish/cli/aiagent not found: publish the CLI first." >&2; exit 1; }

rm -rf "$ROOT"
mkdir -p "$ROOT/DEBIAN" "$ROOT/opt/vibe-coding-hub/cli" "$ROOT/usr/bin" "$ROOT/usr/share/applications" \
         "$ROOT/usr/share/icons/hicolor/256x256/apps" "$ROOT/usr/share/icons/hicolor/512x512/apps"
cp -R publish/app/. "$ROOT/opt/vibe-coding-hub/"
cp -R publish/cli/. "$ROOT/opt/vibe-coding-hub/cli/"
ln -s "/opt/vibe-coding-hub/$APP_EXE" "$ROOT/usr/bin/vibe-coding-hub"
ln -s /opt/vibe-coding-hub/cli/aiagent "$ROOT/usr/bin/aiagent"
install -m 644 packaging/linux/vibe-coding-hub.desktop "$ROOT/usr/share/applications/vibe-coding-hub.desktop"
install -m 644 packaging/icons/vibe-coding-hub.png "$ROOT/usr/share/icons/hicolor/256x256/apps/vibe-coding-hub.png"
install -m 644 packaging/icons/vibe-coding-hub-512.png "$ROOT/usr/share/icons/hicolor/512x512/apps/vibe-coding-hub.png"

# Normalise permissions: the build host's umask must not leak into the package.
find "$ROOT" -type d -exec chmod 755 {} +
find "$ROOT/opt" -type f -exec chmod 644 {} +
chmod 755 "$ROOT/opt/vibe-coding-hub/$APP_EXE" "$ROOT/opt/vibe-coding-hub/cli/aiagent"

INSTALLED_SIZE="$(du -sk "$ROOT" | cut -f1)"
cat > "$ROOT/DEBIAN/control" <<CONTROL
Package: vibe-coding-hub
Version: ${DEB_VERSION}
Section: devel
Priority: optional
Architecture: amd64
Maintainer: ${MAINTAINER}
Installed-Size: ${INSTALLED_SIZE}
Homepage: https://github.com/AIHelpers/vibe-coding-hub
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3t64 | libssl3, libicu76 | libicu74 | libicu72 | libicu70, libx11-6, libice6, libsm6, libfontconfig1
Recommends: libxcursor1, libxext6, libxi6, libxrandr2, libgl1
Description: Multi-agent AI coding workflows
 Vibe Coding Hub is a desktop app and command-line tool (aiagent) in which teams of
 AI agents work on your code in visual flows. Bring your own model; every change is
 shown as a diff before it is applied.
CONTROL

mkdir -p dist
rm -f "$OUT"
# xz: readable by every dpkg, unlike the zstd default of recent dpkg versions.
dpkg-deb --build --root-owner-group -Zxz "$ROOT" "$OUT"
echo "Built $OUT"
