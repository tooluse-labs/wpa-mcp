<#
.SYNOPSIS
  One-line installer for wpa-mcp. Downloads the latest self-contained Windows
  bundle and registers it with detected MCP clients.

.DESCRIPTION
  Designed to be invoked over the network, no clone and no .NET install needed:

    iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) }"

  The script:
    1. Resolves the latest GitHub Release tag, unless -Tag or VERSION is set.
    2. Downloads wpa-mcp-win-x64.zip and installs:
       - %USERPROFILE%\.local\bin\wpa-mcp.exe
       - %USERPROFILE%\.local\native\amd64\*.dll
    3. Registers the executable directly with Claude Code, Codex, and Claude
       Desktop when those clients are detected.

.PARAMETER Client
  Which MCP client to install for: claude-code, codex, claude-desktop, or auto.

.PARAMETER Scope
  Claude Code scope for `claude mcp add`: user, local, or project. Defaults to
  SCOPE env var, then user. Codex and Claude Desktop ignore this.

.PARAMETER SymbolLocalRoot
  Approved local PDB candidate directory used by prepare_symbols. Defaults to
  %LOCALAPPDATA%\WpaMcp\symbol-candidates. Remote symbol servers are not imported.

.PARAMETER SymbolStoreRoot
  Private verified PDB store used by prepare_symbols. Defaults to
  %LOCALAPPDATA%\WpaMcp\symbol-store and must be disjoint from SymbolLocalRoot.

.PARAMETER CacheSize
  Value passed to wpa-mcp via --cache-size. Defaults to 2.

.PARAMETER ForceDownload
  Download and replace the executable even if the installed copy looks complete.

.PARAMETER Diagnostics
  Print Claude Code registration diagnostics. Also enabled by
  WPA_MCP_INSTALL_DIAGNOSTICS=1.
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
    [string]$SymbolLocalRoot = (Join-Path $env:LOCALAPPDATA 'WpaMcp\symbol-candidates'),
    [string]$SymbolStoreRoot = (Join-Path $env:LOCALAPPDATA 'WpaMcp\symbol-store'),
    [int]$CacheSize = 2,
    [switch]$ForceDownload,
    [switch]$Diagnostics
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host "[install] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[install] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "[install] $msg" -ForegroundColor Yellow }
function Write-Diag($msg) { Write-Host "[install:diag] $msg" -ForegroundColor DarkGray }

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
    return @(
        '--symbol-local-root', $SymbolLocalRoot,
        '--symbol-store-root', $SymbolStoreRoot,
        '--cache-size', "$CacheSize"
    )
}

function Move-WithRetry {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $destDir = Split-Path -Parent $Destination
    $destName = Split-Path -Leaf $Destination
    $oldFiles = @(Get-ChildItem -LiteralPath $destDir -Filter "$destName.old-*" -Name -ErrorAction SilentlyContinue)
    foreach ($oldName in $oldFiles) {
        Remove-Item -LiteralPath (Join-Path $destDir $oldName) -Force -ErrorAction SilentlyContinue
    }

    for ($i = 1; $i -le 5; $i++) {
        try {
            if (Test-Path $Destination) {
                $aside = Join-Path $destDir "$destName.old-$(Get-Random)"
                Move-Item -LiteralPath $Destination -Destination $aside -Force
            }
            Move-Item -LiteralPath $Source -Destination $Destination -Force
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

function Test-TruthyEnv {
    param([string]$Value)

    if (-not $Value) { return $false }
    return @('1', 'true', 'yes', 'on') -contains $Value.ToLowerInvariant()
}

function Test-DiagnosticsEnabled {
    return $Diagnostics -or (Test-TruthyEnv $env:WPA_MCP_INSTALL_DIAGNOSTICS)
}

function Join-DiagnosticLines {
    param($Lines)

    $items = @($Lines)
    if ($items.Count -eq 0) { return '<no output>' }
    return ($items | ForEach-Object { "$_" }) -join ' | '
}

function Write-DiagnosticTokens {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][object[]]$Tokens
    )

    Write-Diag "$Label token count: $($Tokens.Count)"
    for ($i = 0; $i -lt $Tokens.Count; $i++) {
        Write-Diag "$Label[$i]: $($Tokens[$i])"
    }
}

function New-ClaudeServerJson {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][object[]]$Args
    )

    $serverObj = [ordered]@{
        type    = 'stdio'
        command = $Command
        args    = @($Args)
    }
    $serverJson = $serverObj | ConvertTo-Json -Compress

    # Windows PowerShell 5.1 strips inner double quotes when passing strings to
    # native commands. Backslash-escape so claude receives valid JSON. PS 7.3+
    # with default Standard argument passing does not need this.
    if ($PSVersionTable.PSVersion.Major -lt 6) {
        $serverJson = $serverJson -replace '"', '\"'
    }
    return $serverJson
}

function Write-ClaudeDiagnostics {
    param(
        [Parameter(Mandatory)][string]$BinaryPath,
        [Parameter(Mandatory)][string]$ClaudeScope,
        [Parameter(Mandatory)][object[]]$ServerArgs
    )

    Write-Diag "PowerShell version: $($PSVersionTable.PSVersion)"
    Write-Diag "PowerShell edition: $($PSVersionTable.PSEdition)"
    Write-Diag "PowerShell language mode: $($ExecutionContext.SessionState.LanguageMode)"

    $claudeCommands = @(Get-Command claude -All -ErrorAction SilentlyContinue)
    if ($claudeCommands.Count -eq 0) {
        Write-Diag "Get-Command claude -All found no candidates."
    } else {
        for ($i = 0; $i -lt $claudeCommands.Count; $i++) {
            $cmd = $claudeCommands[$i]
            $source = $cmd.Source
            if (-not $source) { $source = $cmd.Definition }
            Write-Diag "claude candidate[$i]: type=$($cmd.CommandType) source=$source"
        }
    }

    try {
        $versionOutput = @(& claude --version)
        Write-Diag "claude --version exit=$LASTEXITCODE output=$(Join-DiagnosticLines $versionOutput)"
    } catch {
        Write-Diag "claude --version threw: $_"
    }

    try {
        $mcpHelp = @(& claude mcp --help)
        $hasAddJson = @($mcpHelp | Where-Object { $_ -match 'add-json' }).Count -gt 0
        Write-Diag "claude mcp --help exit=$LASTEXITCODE has-add-json=$hasAddJson"
        foreach ($line in $mcpHelp) {
            if ($line -match 'Usage:|add-json| add \[options\]') {
                Write-Diag "claude mcp --help: $line"
            }
        }
    } catch {
        Write-Diag "claude mcp --help threw: $_"
    }

    try {
        $addHelp = @(& claude mcp add --help)
        Write-Diag "claude mcp add --help exit=$LASTEXITCODE"
        foreach ($line in $addHelp) {
            if ($line -match 'Usage:|args\.\.\.|subprocess flags|--scope') {
                Write-Diag "claude mcp add --help: $line"
            }
        }
    } catch {
        Write-Diag "claude mcp add --help threw: $_"
    }

    $serverJson = New-ClaudeServerJson -Command $BinaryPath -Args $ServerArgs
    Write-DiagnosticTokens 'claude mcp add-json' @('claude', 'mcp', 'add-json', '--scope', $ClaudeScope, $ServerName, $serverJson)
    Write-DiagnosticTokens 'claude mcp add' (@('claude', 'mcp', 'add', $ServerName, '--scope', $ClaudeScope, '--', $BinaryPath) + @($ServerArgs))
}

function Test-UsableBinary {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path $Path)) { return $false }

    try {
        $item = Get-Item -LiteralPath $Path
        if ($item.Length -le 0) { return $false }

        & $Path --version | Out-Null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Find-ReleaseAsset {
    param(
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][string]$AssetName
    )

    return @($Release.assets) | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
}

function Test-InstalledBinaryMatchesRelease {
    param(
        [Parameter(Mandatory)][string]$BinaryPath,
        $ReleaseAsset
    )

    if (-not $ReleaseAsset) { return $false }
    if (-not (Test-UsableBinary -Path $BinaryPath)) { return $false }

    $item = Get-Item -LiteralPath $BinaryPath

    $digest = $ReleaseAsset.digest
    if ($digest -and $digest.Length -gt 7 -and $digest.Substring(0, 7).ToLowerInvariant() -eq 'sha256:') {
        $expectedHash = $digest.Substring(7)
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $BinaryPath).Hash
        return $actualHash -eq $expectedHash
    }

    return $false
}

function Get-InstallRoot {
    param([Parameter(Mandatory)][string]$ResolvedInstallDir)

    $root = Split-Path -Parent $ResolvedInstallDir
    if (-not $root) {
        throw "InstallDir '$ResolvedInstallDir' must have a parent directory so native dependencies can be installed beside bin."
    }
    return $root
}

function Test-NativeDependenciesPresent {
    param([Parameter(Mandatory)][string]$InstallRoot)

    $rootRelativeNativeDir = Join-Path $InstallRoot 'native\amd64'
    return (Test-Path (Join-Path $rootRelativeNativeDir 'msdia140.dll')) -and
           (Test-Path (Join-Path $rootRelativeNativeDir 'KernelTraceControl.dll'))
}

function Test-SameFileHash {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path $Destination)) { return $false }

    try {
        $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Source).Hash
        $destinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash
        return $sourceHash -eq $destinationHash
    } catch {
        return $false
    }
}

function Copy-FileIfDifferent {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (Test-SameFileHash -Source $Source -Destination $Destination) { return }

    try {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
    } catch {
        if (Test-SameFileHash -Source $Source -Destination $Destination) { return }
        throw "Could not replace native dependency $Destination. Close MCP clients using wpa-mcp and re-run the installer. Last error: $_"
    }
}

function Test-InstalledBundleMatchesRelease {
    param(
        [Parameter(Mandatory)][string]$BinaryPath,
        [Parameter(Mandatory)][string]$InstallRoot,
        $ReleaseAsset
    )

    if (-not $ReleaseAsset) { return $false }
    if (-not (Test-UsableBinary -Path $BinaryPath)) { return $false }
    if (-not (Test-NativeDependenciesPresent -InstallRoot $InstallRoot)) { return $false }

    $digest = $ReleaseAsset.digest
    if ($digest -and $digest.Length -gt 7 -and $digest.Substring(0, 7).ToLowerInvariant() -eq 'sha256:') {
        $expectedHash = $digest.Substring(7)
        $markerPath = Join-Path $InstallRoot '.wpa-mcp-win-x64.sha256'
        if (-not (Test-Path $markerPath)) { return $false }
        $actualHash = (Get-Content -LiteralPath $markerPath -ErrorAction SilentlyContinue | Select-Object -First 1)
        return $actualHash -eq $expectedHash
    }

    return $false
}

function Copy-NativeDependencies {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$InstallRoot
    )

    $sourceNative = Join-Path $SourceRoot 'native'
    if (-not (Test-Path (Join-Path $sourceNative 'amd64\msdia140.dll'))) {
        throw "Release bundle is missing native\amd64\msdia140.dll; native PDB symbol resolution would fail."
    }
    if (-not (Test-Path (Join-Path $sourceNative 'amd64\KernelTraceControl.dll'))) {
        throw "Release bundle is missing native\amd64\KernelTraceControl.dll."
    }

    $sourceArch = Join-Path $sourceNative 'amd64'
    $targetArch = Join-Path $InstallRoot 'native\amd64'
    New-Item -ItemType Directory -Path $targetArch -Force | Out-Null
    foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceArch -File) {
        $targetPath = Join-Path $targetArch $sourceFile.Name
        Copy-FileIfDifferent -Source $sourceFile.FullName -Destination $targetPath
    }
}

function Install-Binary {
    $resolvedInstallDir = $InstallDir
    if (-not $resolvedInstallDir) {
        $resolvedInstallDir = $env:INSTALL_DIR
    }
    if (-not $resolvedInstallDir) {
        $resolvedInstallDir = Join-Path $env:USERPROFILE '.local\bin'
    }

    $installRoot = Get-InstallRoot -ResolvedInstallDir $resolvedInstallDir
    $zipAssetName = 'wpa-mcp-win-x64.zip'
    $exeAssetName = 'wpa-mcp-win-x64.exe'
    $release = $null
    $resolvedTag = $Tag
    if (-not $resolvedTag) {
        $resolvedTag = $env:VERSION
    }
    if (-not $resolvedTag) {
        Write-Info "Querying latest release of $Owner/$Repo..."
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/latest" -UseBasicParsing
        $resolvedTag = $release.tag_name
    } else {
        try {
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/tags/$resolvedTag" -UseBasicParsing
        } catch {
            Write-Warn "Could not query release metadata for $resolvedTag; download skip check disabled."
        }
    }
    if (-not $resolvedTag) {
        throw "Could not resolve a release tag for $Owner/$Repo."
    }
    Write-Ok "Tag: $resolvedTag"

    New-Item -ItemType Directory -Path $resolvedInstallDir -Force | Out-Null
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null

    $zipReleaseAsset = if ($release) { Find-ReleaseAsset -Release $release -AssetName $zipAssetName } else { $null }
    $exeReleaseAsset = if ($release) { Find-ReleaseAsset -Release $release -AssetName $exeAssetName } else { $null }
    $binaryPath = Join-Path $resolvedInstallDir 'wpa-mcp.exe'
    $force = $ForceDownload -or (Test-TruthyEnv $env:FORCE_DOWNLOAD) -or (Test-TruthyEnv $env:WPA_MCP_FORCE_DOWNLOAD)

    if ($zipReleaseAsset) {
        if (-not $force -and (Test-InstalledBundleMatchesRelease -BinaryPath $binaryPath -InstallRoot $installRoot -ReleaseAsset $zipReleaseAsset)) {
            Write-Ok "Using existing complete bundle at $installRoot"
            return $binaryPath
        }

        $assetUrl = "https://github.com/$Owner/$Repo/releases/download/$resolvedTag/$zipAssetName"
        $tempPath = Join-Path $resolvedInstallDir "wpa-mcp-$resolvedTag.download.zip"
        $stagePath = Join-Path $resolvedInstallDir "wpa-mcp-$resolvedTag.staging"
        if (Test-Path $stagePath) {
            Remove-Item -LiteralPath $stagePath -Recurse -Force
        }

        Write-Info "Downloading $assetUrl..."
        Invoke-WebRequest -Uri $assetUrl -OutFile $tempPath -UseBasicParsing
        Expand-Archive -LiteralPath $tempPath -DestinationPath $stagePath -Force

        $stagedBinary = Join-Path $stagePath 'bin\wpa-mcp.exe'
        if (-not (Test-UsableBinary -Path $stagedBinary)) {
            throw "Downloaded release bundle does not contain a usable bin\wpa-mcp.exe."
        }

        Copy-NativeDependencies -SourceRoot $stagePath -InstallRoot $installRoot
        Move-WithRetry -Source $stagedBinary -Destination $binaryPath

        $digest = $zipReleaseAsset.digest
        if ($digest -and $digest.Length -gt 7 -and $digest.Substring(0, 7).ToLowerInvariant() -eq 'sha256:') {
            Set-Content -LiteralPath (Join-Path $installRoot '.wpa-mcp-win-x64.sha256') -Value $digest.Substring(7) -Encoding ASCII
        }

        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stagePath -Recurse -Force -ErrorAction SilentlyContinue
        Write-Ok "Installed $binaryPath"

        return $binaryPath
    }

    if (-not $force -and (Test-InstalledBinaryMatchesRelease -BinaryPath $binaryPath -ReleaseAsset $exeReleaseAsset)) {
        Write-Ok "Using existing complete $binaryPath"
        return $binaryPath
    }

    $assetUrl = "https://github.com/$Owner/$Repo/releases/download/$resolvedTag/$exeAssetName"
    $tempPath = Join-Path $resolvedInstallDir "wpa-mcp-$resolvedTag.download.exe"
    Write-Info "Downloading $assetUrl..."
    Invoke-WebRequest -Uri $assetUrl -OutFile $tempPath -UseBasicParsing
    if (Get-Command Unblock-File -ErrorAction SilentlyContinue) {
        try { Unblock-File -Path $tempPath } catch { }
    }

    Move-WithRetry -Source $tempPath -Destination $binaryPath
    if (-not (Test-NativeDependenciesPresent -InstallRoot $installRoot)) {
        Write-Warn "Installed legacy single-exe asset without native DIA DLLs; native PDB symbol resolution may fail. Install a release with $zipAssetName when available."
    }
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

    $serverArgs = New-ServerArgs
    if (Test-DiagnosticsEnabled) {
        Write-ClaudeDiagnostics -BinaryPath $BinaryPath -ClaudeScope $ClaudeScope -ServerArgs $serverArgs
    }

    Write-Info "Registering with Claude Code ($ClaudeScope scope)..."
    & claude mcp remove $ServerName --scope $ClaudeScope
    if ($LASTEXITCODE -ne 0) {
        Write-Info "(no prior $ServerName entry to remove; expected on first install)"
    }

    $serverJson = New-ClaudeServerJson -Command $BinaryPath -Args $serverArgs
    & claude mcp add-json --scope $ClaudeScope $ServerName $serverJson
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "claude mcp add-json failed (exit $LASTEXITCODE); falling back to claude mcp add."
        & claude mcp add $ServerName --scope $ClaudeScope -- $BinaryPath @serverArgs
        if ($LASTEXITCODE -ne 0) {
            if (-not (Test-DiagnosticsEnabled)) {
                Write-Warn "For Claude registration diagnostics, rerun with -Diagnostics or set WPA_MCP_INSTALL_DIAGNOSTICS=1."
            }
            throw 'claude mcp add failed.'
        }
    }

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
Write-Host "  - Put approved local PDB candidates under: $SymbolLocalRoot"
Write-Host '  - After load_trace, call prepare_symbols to create an immutable symbol context.'
