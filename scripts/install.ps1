<#
.SYNOPSIS
  One-line installer for wpa-mcp. Downloads the latest GitHub Release zip, extracts
  it, and runs the bundled setup.ps1.

.DESCRIPTION
  Designed to be invoked over the network — no clone needed:

    irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1 | iex

  The script:
    1. Queries GitHub Releases for the latest tag.
    2. Downloads wpa-mcp-<tag>.zip into %LOCALAPPDATA%\wpa-mcp\releases\<tag>\.
    3. Extracts the zip in place (idempotent: skips if already extracted).
    4. Runs the bundled setup.ps1 (or install.ps1 in older zips) with the user's
       chosen client / symbol-path / etc.  Forward extra args via -InstallArgs:
         iex "& { $(irm $URL) } -InstallArgs '-SymbolPath','SRV*C:\Sym*https://...'"

.PARAMETER Owner
  GitHub repo owner. Default 'tooluse-labs'. Override if forking.

.PARAMETER Repo
  GitHub repo name. Default 'wpa-mcp'.

.PARAMETER Tag
  Specific release tag (e.g. 'v0.2.0'). Default: latest.

.PARAMETER InstallArgs
  Extra arguments forwarded verbatim to the bundled setup.ps1.

.EXAMPLE
  irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1 | iex
#>

[CmdletBinding()]
param(
    [string]$Owner = 'tooluse-labs',
    [string]$Repo  = 'wpa-mcp',
    [string]$Tag,                 # null = latest
    [string[]]$InstallArgs = @()
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host "[install] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[install] $msg" -ForegroundColor Green }

# Step 1 — resolve target tag (default to latest release).
if (-not $Tag) {
    Write-Info "Querying latest release of $Owner/$Repo..."
    $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/latest" -UseBasicParsing
    $Tag = $latest.tag_name
}
Write-Ok "Tag: $Tag"

$zipName = "wpa-mcp-$Tag.zip"
$zipUrl = "https://github.com/$Owner/$Repo/releases/download/$Tag/$zipName"
$installRoot = Join-Path $env:LOCALAPPDATA "wpa-mcp\releases\$Tag"

# Step 2 — download (skip if already cached).
$zipPath = Join-Path $installRoot $zipName
if (-not (Test-Path $zipPath)) {
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    Write-Info "Downloading $zipUrl..."
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
} else {
    Write-Info "Cached zip found at $zipPath."
}

# Step 3 — extract (idempotent: re-extract if neither setup.ps1 nor install.ps1 found).
$extractDir = Join-Path $installRoot 'extracted'
$setupCandidate   = Join-Path $extractDir 'setup.ps1'
$legacyCandidate  = Join-Path $extractDir 'install.ps1'
if (-not (Test-Path $setupCandidate) -and -not (Test-Path $legacyCandidate)) {
    if (Test-Path $extractDir) { Remove-Item -Path $extractDir -Recurse -Force }
    Write-Info "Extracting to $extractDir..."
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
}

# Step 4 — invoke the bundled setup.ps1.  Backward-compat: older release zips (≤ v0.1.0)
# bundled the script under the older name install.ps1.
$installScript = if (Test-Path $setupCandidate) { $setupCandidate } else { $legacyCandidate }
if (-not (Test-Path $installScript)) {
    throw "Neither setup.ps1 nor install.ps1 found in release zip. Asset may be malformed."
}
Write-Info "Running $installScript $InstallArgs..."
& $installScript @InstallArgs

Write-Ok "Install complete. Server deployed from $extractDir\bin."
