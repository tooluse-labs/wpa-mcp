param(
    [string]$RepoRoot = "D:\wpa-mcp",
    [string]$OutputName = "small_memory.etl",
    [int]$SleepSeconds = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$fixturesDir = Join-Path $RepoRoot "tests\WprMcp.Tests\fixtures"
$profilePath = Join-Path $fixturesDir "MemoryCapture.wprp"
$outputPath = Join-Path $fixturesDir $OutputName
$wpr = Join-Path $env:WINDIR "System32\wpr.exe"

function Assert-Admin {
    $fltmc = Join-Path $env:WINDIR "System32\fltmc.exe"
    & $fltmc > $null 2> $null
    if ($LASTEXITCODE -ne 0) {
        throw "This script must run from Administrator PowerShell."
    }
}

Assert-Admin

if (-not (Test-Path -LiteralPath $wpr)) {
    throw "Missing WPR executable: $wpr"
}

if (-not (Test-Path -LiteralPath $profilePath)) {
    throw "Missing WPR profile: $profilePath"
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}

Push-Location $fixturesDir

$started = $false
$buffers = @()

try {
    Write-Host "Starting WPR memory capture..."
    & $wpr -start "$profilePath!MemoryMcp" -filemode
    if ($LASTEXITCODE -ne 0) {
        throw "wpr -start failed with exit code $LASTEXITCODE"
    }
    $started = $true

    Write-Host "Creating memory and handle activity..."
    for ($i = 0; $i -lt 64; $i++) {
        $buffers += ,(New-Object byte[] 1048576)
    }

    $kernel32 = Join-Path $env:WINDIR "System32\kernel32.dll"
    for ($i = 0; $i -lt 100; $i++) {
        Get-Content -LiteralPath $kernel32 -Encoding Byte -TotalCount 1 | Out-Null
    }

    Start-Sleep -Seconds $SleepSeconds

    Write-Host "Stopping WPR capture..."
    Write-Host "WPR may print 'Press Ctrl+C to cancel'; do not press Ctrl+C while it merges the trace."
    & $wpr -stop $outputPath "wpa-mcp small memory fixture" -skipPdbGen -compress
    if ($LASTEXITCODE -ne 0) {
        throw "wpr -stop failed with exit code $LASTEXITCODE"
    }
    $started = $false

    $item = Get-Item -LiteralPath $outputPath
    Write-Host "Captured: $($item.FullName)"
    Write-Host "Size: $($item.Length) bytes"
}
finally {
    if ($started) {
        Write-Host "Capture was still running; cancelling WPR session..."
        & $wpr -cancel | Out-Null
    }

    Pop-Location
}
