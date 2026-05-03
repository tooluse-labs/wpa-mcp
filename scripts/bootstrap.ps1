<#
.SYNOPSIS
  One-line installer for wpa-mcp. Downloads the latest GitHub Release zip, extracts it,
  and runs install.ps1.

.DESCRIPTION
  Designed to be invoked over the network — no clone needed:

    irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/bootstrap.ps1 | iex

  The script:
    1. Queries GitHub Releases for the latest tag.
    2. Downloads wpa-mcp-<tag>.zip into %LOCALAPPDATA%\wpa-mcp\releases\<tag>\.
    3. Extracts the zip in place (idempotent: skips if already extracted).
    4. Runs the bundled install.ps1 with the user's chosen client / symbol-path / etc.
       Forward extra args after `--` to install.ps1, e.g.:
         iex "& { $(irm $URL) } -SymbolPath 'SRV*C:\Sym*https://msdl.microsoft.com/...'"

.PARAMETER Owner
  GitHub repo owner. Default 'tooluse-labs'. Override if forking.

.PARAMETER Repo
  GitHub repo name. Default 'wpa-mcp'.

.PARAMETER Tag
  Specific release tag (e.g. 'v0.2.0'). Default: latest.

.PARAMETER InstallArgs
  Extra arguments forwarded to install.ps1 verbatim.

.EXAMPLE
  irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/bootstrap.ps1 | iex
#>

[CmdletBinding()]
param(
    [string]$Owner = 'tooluse-labs',
    [string]$Repo  = 'wpa-mcp',
    [string]$Tag,                 # null = latest
    [string[]]$InstallArgs = @()
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host "[bootstrap] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[bootstrap] $msg" -ForegroundColor Green }

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

# Step 3 — extract (idempotent).
$extractDir = Join-Path $installRoot 'extracted'
if (-not (Test-Path "$extractDir\install.ps1")) {
    if (Test-Path $extractDir) { Remove-Item -Path $extractDir -Recurse -Force }
    Write-Info "Extracting to $extractDir..."
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
}

# Step 4 — invoke the bundled install.ps1.
$installScript = Join-Path $extractDir 'install.ps1'
if (-not (Test-Path $installScript)) {
    throw "install.ps1 not found in release zip. Asset may be malformed."
}
Write-Info "Running $installScript $InstallArgs..."
& $installScript @InstallArgs

Write-Ok "Bootstrap complete. Server installed from $extractDir\bin."
