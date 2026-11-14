<#
.SYNOPSIS
  Installs the Vibe Coding Hub CLI (aiagent) on Windows.
.DESCRIPTION
  irm https://raw.githubusercontent.com/AIHelpers/vibe-coding-hub/main/install.ps1 | iex

  Parameters (or environment variables of the same name in upper case when piped to iex):
    -Version     release to install, e.g. 1.2.0 (default: latest)
    -InstallDir  target directory (default: %LOCALAPPDATA%\Programs\aiagent)
    -Repo        GitHub repository (default: AIHelpers/vibe-coding-hub)
    -BaseUrl     download root, overrides GitHub (used by tests and mirrors)

  The archive is verified against SHA256SUMS.txt of the same release before anything is installed.
  The directory is added to the user PATH (no administrator rights needed).
#>
[CmdletBinding()]
param(
  [string]$Version    = $(if ($env:VERSION) { $env:VERSION } else { 'latest' }),
  [string]$InstallDir = $(if ($env:INSTALL_DIR) { $env:INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\aiagent' }),
  [string]$Repo       = $(if ($env:REPO) { $env:REPO } else { 'AIHelpers/vibe-coding-hub' }),
  [string]$BaseUrl    = $env:BASE_URL
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without the progress bar
# Windows PowerShell 5.1 may default to old TLS versions; GitHub needs TLS 1.2 or newer.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

# x64 binaries also run on Windows on ARM through emulation.
if (-not [Environment]::Is64BitOperatingSystem) {
  throw 'A 64-bit version of Windows is required. Alternative: dotnet tool install -g VibeCodingHub.Cli'
}

if (-not $BaseUrl) {
  if ($Version -eq 'latest') {
    # The redirect target of /releases/latest carries the tag; this avoids the rate-limited API.
    $req = [Net.HttpWebRequest]::Create("https://github.com/$Repo/releases/latest")
    $req.AllowAutoRedirect = $false
    try { $resp = $req.GetResponse() } catch [Net.WebException] { $resp = $_.Exception.Response }
    $location = $resp.Headers['Location']
    $resp.Close()
    if (-not $location -or $location -notmatch '/tag/(v[^/]+)$') { throw "No release found for $Repo" }
    $tag = $Matches[1]
  } else {
    $tag = 'v' + $Version.TrimStart('v')
  }
  $BaseUrl = "https://github.com/$Repo/releases/download/$tag"
} else {
  if ($Version -eq 'latest') { throw 'Version must be set when BaseUrl is used' }
  $tag = 'v' + $Version.TrimStart('v')
}
$ver = $tag.TrimStart('v')
$archive = "vibe-coding-hub-cli-$ver-win-x64.zip"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
  Write-Host "Downloading $archive"
  Invoke-WebRequest -Uri "$BaseUrl/$archive" -OutFile (Join-Path $tmp $archive) -UseBasicParsing
  Invoke-WebRequest -Uri "$BaseUrl/SHA256SUMS.txt" -OutFile (Join-Path $tmp 'SHA256SUMS.txt') -UseBasicParsing

  $line = Get-Content (Join-Path $tmp 'SHA256SUMS.txt') |
    Where-Object { $_ -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $archive } |
    Select-Object -First 1
  if (-not $line) { throw "$archive is not listed in SHA256SUMS.txt" }
  $null = $line -match '^\s*([0-9a-fA-F]{64})'
  $expected = $Matches[1].ToLowerInvariant()
  $actual = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $tmp $archive)).Hash.ToLowerInvariant()
  if ($expected -ne $actual) { throw "Checksum mismatch for $archive (expected $expected, got $actual)" }
  Write-Host 'Checksum OK'

  $extract = Join-Path $tmp 'x'
  Expand-Archive -Path (Join-Path $tmp $archive) -DestinationPath $extract
  if (-not (Test-Path (Join-Path $extract 'aiagent.exe'))) { throw "The archive does not contain 'aiagent.exe'" }

  New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
  Copy-Item (Join-Path $extract 'aiagent.exe') (Join-Path $InstallDir 'aiagent.exe') -Force
} finally {
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$entries = if ($userPath) { $userPath -split ';' | Where-Object { $_ } } else { @() }
if ($entries -notcontains $InstallDir) {
  [Environment]::SetEnvironmentVariable('Path', (($entries + $InstallDir) -join ';'), 'User')
  Write-Host "Added $InstallDir to your PATH. Open a new terminal to use 'aiagent'."
}
Write-Host "Installed aiagent $ver to $InstallDir\aiagent.exe"
