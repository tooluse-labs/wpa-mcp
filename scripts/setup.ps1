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

.PARAMETER SkipDotNetInstall
  Don't auto-install .NET 8 if missing. Default: bootstrap .NET 8 via Microsoft's
  official user-scope installer (https://dot.net/v1/dotnet-install.ps1) — no admin
  needed, installs into %USERPROFILE%\.dotnet.

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

    [switch]$SkipBuild,

    [switch]$SkipDotNetInstall
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host "[install] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[install] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "[install] $msg" -ForegroundColor Yellow }

# Detect (and optionally install) the right .NET 8 variant: 'sdk' for repo mode (need
# the build toolchain) or 'runtime' for release-zip mode (just need to run the DLL).
# Bootstrap path: download dotnet-install.ps1 from Microsoft and run user-scope; goes
# to %USERPROFILE%\.dotnet, no admin required, no winget dependency.
function Ensure-DotNet {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('runtime', 'sdk')]
        [string]$Variant
    )

    $existing = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($existing) {
        $cmd = if ($Variant -eq 'sdk') { '--list-sdks' } else { '--list-runtimes' }
        $output = & $existing.Source $cmd 2>$null
        $hasIt = $false
        if ($Variant -eq 'sdk') {
            $hasIt = $output -match '^8\.'
        } else {
            $hasIt = $output -match 'Microsoft\.NETCore\.App 8\.'
        }
        if ($hasIt) {
            Write-Ok ".NET 8 $Variant already present."
            return
        }
        Write-Warn "dotnet on PATH but .NET 8 $Variant missing; will bootstrap alongside."
    }

    if ($SkipDotNetInstall) {
        throw ".NET 8 $Variant not detected and -SkipDotNetInstall was passed. Install manually: winget install Microsoft.DotNet.SDK.8"
    }

    # Constrained Language Mode (AppLocker / WDAC / Device Guard policy) blocks .NET
    # method invocations entirely.  dotnet-install.ps1 uses [System.Net.WebClient]::new(),
    # [System.IO.Path]::Combine(...) and similar on every code path, so the bootstrap
    # can't even start under CLM.  Detect early and surface actionable guidance instead
    # of letting the failure manifest deep inside dotnet-install.ps1 as a generic
    # "Cannot invoke method ... in this language mode" error.
    $languageMode = $ExecutionContext.SessionState.LanguageMode
    if ($languageMode -ne 'FullLanguage') {
        $pkg = if ($Variant -eq 'sdk') { 'Microsoft.DotNet.SDK.8' } else { 'Microsoft.DotNet.Runtime.8' }
        throw "Cannot bootstrap .NET 8 ${Variant}: this PowerShell session is in $languageMode mode (typically AppLocker / WDAC / Device Guard policy). Microsoft's dotnet-install.ps1 calls .NET methods directly and Constrained Language Mode blocks them. Install manually then re-run setup.ps1 with -SkipDotNetInstall:`n  winget install $pkg"
    }

    Write-Info "Bootstrapping .NET 8 $Variant via dotnet-install.ps1 (user-scope, no admin)..."
    $bootstrapPath = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $bootstrapPath -UseBasicParsing

    # Hashtable splat (NOT array splat).  In PowerShell, splatting a string array
    # passes its elements POSITIONALLY -- the leading dash on a token like the channel
    # flag is just a literal character, not a parameter-name marker.  That used to
    # make dotnet-install.ps1 receive the flag string as its first positional ($Channel)
    # and 8.0 as its second positional ($Quality), which threw "'8.0' is not a
    # supported value for -Quality option".  Hashtable splat binds by name.
    $bootstrapArgs = @{ Channel = '8.0' }
    if ($Variant -eq 'runtime') { $bootstrapArgs['Runtime'] = 'dotnet' }
    # dotnet-install.ps1 is a .ps1, so $LASTEXITCODE is NEVER set by this call -- it
    # stays at whatever it was before (often $null in a fresh PS session).  Since
    # `$null -ne 0` is $true, an `if ($LASTEXITCODE -ne 0)` check would fire spuriously
    # on every successful install.  .ps1 invocations signal failure via terminating
    # errors, so use try/catch instead.
    try {
        & $bootstrapPath @bootstrapArgs
    } catch {
        throw "dotnet-install.ps1 failed: $_`nInstall manually: winget install Microsoft.DotNet.SDK.8"
    }

    # dotnet-install.ps1 installs to %USERPROFILE%\.dotnet by default. Add to PATH for
    # the rest of this session so subsequent `dotnet` calls resolve.
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet'
    if ((Test-Path "$userDotnet\dotnet.exe") -and ($env:PATH -notlike "*$userDotnet*")) {
        $env:PATH = "$userDotnet;$env:PATH"
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet still not on PATH after install. Add '$userDotnet' to PATH manually and retry."
    }
    Write-Ok ".NET 8 $Variant installed to $userDotnet"
    Write-Warn "If you open a new shell, add '$userDotnet' to PATH (or relog — dotnet-install.ps1 updates user PATH for future sessions)."
}

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

# Step 2 — ensure .NET 8 is available. Repo mode needs the SDK (for `dotnet build`);
# release-zip mode only needs the runtime (just `dotnet WprMcp.dll`).
$dotnetVariant = if ($mode -eq 'release-zip') { 'runtime' } else { 'sdk' }
Ensure-DotNet -Variant $dotnetVariant

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

# Resolve dotnet to its absolute path BEFORE writing any client config.  We
# must not write the literal string "dotnet" — MCP clients spawn the server
# via raw process creation (no shell), and clients launched from environments
# where dotnet isn't on PATH (Git Bash, some VS Code launches, .NET installed
# only on system PATH but client started from Git Bash, etc.) hit "MCP startup
# failed: program not found".  Get-Command resolves to the same absolute path
# the user's PowerShell currently sees, which is the right one to bake in.
$dotnetCommand = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnetCommand) {
    throw "dotnet was just ensured on PATH but Get-Command can't resolve it. Re-run after restarting the shell."
}
Write-Ok "dotnet: $dotnetCommand"

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
    # Idempotent: remove first, accept non-zero exit if the entry doesn't exist.
    # NOTE: do NOT redirect stderr with `2>$null` here — under PS 5.1 + ErrorActionPreference=Stop,
    # captured native-command stderr wraps as a NativeCommandError that DOES terminate.  Letting
    # claude.exe's "No user-scoped MCP server found" message print to the terminal is fine; we
    # check $LASTEXITCODE explicitly.
    & claude mcp remove $ServerName --scope user
    if ($LASTEXITCODE -ne 0) {
        Write-Info "(no prior $ServerName entry to remove; that's expected on first install)"
    }
    # Canonical syntax (https://code.claude.com/docs/en/mcp):
    #   claude mcp add [options] <name> -- <command> [args...]
    # All flags (--scope, -e, --transport, --header) MUST come before the server name.
    # If the name appears first, the variadic env-flag parser keeps consuming tokens
    # past the trailing `-e` and swallows the `--` separator + the actual command,
    # producing "error: missing required argument 'commandOrUrl'".
    & claude mcp add --scope user `
        -e "_NT_SYMBOL_PATH=$SymbolPath" `
        -e "WPRMCP_CACHE_SIZE=$CacheSize" `
        $ServerName `
        -- $dotnetCommand $dllPath
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
    #
    # CLM-safe: -replace operator is built-in (no [regex] type call), and we hand-escape
    # ServerName via -replace itself. PS Constrained Language Mode (AppLocker / WDAC)
    # blocks [regex]::*, [System.Text.StringBuilder]::*, etc.; we avoid all of them.
    $escapedName = $ServerName -replace '([\\.+*?()\[\]{}|^$])', '\$1'
    $sectionPattern = "(?ms)^\[mcp_servers\.$escapedName(?:\.[^\]]+)?\][\s\S]*?(?=^\[|\z)"
    $rawToml = $rawToml -replace $sectionPattern, ''

    # TOML double-quoted string escaping — backslash + double-quote.
    function Format-TomlString([string]$s) {
        $escaped = ($s -replace '\\', '\\\\') -replace '"', '\"'
        return '"' + $escaped + '"'
    }

    $argsToml = (@($dllPath) | ForEach-Object { Format-TomlString $_ }) -join ', '
    $symbolToml = Format-TomlString $SymbolPath
    $cacheSizeToml = Format-TomlString "$CacheSize"
    $dotnetToml = Format-TomlString $dotnetCommand

    # Build via here-string + concatenation. No StringBuilder.
    $newSection = @"
[mcp_servers.$ServerName]
command = $dotnetToml
args = [$argsToml]

[mcp_servers.$ServerName.env]
_NT_SYMBOL_PATH = $symbolToml
WPRMCP_CACHE_SIZE = $cacheSizeToml
"@

    $preamble = $rawToml.TrimEnd()
    if ($preamble.Length -gt 0) { $preamble = $preamble + "`n`n" }
    Set-Content -Path $codexConfigPath -Value ($preamble + $newSection) -Encoding UTF8
    Write-Ok "Wrote '$ServerName' entry to $codexConfigPath."
}

# Step 7 — install for Claude Desktop (JSON edit).
if ($installClaudeDesktop) {
    $configDir = Split-Path $claudeDesktopConfigPath -Parent
    if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
    if (-not (Test-Path $claudeDesktopConfigPath)) { '{}' | Set-Content -Path $claudeDesktopConfigPath -Encoding UTF8 }

    Write-Info "Editing $claudeDesktopConfigPath..."
    $rawJson = Get-Content $claudeDesktopConfigPath -Raw
    # CLM-safe whitespace check: avoid [string]::IsNullOrWhiteSpace static call.
    if (-not $rawJson -or -not $rawJson.Trim()) { $rawJson = '{}' }
    $config = $rawJson | ConvertFrom-Json

    if (-not $config.PSObject.Properties.Name.Contains('mcpServers')) {
        $config | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue ([pscustomobject]@{})
    }

    $serverEntry = [pscustomobject]@{
        command = $dotnetCommand
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
