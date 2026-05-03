<#
.SYNOPSIS
  Build wpa-mcp and register it with one or more MCP clients. Idempotent — running
  twice reinstalls cleanly.

.PARAMETER Client
  Which MCP client to install for:
    'claude-code'    — uses the `claude` CLI (claude mcp add)
    'codex'          — edits ~/.codex/config.toml
    'claude-desktop' — edits %APPDATA%\Claude\claude_desktop_config.json
    'auto' (default) — installs for every client detected on this machine

.PARAMETER SymbolPath
  Value for _NT_SYMBOL_PATH passed to the server. Defaults to the Microsoft public
  symbol server. Override to add Chromium / private vendor servers.

.PARAMETER ServerName
  Name registered with the MCP client(s). Default 'wpa-mcp'.

.PARAMETER CacheSize
  Max number of traces kept in memory at once. Default 2.

.PARAMETER SkipBuild
  Skip 'dotnet build -c Release' (use existing binary). Auto-set in release-zip mode.

.EXAMPLE
  .\scripts\install.ps1
  Default: build (if needed) + register with every detected MCP client (Claude Code,
  Codex, Claude Desktop).

.EXAMPLE
  .\scripts\install.ps1 -Client codex -SymbolPath "SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols;SRV*C:\Symbols*https://chromium-browser-symsrv.commondatastorage.googleapis.com"
  Only install for OpenAI Codex, with Chromium symbols added.
#>

[CmdletBinding()]
param(
    [ValidateSet('claude-code', 'codex', 'claude-desktop', 'auto')]
    [string]$Client = 'auto',

    [string]$SymbolPath = 'SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols',

    [string]$ServerName = 'wpa-mcp',

    [int]$CacheSize = 2,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host "[install] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[install] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "[install] $msg" -ForegroundColor Yellow }

# Step 1 — locate the DLL. Two layouts supported:
#   * Repo mode:        scripts/install.ps1, DLL at ../src/WprMcp/bin/Release/net8.0/WprMcp.dll
#   * Release-zip mode: install.ps1 at zip root, DLL at ./bin/WprMcp.dll
$repoModeDll = Join-Path $PSScriptRoot '..\src\WprMcp\bin\Release\net8.0\WprMcp.dll'
$zipModeDll  = Join-Path $PSScriptRoot 'bin\WprMcp.dll'

if (Test-Path $zipModeDll) {
    $mode = 'release-zip'
    $dllPath = (Resolve-Path $zipModeDll).Path
    if (-not $SkipBuild) {
        Write-Info 'Detected release-zip layout — DLL is pre-built, skipping dotnet build.'
        $SkipBuild = $true
    }
} elseif (Test-Path (Join-Path $PSScriptRoot '..\src\WprMcp\WprMcp.csproj')) {
    $mode = 'repo'
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
    $dllPath = $repoModeDll
} else {
    throw "Cannot determine layout. install.ps1 must run from either the wpa-mcp repo's scripts/ folder OR the root of an extracted release zip."
}

# Step 2 — prereq check.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 8 not found on PATH. Install runtime with: winget install Microsoft.DotNet.Runtime.8 (or SDK if building from source)'
}

# Step 3 — build (repo mode only).
if ($SkipBuild) {
    Write-Info 'Skipping build.'
} else {
    Write-Info 'Building (Release)...'
    Push-Location $repoRoot
    try {
        dotnet build -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
    } finally {
        Pop-Location
    }
}
if (-not (Test-Path $dllPath)) {
    throw "DLL not found at $dllPath. Build it first or use a release zip."
}
Write-Ok "Mode: $mode"
Write-Ok "DLL:  $dllPath"

# Step 4 — pick clients.
$installClaudeCode = $false
$installCodex = $false
$installClaudeDesktop = $false

# Auto-detect helper. Codex CLI is at `codex` on PATH; absent that, the presence of
# ~/.codex/config.toml signals an installed Codex client.
$codexConfigPath = Join-Path $env:USERPROFILE '.codex\config.toml'
$claudeDesktopConfigPath = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'

switch ($Client) {
    'claude-code'    { $installClaudeCode = $true }
    'codex'          { $installCodex = $true }
    'claude-desktop' { $installClaudeDesktop = $true }
    'auto' {
        if (Get-Command claude -ErrorAction SilentlyContinue) { $installClaudeCode = $true }
        if ((Get-Command codex -ErrorAction SilentlyContinue) -or (Test-Path $codexConfigPath)) {
            $installCodex = $true
        }
        if (Test-Path $claudeDesktopConfigPath) { $installClaudeDesktop = $true }
        if (-not ($installClaudeCode -or $installCodex -or $installClaudeDesktop)) {
            throw "Auto-detect found no MCP client. Pass -Client claude-code|codex|claude-desktop explicitly, or install one first."
        }
    }
}

# Step 5 — install for Claude Code.
if ($installClaudeCode) {
    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
        throw "'claude' CLI not on PATH. Install Claude Code first: https://docs.claude.com/claude-code"
    }
    Write-Info 'Registering with Claude Code (user scope)...'
    # Idempotent: remove first, ignore failure if absent.
    & claude mcp remove $ServerName --scope user 2>$null | Out-Null
    & claude mcp add $ServerName --scope user `
        -e "_NT_SYMBOL_PATH=$SymbolPath" `
        -e "WPRMCP_CACHE_SIZE=$CacheSize" `
        -- dotnet $dllPath
    if ($LASTEXITCODE -ne 0) { throw 'claude mcp add failed.' }
    Write-Ok "Registered '$ServerName' with Claude Code."
}

# Step 6 — install for Codex (TOML edit). We don't shell out to `codex mcp add` because
# the subcommand existence varies by Codex CLI version; direct config edit is portable
# and idempotent.
if ($installCodex) {
    $configDir = Split-Path $codexConfigPath -Parent
    if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
    if (-not (Test-Path $codexConfigPath)) { '' | Set-Content -Path $codexConfigPath -Encoding UTF8 }

    Write-Info "Editing $codexConfigPath..."
    $rawToml = Get-Content $codexConfigPath -Raw
    if ($null -eq $rawToml) { $rawToml = '' }

    # Remove any existing [mcp_servers.<name>] and its sub-tables. Pattern matches the
    # named header (and any further '.<sub>' suffix on the same prefix) plus everything
    # up to the next top-level [section] or end of file. Does NOT touch unrelated
    # [mcp_servers.<other>] sections.
    $escapedName = [regex]::Escape($ServerName)
    $sectionPattern = "(?ms)^\[mcp_servers\.$escapedName(?:\.[^\]]+)?\][\s\S]*?(?=^\[|\z)"
    $rawToml = [regex]::Replace($rawToml, $sectionPattern, '')

    # Append the new entry. Use double-quoted TOML strings with backslash escapes so
    # Windows paths survive verbatim.
    function Format-TomlString([string]$s) {
        $escaped = ($s -replace '\\', '\\\\') -replace '"', '\"'
        return '"' + $escaped + '"'
    }

    $argsToml = (@($dllPath) | ForEach-Object { Format-TomlString $_ }) -join ', '
    $sb = [System.Text.StringBuilder]::new($rawToml.TrimEnd())
    if ($sb.Length -gt 0) { $sb.AppendLine() | Out-Null; $sb.AppendLine() | Out-Null }
    $sb.AppendLine("[mcp_servers.$ServerName]") | Out-Null
    $sb.AppendLine("command = " + (Format-TomlString 'dotnet')) | Out-Null
    $sb.AppendLine("args = [$argsToml]") | Out-Null
    $sb.AppendLine() | Out-Null
    $sb.AppendLine("[mcp_servers.$ServerName.env]") | Out-Null
    $sb.AppendLine('_NT_SYMBOL_PATH = ' + (Format-TomlString $SymbolPath)) | Out-Null
    $sb.AppendLine('WPRMCP_CACHE_SIZE = ' + (Format-TomlString "$CacheSize")) | Out-Null

    Set-Content -Path $codexConfigPath -Value $sb.ToString() -Encoding UTF8
    Write-Ok "Wrote '$ServerName' entry to $codexConfigPath."
}

# Step 7 — install for Claude Desktop (JSON edit).
if ($installClaudeDesktop) {
    $configDir = Split-Path $claudeDesktopConfigPath -Parent
    if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
    if (-not (Test-Path $claudeDesktopConfigPath)) { '{}' | Set-Content -Path $claudeDesktopConfigPath -Encoding UTF8 }

    Write-Info "Editing $claudeDesktopConfigPath..."
    $rawJson = Get-Content $claudeDesktopConfigPath -Raw
    if ([string]::IsNullOrWhiteSpace($rawJson)) { $rawJson = '{}' }
    $config = $rawJson | ConvertFrom-Json

    if (-not $config.PSObject.Properties.Name.Contains('mcpServers')) {
        $config | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue ([pscustomobject]@{})
    }

    $serverEntry = [pscustomobject]@{
        command = 'dotnet'
        args    = @($dllPath)
        env     = [pscustomobject]@{
            _NT_SYMBOL_PATH    = $SymbolPath
            WPRMCP_CACHE_SIZE  = "$CacheSize"
        }
    }
    if ($config.mcpServers.PSObject.Properties.Name.Contains($ServerName)) {
        $config.mcpServers.$ServerName = $serverEntry
    } else {
        $config.mcpServers | Add-Member -NotePropertyName $ServerName -NotePropertyValue $serverEntry
    }

    $config | ConvertTo-Json -Depth 32 | Set-Content -Path $claudeDesktopConfigPath -Encoding UTF8
    Write-Ok "Wrote '$ServerName' entry to $claudeDesktopConfigPath."
}

# Step 8 — done.
Write-Host ''
Write-Ok 'Install complete.'
Write-Host ''
Write-Host 'Next steps:'
if ($installClaudeCode)    { Write-Host "  - Claude Code: open a project; tools appear as mcp__$ServerName__*." }
if ($installCodex)         { Write-Host "  - Codex: restart any active codex session to load the new server." }
if ($installClaudeDesktop) { Write-Host '  - Claude Desktop: fully quit and re-launch to pick up the new config.' }
Write-Host ''
Write-Host "First call to load_trace on a fresh .etl takes 30 s - 3 min while the .etlx index is built."
Write-Host "If symbols look wrong (ResolutionRate < 0.8), see CONTRIBUTING.md / docs/SYMBOL_RECIPES.md."
