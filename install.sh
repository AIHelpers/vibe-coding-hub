#!/usr/bin/env bash
# Installs the Vibe Coding Hub CLI (aiagent) on Linux or macOS.
#
#   curl -fsSL https://raw.githubusercontent.com/AIHelpers/vibe-coding-hub/main/install.sh | bash
#
# Options (environment variables):
#   VERSION       release to install, e.g. 1.2.0 (default: latest)
#   INSTALL_DIR   target directory (default: ~/.local/bin)
#   REPO          GitHub repository (default: AIHelpers/vibe-coding-hub)
#   BASE_URL      download root, overrides GitHub (used by tests and mirrors)
#
# The archive is verified against SHA256SUMS.txt of the same release before anything is installed.
set -euo pipefail

REPO="${REPO:-AIHelpers/vibe-coding-hub}"
INSTALL_DIR="${INSTALL_DIR:-$HOME/.local/bin}"
VERSION="${VERSION:-latest}"

say() { printf '%s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "'$1' is required but not installed"; }

need curl
need tar

case "$(uname -s)" in
  Linux)  os=linux ;;
  Darwin) os=osx ;;
  *) die "unsupported operating system: $(uname -s). On Windows use install.ps1" ;;
esac
case "$(uname -m)" in
  x86_64|amd64)  arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) die "unsupported CPU architecture: $(uname -m)" ;;
esac
rid="$os-$arch"
case "$rid" in
  linux-x64|osx-x64|osx-arm64) ;;
  *) die "no prebuilt CLI for $rid. Install it with: dotnet tool install -g VibeCodingHub.Cli" ;;
esac

if command -v sha256sum >/dev/null 2>&1; then
  sha256() { sha256sum "$1" | cut -d' ' -f1; }
elif command -v shasum >/dev/null 2>&1; then
  sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }
else
  die "'sha256sum' or 'shasum' is required to verify the download"
fi

if [ -z "${BASE_URL:-}" ]; then
  if [ "$VERSION" = "latest" ]; then
    # The redirect target of /releases/latest carries the tag; this avoids the rate-limited API.
    final="$(curl -fsSL -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest")" \
      || die "could not look up the latest release of $REPO"
    tag="${final##*/}"
    case "$tag" in v*) ;; *) die "no release found for $REPO" ;; esac
  else
    tag="v${VERSION#v}"
  fi
  BASE_URL="https://github.com/$REPO/releases/download/$tag"
else
  tag="v${VERSION#v}"
fi
version="${tag#v}"
[ "$version" != "latest" ] || die "VERSION must be set when BASE_URL is used"

archive="vibe-coding-hub-cli-$version-$rid.tar.gz"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

say "Downloading $archive"
curl -fsSL -o "$tmp/$archive" "$BASE_URL/$archive" || die "download failed: $BASE_URL/$archive"
curl -fsSL -o "$tmp/SHA256SUMS.txt" "$BASE_URL/SHA256SUMS.txt" || die "download failed: $BASE_URL/SHA256SUMS.txt"

expected="$(awk -v f="$archive" '$2 == f || $2 == "*" f { print $1; exit }' "$tmp/SHA256SUMS.txt")"
[ -n "$expected" ] || die "$archive is not listed in SHA256SUMS.txt"
actual="$(sha256 "$tmp/$archive")"
[ "$expected" = "$actual" ] || die "checksum mismatch for $archive (expected $expected, got $actual)"
say "Checksum OK"

mkdir -p "$tmp/x" "$INSTALL_DIR"
tar -xzf "$tmp/$archive" -C "$tmp/x"
[ -f "$tmp/x/aiagent" ] || die "the archive does not contain 'aiagent'"
install -m 0755 "$tmp/x/aiagent" "$INSTALL_DIR/aiagent"

if [ "$os" = osx ]; then
  # A download from the internet is quarantined; the binary is not notarized yet.
  xattr -d com.apple.quarantine "$INSTALL_DIR/aiagent" 2>/dev/null || true
fi

say "Installed aiagent $version to $INSTALL_DIR/aiagent"
case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *) say "Add it to your PATH:  export PATH=\"$INSTALL_DIR:\$PATH\"" ;;
esac
