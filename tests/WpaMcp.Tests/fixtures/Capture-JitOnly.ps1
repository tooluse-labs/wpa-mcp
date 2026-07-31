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

$fixturesDir = Join-Path $RepoRoot "tests\WpaMcp.Tests\fixtures"
$profilePath = Join-Path $fixturesDir "JitOnlyCapture.wprp"
$outputPath = Join-Path $fixturesDir $OutputName
$toolDll = Join-Path $RepoRoot "src\WpaMcp\bin\Release\net10.0\WpaMcp.dll"
$wpr = Join-Path $env:WINDIR "System32\wpr.exe"
$workloadRoot = Join-Path $env:TEMP ("wpa-mcp-jit-workload-{0}" -f ([Guid]::NewGuid().ToString("N")))
$projectPath = Join-Path $workloadRoot "WpaMcpJitProbe.csproj"
$programPath = Join-Path $workloadRoot "Program.cs"
$workloadDll = Join-Path $workloadRoot "bin\Release\net8.0\WpaMcpJitProbe.dll"

function Assert-Admin {
    $fltmc = Join-Path $env:WINDIR "System32\fltmc.exe"
    & $fltmc > $null 2> $null
    if ($LASTEXITCODE -ne 0) {
        throw "This script must run from Administrator PowerShell."
    }
}

function Write-JitWorkloadProject {
    param([string]$Directory)

    if (Test-Path -LiteralPath $Directory) {
        Remove-Item -LiteralPath $Directory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Directory | Out-Null

    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath $projectPath -Encoding ASCII

    $methods = ""
    for ($m = 0; $m -lt $MethodCount; $m++) {
        $methods += ("    public static long M{0}(long x) {{ long s = x + {0}L; for (int i = 0; i < 256; i++) {{ s = ((s * 1103515245L + 12345L + i) & 0x7fffffffL); }} return s; }}`r`n" -f $m)
    }

    $program = @"
using System;
using System.Reflection;

public static class WpaMcpJitProbe
{
$methods
}

public static class Program
{
    public static int Main(string[] args)
    {
        int methodCount = args.Length > 0 ? int.Parse(args[0]) : $MethodCount;
        int invocationRounds = args.Length > 1 ? int.Parse(args[1]) : $InvocationRounds;

        if (methodCount < 1 || methodCount > $MethodCount)
        {
            throw new ArgumentOutOfRangeException(nameof(methodCount), methodCount, "methodCount is outside the generated method range.");
        }

        if (invocationRounds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(invocationRounds), invocationRounds, "invocationRounds must be positive.");
        }

        Type type = typeof(WpaMcpJitProbe);
        long sum = 0L;

        for (int round = 0; round < invocationRounds; round++)
        {
            for (int m = 0; m < methodCount; m++)
            {
                MethodInfo method = type.GetMethod("M" + m.ToString(), BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    throw new MissingMethodException("WpaMcpJitProbe", "M" + m.ToString());
                }

                object result = method.Invoke(null, new object[] { (long)(m + round) });
                sum += (long)result;
            }
        }

        Console.WriteLine("JIT workload completed. Methods=" + methodCount.ToString() + " Rounds=" + invocationRounds.ToString() + " Sum=" + sum.ToString());
        return 0;
    }
}
"@

    $program | Set-Content -LiteralPath $programPath -Encoding ASCII
}

function Build-JitWorkload {
    Write-Host "Building generated .NET JIT workload..."
    & dotnet build $projectPath -c Release --nologo /p:RestoreIgnoreFailedSources=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build for JIT workload failed with exit code $LASTEXITCODE"
    }

    if (-not (Test-Path -LiteralPath $workloadDll)) {
        throw "JIT workload build did not produce expected DLL: $workloadDll"
    }
}

function Invoke-JitWorkload {
    Write-Host "Running generated .NET JIT workload..."
    & dotnet $workloadDll $MethodCount $InvocationRounds
    if ($LASTEXITCODE -ne 0) {
        throw "JIT workload failed with exit code $LASTEXITCODE"
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

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}

Push-Location $fixturesDir

$started = $false

try {
    & $wpr -cancel | Out-Null

    Write-JitWorkloadProject $workloadRoot
    Build-JitWorkload

    Write-Host "Starting WPR JIT-only capture..."
    & $wpr -start "$profilePath!ClrJitOnly" -filemode
    if ($LASTEXITCODE -ne 0) {
        throw "wpr -start failed with exit code $LASTEXITCODE"
    }
    $started = $true

    Start-Sleep -Milliseconds 250

    Invoke-JitWorkload

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
        Remove-Item -LiteralPath $workloadRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Host "Kept workload directory: $workloadRoot"
    }

    Pop-Location
}
