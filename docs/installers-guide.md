# Vibe Coding Hub: How to Build Installers for Every Release Platform

*Prepared 13 November 2026 for release v1.2.0. Based on `.github/workflows/release.yml` and `src/AiCodeAgent.App/AiCodeAgent.App.csproj` on `main`. The commands below are standard for each tool but were **not run** against this repo; test each package on a clean machine before publishing. Check tool versions and download URLs when you start, because they change.*

---

## 1. What the release contains today

The release workflow publishes self-contained, single-file builds for four runtime identifiers, as plain archives. The release page shows 10 assets: these eight plus GitHub's two source archives. (The asset names below come from the workflow; the release page's asset list could not be read directly.)

| Platform | Desktop app | CLI (`aiagent`) |
|---|---|---|
| Windows x64 | `vibe-coding-hub-1.2.0-win-x64.zip` | `vibe-coding-hub-cli-1.2.0-win-x64.zip` |
| Linux x64 | `vibe-coding-hub-1.2.0-linux-x64.tar.gz` | `vibe-coding-hub-cli-1.2.0-linux-x64.tar.gz` |
| macOS Intel | `vibe-coding-hub-1.2.0-osx-x64.tar.gz` | `vibe-coding-hub-cli-1.2.0-osx-x64.tar.gz` |
| macOS Apple Silicon | `vibe-coding-hub-1.2.0-osx-arm64.tar.gz` | `vibe-coding-hub-cli-1.2.0-osx-arm64.tar.gz` |

**What to add per platform**

| Platform | Installer | Package manager | Keep |
|---|---|---|---|
| Windows | `VibeCodingHub-1.2.0-win-x64-setup.exe` (Inno Setup) | winget | zip as "portable" |
| macOS (both) | `VibeCodingHub-1.2.0-osx-arm64.dmg`, `…-osx-x64.dmg` | Homebrew cask | — (drop the app tar.gz) |
| Linux | `VibeCodingHub-1.2.0-x86_64.AppImage`, `vibe-coding-hub_1.2.0_amd64.deb` | — | tar.gz |
| CLI, all | — | `dotnet tool`, winget (portable), Homebrew formula | zip / tar.gz |

---

## 2. Prepare the repo first (once)

These four gaps block every installer.

### 2.1 Icons (none exist in the repo)

There is no `Assets` folder and no `ApplicationIcon` in the csproj. Create `packaging/icons/` from one 1024×1024 PNG:

| File | Used by | How to make |
|---|---|---|
| `app.ico` (16–256 px sizes) | Windows exe and installer | ImageMagick: `magick icon-1024.png -define icon:auto-resize=256,128,64,48,32,16 app.ico` |
| `app.icns` | macOS bundle | on a Mac: build an `app.iconset` folder with `sips`, then `iconutil -c icns app.iconset` |
| `vibe-coding-hub.png` (256 and 512 px) | Linux desktop entry | resize the PNG |

Add to `AiCodeAgent.App.csproj`:

```xml
<ApplicationIcon>..\..\packaging\icons\app.ico</ApplicationIcon>
```

Also set the window icon in `MainWindow.axaml` (`Icon="/Assets/app.ico"` with the file added as an `AvaloniaResource`), otherwise Linux and the Windows taskbar show a blank icon.

### 2.2 Version and metadata

Add to the app and CLI csproj (the workflow already passes `-p:Version`):

```xml
<Product>Vibe Coding Hub</Product>
<Company>AIHelpers</Company>
<Copyright>Copyright (c) 2026 AIHelpers</Copyright>
```

### 2.3 The executable name has spaces

`AssemblyName` is `Vibe Coding Hub`, so the binary is `Vibe Coding Hub.exe` / `Vibe Coding Hub`. This works, but every script must quote it. On Linux, install it under `/opt` and expose a space-free launcher name `vibe-coding-hub` (shown below). Do not rename the assembly casually: the update check and user shortcuts may depend on it.

### 2.4 Runtime identifiers

The csproj lists `win-x64;linux-x64;osx-x64` while CI also builds `osx-arm64`. Add `osx-arm64` so local `dotnet publish -r osx-arm64` restores cleanly.

### 2.5 Folder layout to create

```
packaging/
  icons/            app.ico, app.icns, vibe-coding-hub.png
  windows/          setup.iss
  macos/            Info.plist, entitlements.plist, make-dmg.sh
  linux/            vibe-coding-hub.desktop, AppRun, make-appimage.sh, make-deb.sh
```

---

## 3. Windows x64: Setup.exe with Inno Setup

Inno Setup is free, scriptable and builds a per-user installer that needs no admin rights. Choose WiX (MSI) later only if companies ask for MSI deployment.

### 3.1 Publish

```powershell
dotnet publish src/AiCodeAgent.App/AiCodeAgent.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=1.2.0 -o publish/app
dotnet publish src/AiCodeAgent.CLI/AiCodeAgent.CLI.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=1.2.0 -o publish/cli
```

### 3.2 `packaging/windows/setup.iss`

```ini
#define AppName "Vibe Coding Hub"
#define AppExe  "Vibe Coding Hub.exe"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Generate this GUID once (PowerShell: [guid]::NewGuid()) and never change it: upgrades depend on it.
AppId={{PUT-YOUR-GUID-HERE}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=AIHelpers
AppPublisherURL=https://github.com/AIHelpers/vibe-coding-hub
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\dist
OutputBaseFilename=VibeCodingHub-{#AppVersion}-win-x64-setup
SetupIconFile=..\icons\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "addcli"; Description: "Install the aiagent command-line tool and add it to PATH"

[Files]
Source: "..\..\publish\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\..\publish\cli\*"; DestDir: "{app}\cli"; Flags: recursesubdirs ignoreversion; Tasks: addcli

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Per-user PATH entry for the CLI. For an all-users install, change HKCU to HKLM and the Environment key accordingly.
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata};{app}\cli"; Tasks: addcli; Check: NeedsAddPath(ExpandConstant('{app}\cli'))

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function NeedsAddPath(Dir: string): Boolean;
var Paths: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Paths) then begin Result := True; exit; end;
  Result := Pos(';' + Lowercase(Dir) + ';', ';' + Lowercase(Paths) + ';') = 0;
end;
```

### 3.3 Build

```powershell
choco install innosetup -y        # skip if ISCC.exe is already present
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=1.2.0 packaging\windows\setup.iss
```

Silent install for testing and for winget: `VibeCodingHub-1.2.0-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.

### 3.4 Code signing

Unsigned installers trigger the SmartScreen "Windows protected your PC" warning. Options:

| Option | Cost | Notes |
|---|---|---|
| **SignPath Foundation** | Free for open-source projects | Fits an MIT project; apply with the repo; signing runs from GitHub Actions |
| Azure Trusted Signing | Low monthly fee | Eligibility depends on country and identity verification; check before planning on it |
| OV/EV certificate from a CA | Yearly fee | Key must live on a hardware token or cloud HSM |
| Unsigned | Free | Publish SHA-256 checksums and say so in the README |

Sign **both** the app exe (before running ISCC) and the setup exe (after):

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a "publish\app\Vibe Coding Hub.exe"
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a dist\VibeCodingHub-1.2.0-win-x64-setup.exe
```

### 3.5 winget

After the GitHub release exists:

```powershell
winget install wingetcreate
wingetcreate new https://github.com/AIHelpers/vibe-coding-hub/releases/download/v1.2.0/VibeCodingHub-1.2.0-win-x64-setup.exe
# Package identifier: AIHelpers.VibeCodingHub. Installer type: inno. Review, then submit the PR it opens.
wingetcreate update AIHelpers.VibeCodingHub --version 1.3.0 --urls <new url> --submit   # later releases
```

For the CLI alone, submit a second manifest `AIHelpers.VibeCodingHub.CLI` that points to the CLI zip with installer type `zip` and nested type `portable` (command alias `aiagent`).

---

## 4. macOS (Intel and Apple Silicon): .app bundle in a DMG

Build once per architecture on a Mac or on the `macos-latest` runner. Repeat everything below with `RID=osx-arm64` and `RID=osx-x64`.

### 4.1 Publish without single-file

Inside a signed bundle every native library must be signed, and single-file self-extraction fights that. Publish as a folder:

```bash
RID=osx-arm64; VER=1.2.0
dotnet publish src/AiCodeAgent.App/AiCodeAgent.App.csproj -c Release -r $RID --self-contained true \
  -p:PublishSingleFile=false -p:UseAppHost=true -p:Version=$VER -o publish/mac-$RID
```

### 4.2 `packaging/macos/Info.plist`

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Vibe Coding Hub</string>
  <key>CFBundleDisplayName</key><string>Vibe Coding Hub</string>
  <key>CFBundleIdentifier</key><string>info.aihelpers.vibecodinghub</string>
  <key>CFBundleExecutable</key><string>Vibe Coding Hub</string>
  <key>CFBundleIconFile</key><string>app.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>__VERSION__</string>
  <key>CFBundleVersion</key><string>__VERSION__</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
</dict></plist>
```

Set `LSMinimumSystemVersion` to the oldest macOS that .NET 10 supports (check the .NET 10 supported-OS list).

### 4.3 `packaging/macos/entitlements.plist`

The .NET runtime needs these under the hardened runtime:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>com.apple.security.cs.allow-jit</key><true/>
  <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
  <key>com.apple.security.cs.disable-library-validation</key><true/>
</dict></plist>
```

### 4.4 `packaging/macos/make-dmg.sh`

```bash
#!/usr/bin/env bash
set -euo pipefail
RID="$1"; VER="$2"                      # e.g. osx-arm64 1.2.0
SIGN_ID="${SIGN_ID:--}"                 # "-" = ad-hoc; or "Developer ID Application: Name (TEAMID)"
APP="dist/Vibe Coding Hub.app"
DMG="dist/VibeCodingHub-$VER-$RID.dmg"

rm -rf "$APP" && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "publish/mac-$RID/." "$APP/Contents/MacOS/"
sed "s/__VERSION__/$VER/g" packaging/macos/Info.plist > "$APP/Contents/Info.plist"
cp packaging/icons/app.icns "$APP/Contents/Resources/app.icns"
chmod +x "$APP/Contents/MacOS/Vibe Coding Hub"

# Sign inner libraries first, then the bundle.
if [ "$SIGN_ID" = "-" ]; then
  codesign --force --deep --sign - "$APP"
else
  find "$APP/Contents/MacOS" -type f \( -name "*.dylib" -o -perm +111 \) -print0 |
    xargs -0 -I{} codesign --force --timestamp --options runtime \
      --entitlements packaging/macos/entitlements.plist --sign "$SIGN_ID" "{}"
  codesign --force --timestamp --options runtime \
    --entitlements packaging/macos/entitlements.plist --sign "$SIGN_ID" "$APP"
fi
codesign --verify --deep --strict "$APP"

# DMG with a drag-to-Applications link.
STAGE=$(mktemp -d); cp -R "$APP" "$STAGE/"; ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create -volname "Vibe Coding Hub" -srcfolder "$STAGE" -ov -format UDZO "$DMG"

if [ "$SIGN_ID" != "-" ]; then
  codesign --force --timestamp --sign "$SIGN_ID" "$DMG"
  xcrun notarytool submit "$DMG" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" \
    --password "$APPLE_APP_PASSWORD" --wait
  xcrun stapler staple "$DMG"
fi
echo "Built $DMG"
```

Run: `packaging/macos/make-dmg.sh osx-arm64 1.2.0`.

### 4.5 Signing and notarization

| Situation | What users see | What to do |
|---|---|---|
| Developer ID + notarized (Apple Developer Program, yearly fee) | Opens normally | Set `SIGN_ID`, `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` (an app-specific password) |
| Ad-hoc signed only (`SIGN_ID=-`) | Gatekeeper blocks the app as from an unidentified developer | Document the workaround below. Apple Silicon refuses to run completely unsigned code, so ad-hoc signing is the minimum. |

Workaround text for the README when the app is not notarized:

```bash
xattr -dr com.apple.quarantine "/Applications/Vibe Coding Hub.app"
```

In CI, import the Developer ID certificate (a base64 `.p12` secret) into a temporary keychain before running the script.

### 4.6 Homebrew cask

Create a tap repo `AIHelpers/homebrew-tap` with `Casks/vibe-coding-hub.rb`:

```ruby
cask "vibe-coding-hub" do
  version "1.2.0"
  on_arm do
    sha256 "<sha256 of the arm64 dmg>"
    url "https://github.com/AIHelpers/vibe-coding-hub/releases/download/v#{version}/VibeCodingHub-#{version}-osx-arm64.dmg"
  end
  on_intel do
    sha256 "<sha256 of the x64 dmg>"
    url "https://github.com/AIHelpers/vibe-coding-hub/releases/download/v#{version}/VibeCodingHub-#{version}-osx-x64.dmg"
  end
  name "Vibe Coding Hub"
  desc "Desktop app for multi-agent AI coding workflows"
  homepage "https://github.com/AIHelpers/vibe-coding-hub"
  app "Vibe Coding Hub.app"
end
```

Users install with `brew install --cask AIHelpers/tap/vibe-coding-hub`. The official `homebrew-cask` repo requires notarized apps and some project popularity; start with your own tap.

---

## 5. Linux x64: AppImage and .deb

Publish as in the current workflow (single-file is fine on Linux):

```bash
VER=1.2.0
dotnet publish src/AiCodeAgent.App/AiCodeAgent.App.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$VER -o publish/app
dotnet publish src/AiCodeAgent.CLI/AiCodeAgent.CLI.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$VER -o publish/cli
```

### 5.1 `packaging/linux/vibe-coding-hub.desktop`

```ini
[Desktop Entry]
Type=Application
Name=Vibe Coding Hub
Comment=Multi-agent AI coding workflows
Exec=vibe-coding-hub %F
Icon=vibe-coding-hub
Categories=Development;IDE;
Terminal=false
StartupWMClass=Vibe Coding Hub
```

### 5.2 AppImage (runs on most distributions, no install)

`packaging/linux/AppRun`:

```bash
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/vibe-coding-hub/Vibe Coding Hub" "$@"
```

`packaging/linux/make-appimage.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail
VER="$1"; D=build/AppDir
rm -rf "$D" && mkdir -p "$D/usr/lib/vibe-coding-hub" "$D/usr/share/icons/hicolor/256x256/apps"
cp -R publish/app/. "$D/usr/lib/vibe-coding-hub/"
cp packaging/linux/AppRun "$D/AppRun" && chmod +x "$D/AppRun"
cp packaging/linux/vibe-coding-hub.desktop "$D/"
cp packaging/icons/vibe-coding-hub.png "$D/vibe-coding-hub.png"
cp packaging/icons/vibe-coding-hub.png "$D/usr/share/icons/hicolor/256x256/apps/"

curl -fsSL -o appimagetool \
  https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
chmod +x appimagetool
mkdir -p dist
# CI runners have no FUSE, so let the tool extract itself.
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 ./appimagetool "$D" "dist/VibeCodingHub-$VER-x86_64.AppImage"
```

User instructions: `chmod +x VibeCodingHub-1.2.0-x86_64.AppImage && ./VibeCodingHub-1.2.0-x86_64.AppImage`. Some distributions need `libfuse2` to run AppImages; mention it in the README.

### 5.3 .deb (Debian, Ubuntu, Mint)

`packaging/linux/make-deb.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail
VER="$1"; P=build/deb
rm -rf "$P"
mkdir -p "$P/DEBIAN" "$P/opt/vibe-coding-hub/cli" "$P/usr/bin" \
         "$P/usr/share/applications" "$P/usr/share/icons/hicolor/256x256/apps"
cp -R publish/app/. "$P/opt/vibe-coding-hub/"
cp -R publish/cli/. "$P/opt/vibe-coding-hub/cli/"
chmod 755 "$P/opt/vibe-coding-hub/Vibe Coding Hub" "$P/opt/vibe-coding-hub/cli/aiagent"
ln -s "/opt/vibe-coding-hub/Vibe Coding Hub" "$P/usr/bin/vibe-coding-hub"
ln -s /opt/vibe-coding-hub/cli/aiagent "$P/usr/bin/aiagent"
cp packaging/linux/vibe-coding-hub.desktop "$P/usr/share/applications/"
cp packaging/icons/vibe-coding-hub.png "$P/usr/share/icons/hicolor/256x256/apps/"

cat > "$P/DEBIAN/control" <<EOF
Package: vibe-coding-hub
Version: $VER
Section: devel
Priority: optional
Architecture: amd64
Maintainer: AIHelpers <maintainer-address@your-domain>
Homepage: https://github.com/AIHelpers/vibe-coding-hub
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3, libicu74 | libicu72 | libicu70, libx11-6, libice6, libsm6, libfontconfig1
Description: Vibe Coding Hub
 Desktop app and command-line tool for multi-agent AI coding workflows.
EOF
mkdir -p dist
dpkg-deb --build --root-owner-group "$P" "dist/vibe-coding-hub_${VER}_amd64.deb"
```

Install and remove: `sudo apt install ./vibe-coding-hub_1.2.0_amd64.deb`, `sudo apt remove vibe-coding-hub`. Extend the `libicu` alternatives when a new Ubuntu or Debian release ships a newer version.

For Fedora and openSUSE, the AppImage is enough to start with. Add an `.rpm` (with `fpm` or `rpmbuild`) only when users ask.

---

## 6. The CLI on every platform

The CLI needs no installer. Offer three routes.

**1. `dotnet tool` (all platforms, easiest to maintain).** Add to `AiCodeAgent.CLI.csproj`:

```xml
<PackAsTool>true</PackAsTool>
<ToolCommandName>aiagent</ToolCommandName>
<PackageId>VibeCodingHub.Cli</PackageId>
```

```bash
dotnet pack src/AiCodeAgent.CLI -c Release -p:Version=1.2.0 -o dist
dotnet nuget push dist/VibeCodingHub.Cli.1.2.0.nupkg --api-key $NUGET_KEY --source https://api.nuget.org/v3/index.json
# users:
dotnet tool install -g VibeCodingHub.Cli
```

Check first that the package ID is free on nuget.org. A tool package is framework-dependent, so users need the .NET 10 runtime.

**2. Install scripts** that download the existing CLI archive for the user's platform and put `aiagent` on PATH: `install.sh` (Linux and macOS, to `~/.local/bin`) and `install.ps1` (Windows, to `%LOCALAPPDATA%\Programs\aiagent`). The README already mentions `install.sh`, so adding it also fixes the docs. Ad-hoc sign the macOS CLI binaries in CI (`codesign --force --sign - aiagent`) so Apple Silicon runs them.

**3. Package managers:** the winget portable manifest (section 3.5), and a Homebrew formula in the same tap that downloads the `osx-*` CLI tar.gz and installs `aiagent` into `bin`.

---

## 7. Wire it into `release.yml`

Keep the existing `publish` job and add packaging steps per matrix entry.

```yaml
      # --- Windows ---
      - name: Build installer
        if: matrix.rid == 'win-x64'
        shell: pwsh
        run: |
          choco install innosetup -y --no-progress
          & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=${{ steps.ver.outputs.version }} packaging\windows\setup.iss

      # --- Linux ---
      - name: Build AppImage and deb
        if: matrix.rid == 'linux-x64'
        run: |
          bash packaging/linux/make-appimage.sh ${{ steps.ver.outputs.version }}
          bash packaging/linux/make-deb.sh ${{ steps.ver.outputs.version }}

      # --- macOS (both RIDs) ---
      - name: Publish app as a folder for the bundle
        if: startsWith(matrix.rid, 'osx')
        run: >
          dotnet publish src/AiCodeAgent.App/AiCodeAgent.App.csproj -c Release -r ${{ matrix.rid }}
          --self-contained true -p:PublishSingleFile=false
          -p:Version=${{ steps.ver.outputs.version }} -o publish/mac-${{ matrix.rid }}
      - name: Build DMG
        if: startsWith(matrix.rid, 'osx')
        env:
          SIGN_ID: ${{ secrets.MACOS_SIGN_ID || '-' }}
          APPLE_ID: ${{ secrets.APPLE_ID }}
          APPLE_TEAM_ID: ${{ secrets.APPLE_TEAM_ID }}
          APPLE_APP_PASSWORD: ${{ secrets.APPLE_APP_PASSWORD }}
        run: bash packaging/macos/make-dmg.sh ${{ matrix.rid }} ${{ steps.ver.outputs.version }}
```

Then:

1. Add `dist/*.exe`, `dist/*.dmg`, `dist/*.AppImage` and `dist/*.deb` to the `upload-artifact` paths.
2. In the `release` job, generate checksums before creating the release: `cd dist && sha256sum * > SHA256SUMS.txt`.
3. Signing steps stay optional: when the secrets are absent the build still succeeds and produces unsigned packages.
4. Use `workflow_dispatch` to test the whole pipeline without tagging; the installers appear as workflow artifacts.

---

## 8. Check the in-app update banner

The app's update banner has a **Download** button. After installers exist, confirm which asset it opens for each OS. It should pick the setup exe on Windows, the DMG matching the CPU on macOS, and the AppImage or deb on Linux, and fall back to the release page when no match is found. Asset file names are now part of a contract with the app: keep the naming pattern stable between releases.

---

## 9. Test checklist per release

Run on a **clean** machine or VM (no .NET SDK installed) for each platform:

- [ ] Installer runs without admin rights (Windows) and creates a Start menu entry with the right icon
- [ ] App starts from the launcher; the setup wizard appears on first run
- [ ] `aiagent --help` works in a **new** terminal window
- [ ] Sample project task completes with one real provider
- [ ] Installing the next version over the old one keeps settings in `~/.aiagent`
- [ ] Uninstall removes the program files, shortcuts and PATH entry, and leaves user data
- [ ] Windows: the SmartScreen behaviour matches what the README says
- [ ] macOS: `spctl -a -vv "/Applications/Vibe Coding Hub.app"` reports "accepted" (notarized builds), on both Intel and Apple Silicon
- [ ] Linux: AppImage starts on Ubuntu 22.04 and 24.04 and one non-Debian distribution; the deb installs with `apt` and pulls its dependencies
- [ ] `SHA256SUMS.txt` matches the uploaded files

---

## 10. Suggested order

1. Icons and csproj metadata (section 2). Half a day.
2. Linux AppImage and deb: no certificates needed. Half a day.
3. Windows Setup.exe unsigned; apply to SignPath Foundation in parallel. One day.
4. macOS DMG, ad-hoc signed, with the workaround documented. One day, needs a Mac for testing.
5. `dotnet tool` and install scripts for the CLI. Half a day.
6. winget manifest and Homebrew tap once the installers are on a release.
7. Notarization and Windows signing when the certificates arrive.

**Later option: Velopack.** It builds a Windows installer, a macOS package and a Linux AppImage with one tool (`vpk pack`) and adds in-app delta updates. It needs a small code change in `Program.cs` and replaces the current update banner logic, so treat it as a separate story after the native installers work.
