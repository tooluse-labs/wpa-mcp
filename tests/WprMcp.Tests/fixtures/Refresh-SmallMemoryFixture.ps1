param(
    [string]$RepoRoot = "D:\wpa-mcp",
    [string]$OutputName = "small_memory.etl",
    [string]$CandidateName = "small_memory_candidate.etl",
    [int]$SleepSeconds = 1,
    [long]$MaxBytes = 95000000,
    [double[]]$ShrinkCutoffsMs = @(250, 500, 750, 1000, 1500, 2000, 3000, 5000),
    [switch]$AllowMissingPool,
    [switch]$KeepCandidate,
    [switch]$SkipTests,
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$fixturesDir = Join-Path $RepoRoot "tests\WprMcp.Tests\fixtures"
$captureScript = Join-Path $fixturesDir "Capture-SmallMemory.ps1"
$outputPath = Join-Path $fixturesDir $OutputName
$candidatePath = Join-Path $fixturesDir $CandidateName
$toolDll = Join-Path $RepoRoot "src\WprMcp\bin\Release\net8.0\WprMcp.dll"

function Assert-Admin {
    $fltmc = Join-Path $env:WINDIR "System32\fltmc.exe"
    & $fltmc > $null 2> $null
    if ($LASTEXITCODE -ne 0) {
        throw "This script must run from Administrator PowerShell."
    }
}

function Remove-TraceCache {
    param([string]$TracePath)

    if ($TracePath -match '\.[^\\/]+$') {
        $etlxPath = $TracePath -replace '\.[^\\/]+$', '.etlx'
    }
    else {
        $etlxPath = "$TracePath.etlx"
    }

    Remove-Item -LiteralPath $etlxPath, "$etlxPath.new", "$TracePath.etlx", "$TracePath.etlx.new" -Force -ErrorAction SilentlyContinue
}

function Invoke-CommandChecked {
    param(
        [string]$Label,
        [scriptblock]$Command
    )

    Write-Host $Label
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE"
    }
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

function Test-Candidate {
    param(
        [string]$TracePath,
        [switch]$RequireSize
    )

    Remove-TraceCache $TracePath
    $memoryCount = Get-MarkerCount $TracePath "Memory/ProcessMemInfo"
    $createHandleCount = Get-MarkerCount $TracePath "Object/CreateHandle"
    $closeHandleCount = Get-MarkerCount $TracePath "Object/CloseHandle"
    $poolAllocCount = Get-MarkerCount $TracePath "Pool/PoolAllocation"
    $poolFreeCount = Get-MarkerCount $TracePath "Pool/PoolFree"
    $rawPoolAllocCount = if ($poolAllocCount -gt 0) { 0 } else { Get-MarkerCount $TracePath "0268a8b6-74fd-4302-9dd0-6e8f1795c0cf)/Opcode(32)" }
    $rawPoolFreeCount = if ($poolFreeCount -gt 0) { 0 } else { Get-MarkerCount $TracePath "0268a8b6-74fd-4302-9dd0-6e8f1795c0cf)/Opcode(34)" }
    $size = (Get-Item -LiteralPath $TracePath).Length

    Write-Host "Candidate: $TracePath"
    Write-Host "  Size: $size bytes"
    Write-Host "  Memory/ProcessMemInfo: $memoryCount"
    Write-Host "  Object/CreateHandle: $createHandleCount"
    Write-Host "  Object/CloseHandle: $closeHandleCount"
    Write-Host "  Pool/PoolAllocation: $poolAllocCount"
    Write-Host "  Pool/PoolFree: $poolFreeCount"
    Write-Host "  Raw Pool Opcode(32): $rawPoolAllocCount"
    Write-Host "  Raw Pool Opcode(34): $rawPoolFreeCount"

    if ($memoryCount -le 0) { return $false }
    if ($createHandleCount -le 0 -and $closeHandleCount -le 0) { return $false }
    if (-not $AllowMissingPool -and
        (($poolAllocCount + $rawPoolAllocCount) -le 0 -or ($poolFreeCount + $rawPoolFreeCount) -le 0)) {
        return $false
    }
    if ($RequireSize -and $size -gt $MaxBytes) { return $false }
    return $true
}

function Test-AnalyzerPoolCandidate {
    param([string]$TracePath)

    if ($AllowMissingPool) { return $true }

    $validationPath = Join-Path $fixturesDir "small_memory_fresh_validation.etl"
    Remove-Item -LiteralPath $validationPath -Force -ErrorAction SilentlyContinue
    Remove-TraceCache $validationPath
    Copy-Item -LiteralPath $TracePath -Destination $validationPath -Force
    Remove-TraceCache $validationPath

    $oldFixturePath = $env:WPRMCP_MEMORY_FIXTURE_PATH
    try {
        $env:WPRMCP_MEMORY_FIXTURE_PATH = $validationPath
        Write-Host "Validating analyzer pool rows for candidate..."
        dotnet test WprMcp.sln -c Release --no-build --filter "FullyQualifiedName~MemoryResourceAnalysis_ReturnsProcessSnapshotsAndHandleDeltas" | Out-Host
        $exitCode = $LASTEXITCODE
        return $exitCode -eq 0
    }
    finally {
        if ($null -eq $oldFixturePath) { Remove-Item Env:\WPRMCP_MEMORY_FIXTURE_PATH -ErrorAction SilentlyContinue }
        else { $env:WPRMCP_MEMORY_FIXTURE_PATH = $oldFixturePath }

        Remove-TraceCache $validationPath
        Remove-Item -LiteralPath $validationPath -Force -ErrorAction SilentlyContinue
    }
}

if ($ValidateOnly) {
    Write-Host "Refresh-SmallMemoryFixture.ps1 parsed successfully."
    Write-Host "RepoRoot: $RepoRoot"
    Write-Host "OutputPath: $outputPath"
    Write-Host "CandidatePath: $candidatePath"
    Write-Host "MaxBytes: $MaxBytes"
    return
}

Assert-Admin

if (-not (Test-Path -LiteralPath $captureScript)) {
    throw "Missing capture script: $captureScript"
}

Push-Location $RepoRoot
try {
    Invoke-CommandChecked "Building WprMcp release tool..." {
        dotnet build WprMcp.sln -c Release --no-restore
    }

    if ($AllowMissingPool -and -not $KeepCandidate) {
        throw "Refusing to overwrite the committed fixture in -AllowMissingPool mode. Re-run with -KeepCandidate and inspect the candidate manually."
    }

    Remove-Item -LiteralPath $candidatePath -Force -ErrorAction SilentlyContinue
    Remove-TraceCache $candidatePath

    Invoke-CommandChecked "Capturing raw memory candidate..." {
        powershell.exe -ExecutionPolicy Bypass -File $captureScript `
            -RepoRoot $RepoRoot `
            -OutputName $CandidateName `
            -SleepSeconds $SleepSeconds
    }

    if (-not (Test-Candidate $candidatePath)) {
        if ($AllowMissingPool) {
            throw "Raw candidate is missing required memory or handle events."
        }

        throw "Raw candidate is missing Pool/PoolAllocation or Pool/PoolFree. Do not replace the committed fixture from this capture."
    }

    $selectedPath = $candidatePath
    if ((Get-Item -LiteralPath $candidatePath).Length -le $MaxBytes) {
        if (-not (Test-AnalyzerPoolCandidate $candidatePath)) {
            throw "Raw candidate passed marker checks but memory_resource_analysis did not expose Pool rows."
        }
    }
    else {
        Write-Host "Raw candidate exceeds $MaxBytes bytes; trying shrink cutoffs..."
        $selectedPath = $null
        foreach ($cutoff in $ShrinkCutoffsMs) {
            $shrunkPath = Join-Path $fixturesDir ("small_memory_candidate_{0}ms.etl" -f $cutoff)
            Remove-Item -LiteralPath $shrunkPath -Force -ErrorAction SilentlyContinue
            Remove-TraceCache $shrunkPath

            Invoke-CommandChecked "Shrinking candidate at ${cutoff}ms..." {
                dotnet run --project tools\etlshrink -- $candidatePath $shrunkPath $cutoff
            }

            if ((Test-Candidate $shrunkPath -RequireSize) -and (Test-AnalyzerPoolCandidate $shrunkPath)) {
                $selectedPath = $shrunkPath
                break
            }
        }

        if ($null -eq $selectedPath) {
            throw "No shrink cutoff produced a fixture <= $MaxBytes bytes with all required markers and analyzer-visible Pool rows."
        }
    }

    Remove-TraceCache $outputPath
    Move-Item -LiteralPath $selectedPath -Destination $outputPath -Force
    Remove-TraceCache $outputPath

    if (-not $KeepCandidate) {
        Remove-Item -LiteralPath $candidatePath -Force -ErrorAction SilentlyContinue
        Remove-TraceCache $candidatePath
        foreach ($cutoff in $ShrinkCutoffsMs) {
            $shrunkPath = Join-Path $fixturesDir ("small_memory_candidate_{0}ms.etl" -f $cutoff)
            Remove-Item -LiteralPath $shrunkPath -Force -ErrorAction SilentlyContinue
            Remove-TraceCache $shrunkPath
        }
    }

    Write-Host "Replaced fixture: $outputPath"
    Test-Candidate $outputPath -RequireSize | Out-Null
    if (-not (Test-AnalyzerPoolCandidate $outputPath)) {
        throw "Replaced fixture passed marker checks but memory_resource_analysis did not expose Pool rows."
    }

    if (-not $SkipTests) {
        Remove-TraceCache $outputPath
        Invoke-CommandChecked "Running memory fixture tests..." {
            dotnet test WprMcp.sln -c Release --no-build --filter "FullyQualifiedName~MemoryResourceAnalysisTests|FullyQualifiedName~InspectTraceTests"
        }
    }
}
finally {
    Pop-Location
}
