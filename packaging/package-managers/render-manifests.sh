#!/usr/bin/env bash
# Renders the package-manager manifests of one release from its SHA256SUMS.txt.
#
#   packaging/package-managers/render-manifests.sh <version> <SHA256SUMS.txt> [output-dir]
#
# Output (default: dist/package-managers):
#   winget/manifests/a/AIHelpers/VibeCodingHub/<v>/        desktop app (Inno Setup installer)   -> microsoft/winget-pkgs
#   winget/manifests/a/AIHelpers/VibeCodingHub/CLI/<v>/    aiagent (portable zip)               -> microsoft/winget-pkgs
#   homebrew/Casks/vibe-coding-hub.rb                      desktop app (DMG)                    -> AIHelpers/homebrew-tap
#   homebrew/Formula/aiagent.rb                            aiagent (tar.gz)                     -> AIHelpers/homebrew-tap
#
# Pre-release versions are refused: neither winget nor Homebrew should point at them.
set -euo pipefail

die() { printf 'error: %s\n' "$*" >&2; exit 1; }

[ $# -ge 2 ] && [ $# -le 3 ] || die "usage: $0 <version> <SHA256SUMS.txt> [output-dir]"
version="${1#v}"
sums="$2"
out="${3:-dist/package-managers}"

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "version '$version' must look like 1.2.3 (no pre-release suffix)"
[ -f "$sums" ] || die "checksum file not found: $sums"

# SHA-256 of a release file, taken from SHA256SUMS.txt ("<hash>  <name>", binary mode prefixes the name with *).
sum_of() {
  local h
  h="$(awk -v f="$1" '$2 == f || $2 == "*" f { print $1; exit }' "$sums")"
  [[ "$h" =~ ^[0-9a-fA-F]{64}$ ]] || die "$1 is not listed in $sums"
  printf '%s' "$h"
}

repo="https://github.com/AIHelpers/vibe-coding-hub"
base="$repo/releases/download/v$version"

setup="VibeCodingHub-$version-win-x64-setup.exe"
cli_zip="vibe-coding-hub-cli-$version-win-x64.zip"
dmg_arm="VibeCodingHub-$version-osx-arm64.dmg"
dmg_x64="VibeCodingHub-$version-osx-x64.dmg"
cli_arm="vibe-coding-hub-cli-$version-osx-arm64.tar.gz"
cli_x64="vibe-coding-hub-cli-$version-osx-x64.tar.gz"

setup_sha="$(sum_of "$setup" | tr 'a-f' 'A-F')"
cli_zip_sha="$(sum_of "$cli_zip" | tr 'a-f' 'A-F')"
dmg_arm_sha="$(sum_of "$dmg_arm" | tr 'A-F' 'a-f')"
dmg_x64_sha="$(sum_of "$dmg_x64" | tr 'A-F' 'a-f')"
cli_arm_sha="$(sum_of "$cli_arm" | tr 'A-F' 'a-f')"
cli_x64_sha="$(sum_of "$cli_x64" | tr 'A-F' 'a-f')"

rm -rf "$out"

# ---------------------------------------------------------------- winget (manifest schema 1.6.0)
schema="https://aka.ms/winget-manifest"
winget_set() { # <PackageIdentifier> <dir> <PackageName> <ShortDescription> <installer-block-file>
  local id="$1" dir="$2" name="$3" short="$4" installer="$5"
  mkdir -p "$dir"
  cat > "$dir/$id.yaml" <<YAML
# yaml-language-server: \$schema=$schema.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
YAML
  { printf '# yaml-language-server: $schema=%s.installer.1.6.0.schema.json\n' "$schema"
    printf 'PackageIdentifier: %s\nPackageVersion: %s\n' "$id" "$version"
    cat "$installer"
    printf 'ManifestType: installer\nManifestVersion: 1.6.0\n'
  } > "$dir/$id.installer.yaml"
  cat > "$dir/$id.locale.en-US.yaml" <<YAML
# yaml-language-server: \$schema=$schema.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: en-US
Publisher: AIHelpers
PublisherUrl: https://github.com/AIHelpers
PublisherSupportUrl: $repo/issues
PackageName: $name
PackageUrl: $repo
License: MIT
LicenseUrl: $repo/blob/main/LICENSE
ShortDescription: $short
Tags:
- ai
- agent
- coding
- llm
ReleaseNotesUrl: $repo/releases/tag/v$version
ManifestType: defaultLocale
ManifestVersion: 1.6.0
YAML
}

tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

cat > "$tmp/app.yaml" <<YAML
MinimumOSVersion: 10.0.14393.0
InstallerType: inno
Scope: user
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
Installers:
- Architecture: x64
  InstallerUrl: $base/$setup
  InstallerSha256: $setup_sha
  ProductCode: '{CF84457E-26BC-4F88-9CA2-412A5A74728C}_is1'
YAML
winget_set "AIHelpers.VibeCodingHub" "$out/winget/manifests/a/AIHelpers/VibeCodingHub/$version" \
  "Vibe Coding Hub" "Desktop app for multi-agent AI coding workflows." "$tmp/app.yaml"

cat > "$tmp/cli.yaml" <<YAML
MinimumOSVersion: 10.0.14393.0
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: aiagent.exe
  PortableCommandAlias: aiagent
Commands:
- aiagent
Installers:
- Architecture: x64
  InstallerUrl: $base/$cli_zip
  InstallerSha256: $cli_zip_sha
YAML
winget_set "AIHelpers.VibeCodingHub.CLI" "$out/winget/manifests/a/AIHelpers/VibeCodingHub/CLI/$version" \
  "Vibe Coding Hub CLI" "Command-line tool (aiagent) for multi-agent AI coding workflows." "$tmp/cli.yaml"

# ---------------------------------------------------------------- Homebrew
mkdir -p "$out/homebrew/Casks" "$out/homebrew/Formula"

cat > "$out/homebrew/Casks/vibe-coding-hub.rb" <<RUBY
cask "vibe-coding-hub" do
  version "$version"

  on_arm do
    sha256 "$dmg_arm_sha"
    url "$repo/releases/download/v#{version}/VibeCodingHub-#{version}-osx-arm64.dmg"
  end
  on_intel do
    sha256 "$dmg_x64_sha"
    url "$repo/releases/download/v#{version}/VibeCodingHub-#{version}-osx-x64.dmg"
  end

  name "Vibe Coding Hub"
  desc "Desktop app for multi-agent AI coding workflows"
  homepage "$repo"

  depends_on macos: ">= :sonoma"

  app "Vibe Coding Hub.app"

  caveats <<~EOS
    The app is not notarized yet. If macOS refuses to open it, clear the download flag once:
      xattr -dr com.apple.quarantine "/Applications/Vibe Coding Hub.app"
  EOS
end
RUBY

cat > "$out/homebrew/Formula/aiagent.rb" <<RUBY
class Aiagent < Formula
  desc "Command-line tool for multi-agent AI coding workflows"
  homepage "$repo"
  version "$version"
  license "MIT"

  on_arm do
    url "$repo/releases/download/v#{version}/vibe-coding-hub-cli-#{version}-osx-arm64.tar.gz"
    sha256 "$cli_arm_sha"
  end
  on_intel do
    url "$repo/releases/download/v#{version}/vibe-coding-hub-cli-#{version}-osx-x64.tar.gz"
    sha256 "$cli_x64_sha"
  end

  def install
    bin.install "aiagent"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/aiagent --version")
  end
end
RUBY

printf 'Rendered manifests for v%s in %s\n' "$version" "$out"
