<#
.SYNOPSIS
  Unregister wpa-mcp from MCP clients and remove the one-line installer binary.

.PARAMETER Client
  Which MCP client to remove from: 'claude-code', 'codex', 'claude-desktop', or 'auto'
  (default — removes from every client where the entry is found).

.PARAMETER ServerName
  Name to remove. Default 'wpa-mcp'.

.PARAMETER Scope
  Claude Code scope for `claude mcp remove`: user, local, or project. Defaults to
  SCOPE env var, then user.

.PARAMETER InstallDir
  Directory containing wpa-mcp.exe from the one-line installer. Defaults to
  %USERPROFILE%\.local\bin, or INSTALL_DIR if set.

.PARAMETER KeepBinary
  Leave wpa-mcp.exe on disk and only remove MCP client registrations.

.PARAMETER CleanBuild
  Also delete bin/ and obj/ output directories.

.EXAMPLE
  .\scripts\uninstall.ps1
  Default: remove from every installed client.
#>

[CmdletBinding()]
param(
    [ValidateSet('claude-code', 'codex', 'claude-desktop', 'auto')]
    [string]$Client = 'auto',

    [string]$ServerName = 'wpa-mcp',

    [string]$Scope,

    [string]$InstallDir,

    [switch]$KeepBinary,

    [switch]$CleanBuild
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host "[uninstall] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[uninstall] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "[uninstall] $msg" -ForegroundColor Yellow }

function Normalize-Scope {
    param([string]$Value)

    if (-not $Value) { $Value = $env:SCOPE }
    if (-not $Value) { $Value = 'user' }
    if (@('user', 'local', 'project') -notcontains $Value) {
        throw "Invalid Scope '$Value'. Use user, local, or project."
    }
    return $Value
}

function Resolve-InstallDir {
    if ($InstallDir) { return $InstallDir }
    if ($env:INSTALL_DIR) { return $env:INSTALL_DIR }
    return (Join-Path $env:USERPROFILE '.local\bin')
}

$repoRoot = $null
$repoCsproj = Join-Path $PSScriptRoot '..\src\WprMcp\WprMcp.csproj'
if (Test-Path $repoCsproj) { $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..') }

$Scope = Normalize-Scope $Scope
$InstallDir = Resolve-InstallDir
$binaryPath = Join-Path $InstallDir 'wpa-mcp.exe'

$codexConfigPath = Join-Path $env:USERPROFILE '.codex\config.toml'
$claudeDesktopConfigPath = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'

# Step 1 — pick clients.
$tryClaudeCode = $false
$tryCodex = $false
$tryClaudeDesktop = $false

switch ($Client) {
    'claude-code'    { $tryClaudeCode = $true }
    'codex'          { $tryCodex = $true }
    'claude-desktop' { $tryClaudeDesktop = $true }
    'auto'           { $tryClaudeCode = $true; $tryCodex = $true; $tryClaudeDesktop = $true }
}

# Step 2 — Claude Code.
if ($tryClaudeCode) {
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        Write-Info "Removing '$ServerName' from Claude Code ($Scope scope)..."
        & claude mcp remove $ServerName --scope $Scope
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "Removed from Claude Code."
        } else {
            Write-Warn "claude mcp remove returned non-zero (entry may not exist). Continuing."
        }
    } elseif ($Client -eq 'claude-code') {
        Write-Warn "'claude' CLI not found; skipping Claude Code uninstall."
    }
}

# Step 3 — Codex (TOML edit).
if ($tryCodex) {
    if (Test-Path $codexConfigPath) {
        Write-Info "Editing $codexConfigPath..."
        $rawToml = Get-Content $codexConfigPath -Raw
        if ($rawToml -and $rawToml.Trim()) {
            # CLM-safe: -replace + manual regex-escape (no [regex]::* calls).
            $escapedName = $ServerName -replace '([\\.+*?()\[\]{}|^$])', '\$1'
            $sectionPattern = "(?ms)^\[mcp_servers\.$escapedName(?:\.[^\]]+)?\][\s\S]*?(?=^\[|\z)"
            $newToml = $rawToml -replace $sectionPattern, ''
            if ($newToml -ne $rawToml) {
                Set-Content -Path $codexConfigPath -Value $newToml.TrimEnd() -Encoding UTF8
                Write-Ok "Removed '$ServerName' from $codexConfigPath."
            } else {
                Write-Warn "'$ServerName' not present in Codex config. Nothing to do."
            }
        }
    } elseif ($Client -eq 'codex') {
        Write-Warn "Codex config not found at $codexConfigPath."
    }
}

# Step 4 — Claude Desktop (JSON edit).
if ($tryClaudeDesktop) {
    if (Test-Path $claudeDesktopConfigPath) {
        Write-Info "Editing $claudeDesktopConfigPath..."
        $rawJson = Get-Content $claudeDesktopConfigPath -Raw
        if ($rawJson -and $rawJson.Trim()) {
            $config = $rawJson | ConvertFrom-Json
            if (($config.PSObject.Properties.Name -contains 'mcpServers') -and
                ($config.mcpServers.PSObject.Properties.Name -contains $ServerName)) {
                $newServers = New-Object PSObject -Property @{}
                foreach ($property in $config.mcpServers.PSObject.Properties) {
                    if ($property.Name -ne $ServerName) {
                        $newServers | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value
                    }
                }
                $config.mcpServers = $newServers
                $config | ConvertTo-Json -Depth 32 | Set-Content -Path $claudeDesktopConfigPath -Encoding UTF8
                Write-Ok "Removed '$ServerName' from $claudeDesktopConfigPath."
            } else {
                Write-Warn "'$ServerName' not present in Claude Desktop config. Nothing to do."
            }
        }
    } elseif ($Client -eq 'claude-desktop') {
        Write-Warn "Claude Desktop config not found at $claudeDesktopConfigPath."
    }
}

# Step 5 - remove one-line installer binary.
if (-not $KeepBinary) {
    if (Test-Path $binaryPath) {
        Write-Info "Removing $binaryPath..."
        try {
            Remove-Item -Path $binaryPath -Force
            Write-Ok 'Removed installed binary.'
        } catch {
            Write-Warn "Could not remove $binaryPath. Close MCP clients using it and delete it manually. Error: $_"
        }
    }
}

# Step 6 — optional clean (only meaningful in repo mode).
if ($CleanBuild) {
    if ($null -eq $repoRoot) {
        Write-Warn '-CleanBuild only works in repo mode (when scripts/ sits next to src/).'
    } else {
        Write-Info 'Cleaning build artifacts...'
        Get-ChildItem -Path $repoRoot -Include 'bin', 'obj' -Directory -Recurse |
            ForEach-Object {
                Write-Host "  rm -rf $($_.FullName)"
                Remove-Item -Path $_.FullName -Recurse -Force
            }
        Write-Ok 'Build artifacts removed.'
    }
}

Write-Host ''
Write-Ok 'Uninstall complete.'
Write-Host 'Restart your MCP client(s) to drop the cached server entry.'
