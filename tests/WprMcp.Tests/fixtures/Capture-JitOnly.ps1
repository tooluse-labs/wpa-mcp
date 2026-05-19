param(
    [string]$RepoRoot = "D:\wpa-mcp",
    [string]$OutputName = "jit_only.etl",
    [int]$MethodCount = 240,
    [int]$InvocationRounds = 2,
    [switch]$SkipValidation,
    [switch]$KeepWorkloadScript
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$fixturesDir = Join-Path $RepoRoot "tests\WprMcp.Tests\fixtures"
$profilePath = Join-Path $fixturesDir "JitOnlyCapture.wprp"
$outputPath = Join-Path $fixturesDir $OutputName
$toolDll = Join-Path $RepoRoot "src\WprMcp\bin\Release\net8.0\WprMcp.dll"
$wpr = Join-Path $env:WINDIR "System32\wpr.exe"
$powershell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
$workloadScript = Join-Path $env:TEMP ("wpa-mcp-jit-workload-{0}.ps1" -f ([Guid]::NewGuid().ToString("N")))

function Assert-Admin {
    $fltmc = Join-Path $env:WINDIR "System32\fltmc.exe"
    & $fltmc > $null 2> $null
    if ($LASTEXITCODE -ne 0) {
        throw "This script must run from Administrator PowerShell."
    }
}

function Write-JitWorkloadScript {
    param([string]$Path)

    @'
param(
    [int]$MethodCount = 240,
    [int]$InvocationRounds = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$methods = New-Object System.Text.StringBuilder
for ($m = 0; $m -lt $MethodCount; $m++) {
    [void]$methods.AppendLine(("    public static long M{0}(long x) {{ long s = x + {0}; for (int i = 0; i < 256; i++) {{ s = ((s * 1103515245L + 12345L + i) & 0x7fffffffL); }} return s; }}" -f $m))
}

$methodText = $methods.ToString()
$source = @"
using System;
public static class WpaMcpJitProbe
{
$methodText
}
"@

Add-Type -TypeDefinition $source -Language CSharp
$type = [WpaMcpJitProbe]
$sum = 0L

for ($round = 0; $round -lt $InvocationRounds; $round++) {
    for ($m = 0; $m -lt $MethodCount; $m++) {
        $method = $type.GetMethod(("M{0}" -f $m))
        $sum += [long]$method.Invoke($null, @([long]($m + $round)))
    }
}

Write-Host "JIT workload completed. Methods=$MethodCount Rounds=$InvocationRounds Sum=$sum"
'@ | Set-Content -LiteralPath $Path -Encoding ASCII
}

function Get-MarkerCount {
    param(
        [string]$TracePath,
        [string]$Marker
    )

    $jsonText = & dotnet exec $toolDll --find-marker $TracePath $Marker count_by_event 20
    if ($LASTEXITCODE -ne 0) {
        throw "find-marker '$Marker' failed with exit code $LASTEXITCODE"
    }

    $json = ($jsonText -join "`n") | ConvertFrom-Json
    return [long]$json.TotalMatched
}

function Invoke-MarkerValidation {
    param([string]$TracePath)

    if (-not (Test-Path -LiteralPath $toolDll)) {
        Write-Host "Skipping marker validation because the Release tool DLL is missing: $toolDll"
        Write-Host "Build first, then validate with:"
        Write-Host "  dotnet exec $toolDll --find-marker $TracePath JittingStarted count_by_event 20"
        Write-Host "  dotnet exec $toolDll --find-marker $TracePath LoadVerbose count_by_event 20"
        return
    }

    $jittingStarted = Get-MarkerCount $TracePath "JittingStarted"
    $loadVerbose = Get-MarkerCount $TracePath "LoadVerbose"

    Write-Host "Validation markers:"
    Write-Host "  JittingStarted: $jittingStarted"
    Write-Host "  LoadVerbose: $loadVerbose"

    if ($jittingStarted -le 0 -or $loadVerbose -le 0) {
        throw "Captured trace did not contain both JittingStarted and LoadVerbose CLR events."
    }
}

Assert-Admin

if ($MethodCount -le 0) {
    throw "MethodCount must be > 0."
}

if ($InvocationRounds -le 0) {
    throw "InvocationRounds must be > 0."
}

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

try {
    Write-JitWorkloadScript $workloadScript
    & $wpr -cancel | Out-Null

    Write-Host "Starting WPR JIT-only capture..."
    & $wpr -start "$profilePath!ClrJitOnly" -filemode
    if ($LASTEXITCODE -ne 0) {
        throw "wpr -start failed with exit code $LASTEXITCODE"
    }
    $started = $true

    Start-Sleep -Milliseconds 250

    Write-Host "Running generated .NET JIT workload..."
    & $powershell -NoProfile -ExecutionPolicy Bypass -File $workloadScript `
        -MethodCount $MethodCount `
        -InvocationRounds $InvocationRounds
    if ($LASTEXITCODE -ne 0) {
        throw "JIT workload failed with exit code $LASTEXITCODE"
    }

    Start-Sleep -Milliseconds 250

    Write-Host "Stopping WPR capture..."
    Write-Host "WPR may print 'Press Ctrl+C to cancel'; do not press Ctrl+C while it merges the trace."
    & $wpr -stop $outputPath "wpa-mcp JIT-only CLR capture" -skipPdbGen -compress
    if ($LASTEXITCODE -ne 0) {
        throw "wpr -stop failed with exit code $LASTEXITCODE"
    }
    $started = $false

    $item = Get-Item -LiteralPath $outputPath
    Write-Host "Captured: $($item.FullName)"
    Write-Host "Size: $($item.Length) bytes"

    if (-not $SkipValidation) {
        Invoke-MarkerValidation $outputPath
    }
}
finally {
    if ($started) {
        Write-Host "Capture was still running; cancelling WPR session..."
        & $wpr -cancel | Out-Null
    }

    if (-not $KeepWorkloadScript) {
        Remove-Item -LiteralPath $workloadScript -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Host "Kept workload script: $workloadScript"
    }

    Pop-Location
}
