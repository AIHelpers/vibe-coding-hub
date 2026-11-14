#!/usr/bin/env bash
# Builds dist/VibeCodingHub-<version>-<rid>.dmg: "Vibe Coding Hub.app" plus an Applications shortcut for drag-and-drop.
# Run on macOS, once per architecture.
#
#   dotnet publish src/AiCodeAgent.App -c Release -r osx-arm64 --self-contained true \
#     -p:PublishSingleFile=false -p:Version=1.2.0 -o publish/mac-osx-arm64
#   bash packaging/macos/make-dmg.sh osx-arm64 1.2.0
#
# The app is published as a folder, not a single file: inside a signed bundle every native library must carry its own
# signature, and single-file self-extraction at run time would break that.
#
# Signing (environment):
#   SIGN_ID            unset or "-": ad-hoc signature (Apple Silicon refuses to run code with no signature at all, but
#                      Gatekeeper still warns about an unidentified developer).
#                      "Developer ID Application: Name (TEAMID)": hardened-runtime signature, then the DMG is signed,
#                      notarized and stapled. Needs the certificate in the keychain and:
#   APPLE_ID, APPLE_TEAM_ID, APPLE_APP_PASSWORD   (an app-specific password) for notarytool
set -euo pipefail

RID="${1:?usage: make-dmg.sh <osx-arm64|osx-x64> <version>}"
VERSION="${2:?usage: make-dmg.sh <osx-arm64|osx-x64> <version>}"
case "$RID" in osx-arm64|osx-x64) ;; *) echo "Unsupported runtime identifier: $RID" >&2; exit 1 ;; esac
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.]+)?$ ]] || { echo "Not a version: $VERSION" >&2; exit 1; }
# Bundle versions are numeric: the release version without any pre-release suffix.
NUMERIC="${VERSION%%[-+]*}"

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

APP_EXE="Vibe Coding Hub"
PUBLISH="publish/mac-$RID"
WORK="build/dmg-$RID"
APP="$WORK/$APP_EXE.app"
DMG="dist/VibeCodingHub-$VERSION-$RID.dmg"
SIGN_ID="${SIGN_ID:--}"
ENTITLEMENTS="packaging/macos/entitlements.plist"

# Fail before the long work, not after it, when notarization is requested but not configured.
if [ "$SIGN_ID" != "-" ]; then
  for name in APPLE_ID APPLE_TEAM_ID APPLE_APP_PASSWORD; do
    [ -n "${!name:-}" ] || { echo "$name is required to notarize (SIGN_ID is set)." >&2; exit 1; }
  done
fi

[ -f "$PUBLISH/$APP_EXE" ] || { echo "$PUBLISH/$APP_EXE not found: publish the app as a folder first (see the header of this script)." >&2; exit 1; }

rm -rf "$WORK"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
sed "s/__VERSION__/$NUMERIC/g" packaging/macos/Info.plist > "$APP/Contents/Info.plist"
cp packaging/icons/app.icns "$APP/Contents/Resources/app.icns"
chmod 755 "$APP/Contents/MacOS/$APP_EXE"

if [ "$SIGN_ID" = "-" ]; then
  codesign --force --deep --sign - "$APP"
else
  # Inner code first: every Mach-O file (native libraries and executables; the managed .dll files are plain data and
  # are sealed with the bundle). Then the bundle itself. All with the hardened runtime, as notarization requires.
  while IFS= read -r -d '' file; do
    if file -b "$file" | grep -q 'Mach-O'; then
      codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$SIGN_ID" "$file"
    fi
  done < <(find "$APP/Contents/MacOS" -type f -print0)
  codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$SIGN_ID" "$APP"
fi
codesign --verify --deep --strict --verbose=2 "$APP"

# The disk image: the app next to a link to /Applications.
IMAGE_SOURCE="$WORK/image"
mkdir -p "$IMAGE_SOURCE" dist
cp -R "$APP" "$IMAGE_SOURCE/"
ln -s /Applications "$IMAGE_SOURCE/Applications"
rm -f "$DMG"
hdiutil create -volname "Vibe Coding Hub" -srcfolder "$IMAGE_SOURCE" -ov -format UDZO "$DMG"

if [ "$SIGN_ID" != "-" ]; then
  codesign --force --timestamp --sign "$SIGN_ID" "$DMG"
  xcrun notarytool submit "$DMG" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait
  xcrun stapler staple "$DMG"
fi
echo "Built $DMG"
