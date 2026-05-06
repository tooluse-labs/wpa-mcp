<#
.SYNOPSIS
  One-line installer for wpa-mcp. Downloads the latest self-contained Windows
  executable and registers it with detected MCP clients.

.DESCRIPTION
  Designed to be invoked over the network, no clone and no .NET install needed:

    iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) }"

  The script:
    1. Resolves the latest GitHub Release tag, unless -Tag or VERSION is set.
    2. Downloads wpa-mcp-win-x64.exe to %USERPROFILE%\.local\bin\wpa-mcp.exe.
    3. Registers the executable directly with Claude Code, Codex, and Claude
       Desktop when those clients are detected.

.PARAMETER Client
  Which MCP client to install for: claude-code, codex, claude-desktop, or auto.

.PARAMETER Scope
  Claude Code scope for `claude mcp add`: user, local, or project. Defaults to
  SCOPE env var, then user. Codex and Claude Desktop ignore this.

.PARAMETER SymbolPath
  Value passed to wpa-mcp via --symbol-path. Defaults to Microsoft public symbols.

.PARAMETER CacheSize
  Value passed to wpa-mcp via --cache-size. Defaults to 2.
#>

[CmdletBinding()]
param(
    [string]$Owner = 'tooluse-labs',
    [string]$Repo = 'wpa-mcp',
    [string]$Tag,
    [string]$InstallDir,

    [ValidateSet('claude-code', 'codex', 'claude-desktop', 'auto')]
    [string]$Client = 'auto',

    [string]$Scope,
    [string]$ServerName = 'wpa-mcp',
    [string]$SymbolPath = 'SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols',
    [int]$CacheSize = 2
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host "[install] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[install] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "[install] $msg" -ForegroundColor Yellow }

function Normalize-Scope {
    param([string]$Value)

    if (-not $Value) { $Value = $env:SCOPE }
    if (-not $Value) { $Value = 'user' }
    if (@('user', 'local', 'project') -notcontains $Value) {
        throw "Invalid Scope '$Value'. Use user, local, or project."
    }
    return $Value
}

function Format-TomlString {
    param([string]$Value)

    $escaped = ($Value -replace '\\', '\\\\') -replace '"', '\"'
    return '"' + $escaped + '"'
}

function New-ServerArgs {
    return @('--symbol-path', $SymbolPath, '--cache-size', "$CacheSize")
}

function Move-WithRetry {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    for ($i = 1; $i -le 5; $i++) {
        try {
            Move-Item -Path $Source -Destination $Destination -Force
            return
        } catch {
            if ($i -eq 5) {
                throw "Could not replace $Destination. Close MCP clients using wpa-mcp.exe and re-run the installer. Last error: $_"
            }
            Write-Warn "Could not replace $Destination yet; retrying in 2 seconds..."
            Start-Sleep -Seconds 2
        }
    }
}

function Install-Binary {
    if (-not $InstallDir) {
        if ($env:INSTALL_DIR) {
            $script:InstallDir = $env:INSTALL_DIR
        } else {
            $script:InstallDir = Join-Path $env:USERPROFILE '.local\bin'
        }
    }

    if (-not $Tag -and $env:VERSION) { $script:Tag = $env:VERSION }
    if (-not $Tag) {
        Write-Info "Querying latest release of $Owner/$Repo..."
        $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/latest" -UseBasicParsing
        $script:Tag = $latest.tag_name
    }
    Write-Ok "Tag: $Tag"

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    $assetName = 'wpa-mcp-win-x64.exe'
    $assetUrl = "https://github.com/$Owner/$Repo/releases/download/$Tag/$assetName"
    $binaryPath = Join-Path $InstallDir 'wpa-mcp.exe'
    $tempPath = Join-Path $InstallDir "wpa-mcp-$Tag.download.exe"

    Write-Info "Downloading $assetUrl..."
    Invoke-WebRequest -Uri $assetUrl -OutFile $tempPath -UseBasicParsing
    if (Get-Command Unblock-File -ErrorAction SilentlyContinue) {
        try { Unblock-File -Path $tempPath } catch { }
    }

    Move-WithRetry -Source $tempPath -Destination $binaryPath
    Write-Ok "Installed $binaryPath"

    return $binaryPath
}

function Register-ClaudeCode {
    param(
        [Parameter(Mandatory)][string]$BinaryPath,
        [Parameter(Mandatory)][string]$ClaudeScope
    )

    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
        if ($Client -eq 'claude-code') {
            throw "'claude' CLI not on PATH. Install Claude Code first."
        }
        return $false
    }

    Write-Info "Registering with Claude Code ($ClaudeScope scope)..."
    & claude mcp remove $ServerName --scope $ClaudeScope
    if ($LASTEXITCODE -ne 0) {
        Write-Info "(no prior $ServerName entry to remove; expected on first install)"
    }

    $serverArgs = New-ServerArgs
    & claude mcp add --scope $ClaudeScope $ServerName -- $BinaryPath @serverArgs
    if ($LASTEXITCODE -ne 0) { throw 'claude mcp add failed.' }

    Write-Ok "Registered '$ServerName' with Claude Code."
    return $true
}

function Register-Codex {
    param([Parameter(Mandatory)][string]$BinaryPath)

    $codexConfigPath = Join-Path $env:USERPROFILE '.codex\config.toml'
    $codexInstalled = (Get-Command codex -ErrorAction SilentlyContinue) -or (Test-Path $codexConfigPath)
    if (-not $codexInstalled) {
        if ($Client -eq 'codex') { throw "Codex config not found and codex CLI not on PATH." }
        return $false
    }

    $configDir = Split-Path $codexConfigPath -Parent
    if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
    if (-not (Test-Path $codexConfigPath)) { '' | Set-Content -Path $codexConfigPath -Encoding UTF8 }

    Write-Info "Editing $codexConfigPath..."
    $rawToml = Get-Content $codexConfigPath -Raw
    if ($null -eq $rawToml) { $rawToml = '' }

    $escapedName = $ServerName -replace '([\\.+*?()\[\]{}|^$])', '\$1'
    $sectionPattern = "(?ms)^\[mcp_servers\.$escapedName(?:\.[^\]]+)?\][\s\S]*?(?=^\[|\z)"
    $rawToml = $rawToml -replace $sectionPattern, ''

    $argsToml = (New-ServerArgs | ForEach-Object { Format-TomlString $_ }) -join ', '
    $commandToml = Format-TomlString $BinaryPath
    $newSection = @"
[mcp_servers.$ServerName]
command = $commandToml
args = [$argsToml]
"@

    $preamble = $rawToml.TrimEnd()
    if ($preamble.Length -gt 0) { $preamble = $preamble + "`n`n" }
    Set-Content -Path $codexConfigPath -Value ($preamble + $newSection) -Encoding UTF8

    Write-Ok "Wrote '$ServerName' entry to $codexConfigPath."
    return $true
}

function Register-ClaudeDesktop {
    param([Parameter(Mandatory)][string]$BinaryPath)

    $claudeDesktopConfigPath = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
    if (-not (Test-Path $claudeDesktopConfigPath)) {
        if ($Client -eq 'claude-desktop') { throw "Claude Desktop config not found at $claudeDesktopConfigPath." }
        return $false
    }

    Write-Info "Editing $claudeDesktopConfigPath..."
    $rawJson = Get-Content $claudeDesktopConfigPath -Raw
    if (-not $rawJson -or -not $rawJson.Trim()) { $rawJson = '{}' }
    $config = $rawJson | ConvertFrom-Json

    if (-not ($config.PSObject.Properties.Name -contains 'mcpServers')) {
        $config | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue (New-Object PSObject -Property @{})
    }

    $serverEntry = New-Object PSObject -Property @{
        command = $BinaryPath
        args = @(New-ServerArgs)
    }

    if ($config.mcpServers.PSObject.Properties.Name -contains $ServerName) {
        $config.mcpServers.$ServerName = $serverEntry
    } else {
        $config.mcpServers | Add-Member -NotePropertyName $ServerName -NotePropertyValue $serverEntry
    }

    $config | ConvertTo-Json -Depth 32 | Set-Content -Path $claudeDesktopConfigPath -Encoding UTF8
    Write-Ok "Wrote '$ServerName' entry to $claudeDesktopConfigPath."
    return $true
}

$Scope = Normalize-Scope $Scope
$binaryPath = Install-Binary

$registered = $false
if ($Client -eq 'auto' -or $Client -eq 'claude-code') {
    $registered = (Register-ClaudeCode -BinaryPath $binaryPath -ClaudeScope $Scope) -or $registered
}
if ($Client -eq 'auto' -or $Client -eq 'codex') {
    $registered = (Register-Codex -BinaryPath $binaryPath) -or $registered
}
if ($Client -eq 'auto' -or $Client -eq 'claude-desktop') {
    $registered = (Register-ClaudeDesktop -BinaryPath $binaryPath) -or $registered
}

if (-not $registered) {
    Write-Warn "No MCP client detected. Installed binary only: $binaryPath"
}

Write-Host ''
Write-Ok 'Install complete.'
Write-Host ''
Write-Host 'Next steps:'
Write-Host '  - Restart any active MCP client sessions.'
Write-Host "  - Binary: $binaryPath"
