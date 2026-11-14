#!/usr/bin/env bash
# Checks the macOS disk image and CLI archive of a build (Apple Silicon runner).
#   packaging/smoke/smoke-macos.sh <artifact-dir> <version>
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
# shellcheck source=lib.sh
source "$here/lib.sh"

dir="$(cd "${1:?artifact directory}" && pwd)"
version="${2:?version}"
rid=osx-arm64

dmg="$(ls "$dir"/VibeCodingHub-*-$rid.dmg 2>/dev/null | head -n1)"; [ -n "$dmg" ] || fail "no $rid .dmg in $dir"

step "DMG mounts and holds a valid app bundle"
mnt="$(mktemp -d)"
hdiutil attach -nobrowse -readonly -mountpoint "$mnt" "$dmg" >/dev/null
trap 'hdiutil detach "$mnt" -quiet || true' EXIT
app="$mnt/Vibe Coding Hub.app"
[ -d "$app" ] || fail "the disk image does not contain 'Vibe Coding Hub.app'"
[ -L "$mnt/Applications" ] || fail "the disk image has no Applications shortcut"
shown="$(plutil -extract CFBundleShortVersionString raw "$app/Contents/Info.plist")"
case "$version" in "$shown"*) ;; *) fail "bundle version '$shown' does not match build version '$version'" ;; esac
codesign --verify --deep --strict --verbose=2 "$app" || fail "the bundle signature is not valid (codesign --deep --strict)"

step "CLI archive runs after an ad-hoc signature"
work="$(mktemp -d)"
mkdir "$work/cli"
tar -xzf "$dir/vibe-coding-hub-cli-$version-$rid.tar.gz" -C "$work/cli"
"$work/cli/aiagent" --version | grep -F "$version" || fail "the CLI from the archive does not report version $version"

step "install.sh installs the CLI archive and verifies its checksum"
start_release_server "$dir" 8766
test_install_script "$root" "$version" 8766 "$work/bin"

printf '\nmacOS smoke test passed.\n'
