# Captures small_cpu.etl, small_fileio.etl, small_mmap.etl
# Requires Administrator. Triggered via Start-Process -Verb RunAs.
# Designed to work under PowerShell 5.1 Constrained Language Mode.

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $PSCommandPath
Set-Location $here

$log = Join-Path $here 'capture_all.log'
"=== capture_all.ps1 started $(Get-Date -Format 'o') ===" | Out-File $log -Encoding utf8

function Log($msg) {
    $line = "$(Get-Date -Format 'HH:mm:ss')  $msg"
    Write-Host $line
    $line | Out-File $log -Append -Encoding utf8
}

function ReportSize($file, $tag) {
    if (Test-Path $file) {
        $bytes = (Get-Item $file).Length
        $mb = $bytes / 1048576
        Log "$tag $file  $('{0:N0}' -f $bytes) bytes  ($('{0:N2}' -f $mb) MB)"
    } else {
        Log "$tag $file  NOT CREATED"
    }
}

# Run a wpr.exe command, capturing both stdout and stderr to a tmp file we
# then log. Native command stderr in CLM is wrapped as ErrorRecord; the only
# safe way to inspect it is via redirection to a file (PS file redirection
# does NOT trigger NativeCommandError wrapping).
$script:wprSeq = 0
function RunWpr {
    param([Parameter(Mandatory=$true)][string]$WprArgs)
    $script:wprSeq++
    $tmp = Join-Path $env:TEMP ("wpr_out_{0}_{1}.log" -f $PID, $script:wprSeq)
    Log "    wpr.exe $WprArgs"
    cmd.exe /c "wpr.exe $WprArgs > `"$tmp`" 2>&1"
    $code = $LASTEXITCODE
    if (Test-Path $tmp) {
        Get-Content $tmp | ForEach-Object { Log "      $_" }
        Remove-Item $tmp -Force
    }
    Log "    wpr exit: $code"
    return $code
}

Log "Working dir: $here"

# Cancel any leftover wpr session.
Log "wpr -cancel..."
RunWpr "-cancel" | Out-Null

# Clean up old artifacts.
foreach ($f in @('small_cpu.etl','small_cpu.etlx','small_cpu.perfView.csv',
                 'small_fileio.etl','small_fileio.etlx','small_fileio.perfView.csv',
                 'small_mmap.etl','small_mmap.etlx','small_mmap.perfView.csv')) {
    $p = Join-Path $here $f
    if (Test-Path $p) { Log "  removing $f"; Remove-Item $p -Force -ErrorAction SilentlyContinue }
}
foreach ($d in @('small_cpu.etl.NGENPDB','small_fileio.etl.NGENPDB','small_mmap.etl.NGENPDB')) {
    $p = Join-Path $here $d
    if (Test-Path $p) { Log "  removing dir $d"; Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
}

# ===========================================================
# Fixture 1: small_cpu.etl  (CPU sampling, Light detail)
# ===========================================================
Log "[1/3] starting CPU.light profile..."
RunWpr "-start CPU.light -filemode" | Out-Null
Start-Sleep -Seconds 2
Log "[1/3] stopping..."
RunWpr "-stop `"$(Join-Path $here 'small_cpu.etl')`"" | Out-Null
ReportSize 'small_cpu.etl' '[1/3]'

# ===========================================================
# Fixture 2: small_fileio.etl  (FileIO, very short workload)
# ===========================================================
Log "[2/3] starting FileIO.light profile..."
RunWpr "-start FileIO.light -filemode" | Out-Null
Log "[2/3] running tiny workload (Get-ChildItem 'C:\Windows\System32\drivers')..."
# Much smaller scope than full System32 — drivers folder is ~1k files.
Get-ChildItem 'C:\Windows\System32\drivers' -ErrorAction SilentlyContinue | Out-Null
Start-Sleep -Milliseconds 500
Log "[2/3] stopping..."
RunWpr "-stop `"$(Join-Path $here 'small_fileio.etl')`"" | Out-Null
ReportSize 'small_fileio.etl' '[2/3]'

# ===========================================================
# Fixture 3: small_mmap.etl
# ===========================================================
$wprp = Join-Path $here 'MmapCapture.wprp'
if (-not (Test-Path $wprp)) {
    Log "[3/3] ERROR: MmapCapture.wprp missing"
    exit 2
}
Log "[3/3] starting MmapCapture profile (small buffers)..."
RunWpr "-start `"$wprp`" -filemode" | Out-Null

Log "[3/3] running mmap workload (8 short-lived processes)..."
$exes = @(
    'C:\Windows\System32\where.exe',
    'C:\Windows\System32\sort.exe',
    'C:\Windows\System32\find.exe',
    'C:\Windows\System32\hostname.exe',
    'C:\Windows\System32\whoami.exe',
    'C:\Windows\System32\fc.exe',
    'C:\Windows\System32\comp.exe',
    'C:\Windows\System32\timeout.exe'
)
foreach ($e in $exes) {
    if (Test-Path $e) {
        Start-Process -FilePath $e -ArgumentList '/?' -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Milliseconds 500
Log "[3/3] stopping..."
RunWpr "-stop `"$(Join-Path $here 'small_mmap.etl')`"" | Out-Null
ReportSize 'small_mmap.etl' '[3/3]'

# Summary
Log ""
Log "=== Summary ==="
foreach ($f in @('small_cpu.etl','small_fileio.etl','small_mmap.etl')) {
    ReportSize $f '  '
}
Log "=== capture_all.ps1 done $(Get-Date -Format 'o') ==="
exit 0
