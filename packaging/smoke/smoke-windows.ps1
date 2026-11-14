<#
.SYNOPSIS
  Installs the Windows setup of a build silently on a clean machine and checks that it works and uninstalls cleanly.
.DESCRIPTION
  packaging/smoke/smoke-windows.ps1 -ArtifactDir <dir> -Version <version> -RepoRoot <repo>
  Meant for CI runners. It changes the user PATH of the machine it runs on.
#>
param(
  [Parameter(Mandatory)][string]$ArtifactDir,
  [Parameter(Mandatory)][string]$Version,
  [Parameter(Mandatory)][string]$RepoRoot
)
$ErrorActionPreference = 'Stop'

function Fail($m) { throw "SMOKE FAILED: $m" }
function Step($m) { Write-Host "`n== $m" }
function UserPathEntries { ([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') | Where-Object { $_ } }

$setup = Get-ChildItem $ArtifactDir -Filter 'VibeCodingHub-*-win-x64-setup.exe' | Select-Object -First 1
if (-not $setup) { Fail "no setup exe in $ArtifactDir" }
$cliZip = Get-ChildItem $ArtifactDir -Filter 'vibe-coding-hub-cli-*-win-x64.zip' | Select-Object -First 1
if (-not $cliZip) { Fail "no CLI zip in $ArtifactDir" }

$scratch = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$target = Join-Path $scratch 'vch-install'

Step 'Setup installs silently, per user, without administrator rights'
$p = Start-Process $setup.FullName -Wait -PassThru -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$target`""
if ($p.ExitCode -ne 0) { Fail "setup exited with code $($p.ExitCode)" }
if (-not (Test-Path (Join-Path $target 'Vibe Coding Hub.exe'))) { Fail 'the app is not installed' }
if (-not (Test-Path (Join-Path $target 'cli\aiagent.exe'))) { Fail 'the CLI is not installed' }
if ((UserPathEntries) -notcontains (Join-Path $target 'cli')) { Fail 'the CLI folder is not on the user PATH' }
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Vibe Coding Hub\Vibe Coding Hub.lnk'
if (-not (Test-Path $lnk)) { Fail 'the Start menu entry is missing' }
$out = & (Join-Path $target 'cli\aiagent.exe') --version
if (($out | Out-String) -notmatch [regex]::Escape($Version)) { Fail "aiagent reports '$out', expected version $Version" }

Step 'Uninstall removes files, shortcut and PATH entry'
$unins = Get-ChildItem $target -Filter 'unins*.exe' | Select-Object -First 1
if (-not $unins) { Fail 'the uninstaller is missing' }
Start-Process $unins.FullName -Wait -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
# The Inno Setup uninstaller hands over to a temporary copy of itself and returns at once: wait for the result.
$deadline = (Get-Date).AddSeconds(90)
while ((Test-Path (Join-Path $target 'Vibe Coding Hub.exe')) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
if (Test-Path (Join-Path $target 'Vibe Coding Hub.exe')) { Fail 'the app is still installed after uninstalling' }
if (Test-Path (Join-Path $target 'cli\aiagent.exe')) { Fail 'the CLI is still installed after uninstalling' }
if (Test-Path $lnk) { Fail 'the Start menu entry is still there after uninstalling' }
if ((UserPathEntries) -contains (Join-Path $target 'cli')) { Fail 'the PATH entry is still there after uninstalling' }

Step 'install.ps1 installs the CLI zip and verifies its checksum (Windows PowerShell 5.1, as users run it)'
$serve = Join-Path $scratch 'vch-serve'
New-Item -ItemType Directory -Force -Path $serve | Out-Null
Copy-Item $cliZip.FullName $serve
$hash = (Get-FileHash -Algorithm SHA256 -Path $cliZip.FullName).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $serve 'SHA256SUMS.txt'), "$hash  $($cliZip.Name)`n")
$server = Start-Process python -ArgumentList '-m', 'http.server', '8767', '--bind', '127.0.0.1', '--directory', "`"$serve`"" -PassThru -WindowStyle Hidden
try {
  Start-Sleep -Seconds 2
  $bin = Join-Path $scratch 'vch-cli'
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot 'install.ps1') -Version $Version -BaseUrl 'http://127.0.0.1:8767' -InstallDir $bin
  if ($LASTEXITCODE -ne 0) { Fail "install.ps1 exited with code $LASTEXITCODE" }
  $out = & (Join-Path $bin 'aiagent.exe') --version
  if (($out | Out-String) -notmatch [regex]::Escape($Version)) { Fail "aiagent from install.ps1 reports '$out', expected version $Version" }
  if ((UserPathEntries) -notcontains $bin) { Fail 'install.ps1 did not add its folder to the user PATH' }
} finally {
  if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}

Write-Host "`nWindows smoke test passed."
