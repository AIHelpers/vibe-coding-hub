# Package managers (winget, Homebrew)

`render-manifests.sh` turns the `SHA256SUMS.txt` of a published release into ready-to-submit files. The Release workflow
runs it for every stable tag (`v1.2.3`, not `v1.2.3-rc.1`) and stores the result in the `package-manager-manifests`
artifact. To render by hand:

```bash
gh release download v1.2.3 --pattern SHA256SUMS.txt
packaging/package-managers/render-manifests.sh 1.2.3 SHA256SUMS.txt
```

| Package | Files | Goes to |
|---|---|---|
| `AIHelpers.VibeCodingHub` (app, Inno Setup installer) | `winget/manifests/a/AIHelpers/VibeCodingHub/<v>/` | pull request to `microsoft/winget-pkgs` |
| `AIHelpers.VibeCodingHub.CLI` (`aiagent`, portable zip) | `winget/manifests/a/AIHelpers/VibeCodingHub/CLI/<v>/` | pull request to `microsoft/winget-pkgs` |
| `vibe-coding-hub` cask (DMG) | `homebrew/Casks/vibe-coding-hub.rb` | tap `AIHelpers/homebrew-tap` |
| `aiagent` formula (tar.gz) | `homebrew/Formula/aiagent.rb` | tap `AIHelpers/homebrew-tap` |

## First submission

1. **winget.** Copy the folders into a fork of `microsoft/winget-pkgs` under `manifests/`, validate with
   `winget validate --manifest <folder>` and test with `winget install --manifest <folder>` on a clean Windows machine,
   then open the pull request. Later versions can be automated with `wingetcreate update` or the
   `vedantmgoyal9/winget-releaser` action.
2. **Homebrew.** Create the repository `AIHelpers/homebrew-tap` (the name must start with `homebrew-`), copy `Casks/` and
   `Formula/` into it and run `brew audit --strict --new --cask vibe-coding-hub` and `brew audit --strict --new aiagent`.
   Users then install with `brew install --cask AIHelpers/tap/vibe-coding-hub` or `brew install AIHelpers/tap/aiagent`.

## Things to know

- The manifests contain the hashes of the release files, so they can only be rendered after the release is published
  and must be re-rendered for every version. A republished file with the same name changes its hash and breaks installs.
- The Windows installer is not code-signed and the macOS app is not notarized yet. winget accepts unsigned installers
  but SmartScreen may warn. The official `homebrew-cask` repository requires notarization, which is why the cask lives
  in an own tap.
- The Windows `ProductCode` in the app manifest is the Inno Setup `AppId` plus `_is1`. If `AppId` in `setup.iss` ever
  changes, winget can no longer match installed versions: that identifier must never change.
