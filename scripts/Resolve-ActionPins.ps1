[CmdletBinding()]
param(
    [string]$InputPath = 'eng/action-pin-inputs.v1.json',
    [string]$OutputPath = 'artifacts/action-pins.candidate.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Utf8File {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content
    )

    $temporaryPath = "$Path.utf8.tmp"
    try {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
        Set-Content -LiteralPath $temporaryPath -Value $Content -Encoding UTF8 -NoNewline
        [byte[]]$bytes = Get-Content -LiteralPath $temporaryPath -Encoding Byte -Raw
        $offset = if ($bytes.Count -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
        if (Test-Path -LiteralPath $Path) {
            Remove-Item -LiteralPath $Path -Force
        }
        [void](New-Item -ItemType File -Path $Path)
        if ($bytes.Count -gt $offset) {
            [byte[]]$payload = $bytes[$offset..($bytes.Count - 1)]
            Set-Content -LiteralPath $Path -Value $payload -Encoding Byte -NoNewline
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Resolve-TagCommit {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag
    )

    $remote = "https://github.com/$Repository.git"
    $tagRef = "refs/tags/$Tag"
    $peeledRef = "$tagRef^{}"
    $command = "git ls-remote $remote refs/tags/$tag refs/tags/$tag^{}"
    $resolvedCommits = @()

    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $lines = @(& git ls-remote $remote $tagRef $peeledRef)
        if ($LASTEXITCODE -ne 0) {
            throw "Action tag lookup failed on attempt ${attempt}: $command"
        }

        $refMatches = @()
        foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ($line -notmatch '^(?<commit>[0-9a-fA-F]{40})\s+(?<ref>\S+)$') {
                throw "Unexpected git ls-remote output for ${Repository}@${Tag}: $line"
            }
            $refMatches += [ordered]@{
                Commit = $Matches.commit.ToLowerInvariant()
                Ref = $Matches.ref
            }
        }

        $peeled = @($refMatches | Where-Object { $_.Ref -ceq $peeledRef })
        $direct = @($refMatches | Where-Object { $_.Ref -ceq $tagRef })
        if ($peeled.Count -gt 1 -or $direct.Count -gt 1) {
            throw "Ambiguous action tag result for ${Repository}@${Tag}."
        }

        $selected = if ($peeled.Count -eq 1) { $peeled[0] } elseif ($direct.Count -eq 1) { $direct[0] } else { $null }
        if ($null -eq $selected) {
            throw "Action tag was not found: ${Repository}@${Tag}."
        }
        if ($selected.Commit -notmatch '^[0-9a-fA-F]{40}$') {
            throw "Action tag did not resolve to a full commit SHA: ${Repository}@${Tag}."
        }
        $resolvedCommits += $selected.Commit
    }

    if ($resolvedCommits.Count -ne 2 -or $resolvedCommits[0] -cne $resolvedCommits[1]) {
        throw "Action tag changed between repeated lookups: ${Repository}@${Tag}."
    }

    return [ordered]@{
        repository = $Repository
        tag = $Tag
        commit = $resolvedCommits[0]
        retrievalUtc = [DateTimeOffset]::UtcNow.ToString('O')
        command = $command
    }
}

$resolvedInput = (Resolve-Path -LiteralPath $InputPath).Path
$input = Get-Content -LiteralPath $resolvedInput -Raw | ConvertFrom-Json
if ($input.schemaVersion -ne 1 -or $null -eq $input.actions) {
    throw "Unsupported action pin input schema: $resolvedInput"
}

$seen = @{}
$resolved = @()
foreach ($action in @($input.actions)) {
    $repository = [string]$action.repository
    $tag = [string]$action.tag
    if ($repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "Invalid action repository: $repository"
    }
    if ($tag -notmatch '^v[1-9][0-9]*$') {
        throw "Invalid action major tag for ${repository}: $tag"
    }
    $pair = "${repository}@${tag}"
    if ($seen.ContainsKey($pair)) {
        throw "Duplicate action pin input: $pair"
    }
    $seen[$pair] = $true
    $resolved += Resolve-TagCommit -Repository $repository -Tag $tag
}

$candidate = [ordered]@{
    schemaVersion = 1
    actions = $resolved
}
$outputDirectory = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($outputDirectory)) { $outputDirectory = '.' }
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
$resolvedOutput = Join-Path (Resolve-Path -LiteralPath $outputDirectory).Path (Split-Path -Leaf $OutputPath)
Write-Utf8File -Path $resolvedOutput -Content (($candidate | ConvertTo-Json -Depth 6) + "`n")
Write-Output $resolvedOutput
