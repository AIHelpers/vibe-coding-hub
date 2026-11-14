#!/usr/bin/env bash
# Installs the Linux packages of a build on a clean machine and checks that they work and uninstall cleanly.
#   packaging/smoke/smoke-linux.sh <artifact-dir> <version>
# Needs sudo (apt). Meant for CI runners, not for a developer machine.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
# shellcheck source=lib.sh
source "$here/lib.sh"

dir="$(cd "${1:?artifact directory}" && pwd)"
version="${2:?version}"

deb="$(ls "$dir"/vibe-coding-hub_*_amd64.deb 2>/dev/null | head -n1)"; [ -n "$deb" ] || fail "no .deb in $dir"
appimage="$(ls "$dir"/VibeCodingHub-*-x86_64.AppImage 2>/dev/null | head -n1)"; [ -n "$appimage" ] || fail "no AppImage in $dir"

step ".deb installs with its dependencies"
sudo apt-get update -qq
sudo apt-get install -y "$deb"
command -v aiagent >/dev/null || fail "aiagent is not on PATH after installing the .deb"
aiagent --version | grep -F "$version" || fail "aiagent from the .deb does not report version $version"
[ -x /usr/bin/vibe-coding-hub ] || fail "/usr/bin/vibe-coding-hub is missing"
[ -f /usr/share/applications/vibe-coding-hub.desktop ] || fail "the menu entry is missing"
[ -f /usr/share/icons/hicolor/256x256/apps/vibe-coding-hub.png ] || fail "the menu icon is missing"

step ".deb uninstalls cleanly"
sudo apt-get remove -y vibe-coding-hub
if command -v aiagent >/dev/null; then fail "aiagent is still on PATH after removing the .deb"; fi
[ ! -e /opt/vibe-coding-hub ] || fail "/opt/vibe-coding-hub is still there after removing the .deb"

step "AppImage unpacks without FUSE"
work="$(mktemp -d)"
cp "$appimage" "$work/app.AppImage"; chmod +x "$work/app.AppImage"
(cd "$work" && ./app.AppImage --appimage-extract >/dev/null)
[ -x "$work/squashfs-root/AppRun" ] || fail "the AppImage does not contain an executable AppRun"
[ -f "$work/squashfs-root/vibe-coding-hub.desktop" ] || fail "the AppImage does not contain its .desktop file"

step "install.sh installs the CLI archive and verifies its checksum"
start_release_server "$dir" 8765
test_install_script "$root" "$version" 8765 "$work/bin"

printf '\nLinux smoke test passed.\n'
