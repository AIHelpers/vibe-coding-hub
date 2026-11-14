# Release checklist

Releases are cut by pushing a tag `vX.Y.Z`. The Release workflow builds and publishes everything. Test the pipeline first
with **Actions > Release > Run workflow** (no tag): it builds all installers and runs the smoke tests without publishing.

## Automated by the workflow

The `smoke` job installs the build on a clean runner of each system and fails the release when something is wrong:

| System | Checked |
|---|---|
| Windows | silent per-user install, Start menu entry, `aiagent` runs and is on the user PATH, uninstall removes files, shortcut and PATH entry, `install.ps1` (Windows PowerShell 5.1) with checksum |
| Linux | `.deb` installs with its dependencies, `aiagent` and menu entry present, removal leaves nothing behind, AppImage unpacks without FUSE, `install.sh` with checksum |
| macOS (Apple Silicon) | DMG mounts, app bundle signature is valid (`codesign --deep --strict`), bundle version matches, CLI archive runs, `install.sh` with checksum |

The `SHA256SUMS.txt` of a release is generated from the files that are uploaded, and `UpdateCheckerTests` pins the
asset names the update banner relies on.

## By hand, on a clean machine or VM (no .NET SDK)

CI cannot start the desktop app or sign in to a model provider. Before announcing a release, on each system you can reach:

- [ ] The installed app starts from its launcher with the right icon; the setup wizard appears on the first run
- [ ] A sample-project task completes with one real provider
- [ ] Installing the next version over the old one keeps the settings in `~/.aiagent`
- [ ] Windows: the SmartScreen behaviour matches what the README says
- [ ] macOS Intel: the x64 DMG installs and starts (CI only covers Apple Silicon)
- [ ] macOS, once builds are notarized: `spctl -a -vv "/Applications/Vibe Coding Hub.app"` reports "accepted"
- [ ] Linux: the AppImage starts on Ubuntu 22.04 and 24.04 and on one non-Debian distribution
- [ ] The update banner in the previous release offers the right download for the system

## After the release is published

- [ ] `SHA256SUMS.txt` is attached and matches the downloads (`sha256sum -c --ignore-missing SHA256SUMS.txt`)
- [ ] `NUGET_API_KEY` was set, so `VibeCodingHub.Cli` reached NuGet.org (the `tool` job shows a notice when it was skipped)
- [ ] Submit the `package-manager-manifests` artifact: see `packaging/package-managers/README.md`
