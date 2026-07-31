param(
    [string]$RepoRoot = "D:\wpa-mcp",
    [string]$OutputName = "small_wait_bound.etl",
    [int]$SleepWorkerCount = 6,
    [int]$CpuWorkerCount = 2,
    [int]$Iterations = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$fixturesDir = Join-Path $RepoRoot "tests\WpaMcp.Tests\fixtures"
$profilePath = Join-Path $fixturesDir "WaitBoundCapture.wprp"
$outputPath = Join-Path $fixturesDir $OutputName
$wpr = Join-Path $env:WINDIR "System32\wpr.exe"
$powershell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"

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

if (-not (Test-Path -LiteralPath $powershell)) {
    throw "Missing PowerShell executable: $powershell"
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}

Push-Location $fixturesDir

$started = $false
$children = @()

try {
    & $wpr -cancel | Out-Null

    Write-Host "Starting WPR wait-bound capture..."
    & $wpr -start "$profilePath!WaitBoundMcp" -filemode
    if ($LASTEXITCODE -ne 0) {
        throw "wpr -start failed with exit code $LASTEXITCODE"
    }
    $started = $true

    Write-Host "Starting wait-heavy worker processes..."
    $sleepWork = "for (`$i = 0; `$i -lt $Iterations; `$i++) { Start-Sleep -Milliseconds 50 }"
    for ($i = 0; $i -lt $SleepWorkerCount; $i++) {
        $children += Start-Process -FilePath $powershell `
            -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $sleepWork) `
            -WindowStyle Hidden `
            -PassThru
    }

    Write-Host "Starting scheduler-heavy worker processes..."
    $cpuWork = "`$sum = 0; for (`$j = 0; `$j -lt 8000000; `$j++) { `$sum += (`$j % 7) }; Write-Output `$sum | Out-Null"
    for ($i = 0; $i -lt $CpuWorkerCount; $i++) {
        $children += Start-Process -FilePath $powershell `
            -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $cpuWork) `
            -WindowStyle Hidden `
            -PassThru
    }

    foreach ($child in $children) {
        if ($null -ne $child) {
            $child.WaitForExit()
        }
    }
    Start-Sleep -Milliseconds 250

    Write-Host "Stopping WPR capture..."
    Write-Host "WPR may print 'Press Ctrl+C to cancel'; do not press Ctrl+C while it merges the trace."
    & $wpr -stop $outputPath "wpa-mcp wait-bound fixture" -skipPdbGen -compress
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
