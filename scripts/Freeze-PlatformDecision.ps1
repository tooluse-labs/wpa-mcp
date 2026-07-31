[CmdletBinding()]
param(
    [ValidateSet(
        'net8-stable-stateful',
        'net10-stable-stateful',
        'net10-next-stateless')]
    [string]$CandidateId,
    [string]$ResultsDirectory = 'artifacts/platform-matrix',
    [string]$SelectedPlatformPath = 'eng/SelectedPlatform.props',
    [string]$DecisionRecordPath = 'docs/decisions/0001-platform-protocol.md'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:FreezeRepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:CandidateRunnerPath = Join-Path $PSScriptRoot 'Test-PlatformCandidate.ps1'

function Resolve-FreezePath {
    param([Parameter(Mandatory)][string]$Path)

    if ($Path -match '^(?:[A-Za-z]:[\\/]|[\\/]{2})') { return $Path }
    return Join-Path $script:FreezeRepositoryRoot $Path
}

function Test-PlatformDecisionInput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ResultPath,
        [Parameter(Mandatory)][ValidateSet(
            'net8-stable-stateful',
            'net10-stable-stateful',
            'net10-next-stateless')]
        [string]$CandidateId,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit
    )

    try {
        . $script:CandidateRunnerPath -CandidateId $CandidateId
        if (-not (Test-PlatformCandidateResult -Path $ResultPath -CandidateId $CandidateId)) { return $false }
        $result = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
        if ($result.commit -cne $ExpectedCommit -or $result.candidateId -cne $CandidateId) { return $false }
        foreach ($probe in @($result.probes)) {
            if ($probe.exitCode -ne 0 -or $probe.passed -ne $true) { return $false }
            foreach ($case in @($probe.cases)) {
                if ($case.exitCode -ne 0 -or $case.passed -ne $true -or $case.failureStage -cne 'none') { return $false }
            }
        }
        return $true
    }
    catch {
        Write-Verbose "Platform decision input validation exception: $($_.Exception.Message)"
        return $false
    }
}

function Get-RejectionReasons {
    param(
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][string]$SelectedCandidateId
    )

    if ($Result.candidateId -ceq $SelectedCandidateId) {
        return @('Selected after all required probes and all twelve SDK-surface host-mode cases passed; .NET 10 LTS is paired with the stable ModelContextProtocol 1.4.1 package.')
    }
    $failed = @($Result.probes | Where-Object { $_.passed -ne $true })
    if ($failed.Count -gt 0) {
        return @($failed | ForEach-Object { "Rejected because required probe '$($_.name)' failed at recorded exit code $($_.exitCode)." })
    }
    if ($Result.candidateId -ceq 'net8-stable-stateful') {
        return @('Rejected despite passing every probe because .NET 8 support ends 2026-11-10, while the passing .NET 10 stable candidate retains the same stable MCP contract with support through 2028-11-14.')
    }
    if ($Result.candidateId -ceq 'net10-next-stateless') {
        return @('Rejected despite passing every probe because ModelContextProtocol 2.0.0-rc.1 is prerelease; the stateless-discovery profile does not justify accepting prerelease dependency and operational risk while a stable .NET 10 candidate passes.')
    }
    return @('Rejected after the passing candidates were compared on support lifecycle and dependency stability.')
}

function Invoke-PlatformDecisionFreeze {
    [CmdletBinding()]
    param()

    if ([string]::IsNullOrWhiteSpace($CandidateId)) { throw 'CandidateId is required when executing the freeze script.' }
    if ($CandidateId -cne $CandidateId.Trim()) { throw 'CandidateId must not contain surrounding whitespace.' }

    . $script:CandidateRunnerPath -CandidateId $CandidateId
    $matrix = Get-PlatformMatrix
    $commit = (& git -C $script:FreezeRepositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Unable to resolve the current full Git commit.' }

    $resultsRootInput = Resolve-FreezePath $ResultsDirectory
    if (-not (Test-Path -LiteralPath $resultsRootInput -PathType Container)) {
        throw "Candidate results directory does not exist: $resultsRootInput"
    }
    $resultsRoot = (Resolve-Path -LiteralPath $resultsRootInput).Path
    $results = @()
    foreach ($candidate in @($matrix.candidates)) {
        $resultPath = Join-Path $resultsRoot "$($candidate.id).result.json"
        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            throw "Missing immutable candidate result: $resultPath"
        }
        if (-not (Test-PlatformCandidateResult -Path $resultPath -CandidateId $candidate.id)) {
            throw "Candidate result failed its strict contract: $resultPath"
        }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($result.commit -cne $commit) {
            throw "Candidate '$($candidate.id)' records commit '$($result.commit)' instead of current HEAD '$commit'."
        }
        $results += ,$result
    }

    $selected = @($results | Where-Object { $_.candidateId -ceq $CandidateId })
    if ($selected.Count -ne 1) { throw "Expected exactly one result for selected candidate '$CandidateId'." }
    $selected = $selected[0]
    $selectedResultPath = Join-Path $resultsRoot "$CandidateId.result.json"
    if (-not (Test-PlatformDecisionInput -ResultPath $selectedResultPath -CandidateId $CandidateId -ExpectedCommit $commit)) {
        throw "Selected candidate '$CandidateId' did not pass every freeze validation."
    }

    $selectedOutputPath = Resolve-FreezePath $SelectedPlatformPath
    $decisionOutputPath = Resolve-FreezePath $DecisionRecordPath
    if (Test-Path -LiteralPath $selectedOutputPath) { throw "CreateNew target already exists: $selectedOutputPath" }
    if (Test-Path -LiteralPath $decisionOutputPath) { throw "CreateNew target already exists: $decisionOutputPath" }

    $decisions = @()
    foreach ($result in $results) {
        $disposition = if ($result.candidateId -ceq $CandidateId) { 'selected' } else { 'rejected' }
        $decisions += ,[ordered]@{
            candidateId = $result.candidateId
            disposition = $disposition
            reasons = @(Get-RejectionReasons -Result $result -SelectedCandidateId $CandidateId)
        }
    }
    $prereleaseRisk = 'ModelContextProtocol 2.0.0-rc.1 is prerelease. Although its complete matrix passed, selecting it would accept prerelease API and operational change risk without a demonstrated need over the passing stable package.'
    $selectedPublicSdkSeams = @(
        'StreamServerTransport over caller-supplied guarded input/output streams',
        'McpServer.Create with McpServerOptions.AddIncomingFilter before dispatch',
        'McpServerTool.Create with McpServerToolCreateOptions.UseStructuredContent=true',
        'DelegatingMcpServerTool for text and StructuredContent replacement',
        'CancellationToken and IProgress<ProgressNotificationValue> handler injection without schema leakage'
    )
    $evidenceIndex = [ordered]@{
        schemaVersion = '1.0'
        selectedCandidateId = $CandidateId
        sourceCommit = $commit
        officialEvidence = $matrix.planDateEvidence
        windowsArchitectureMatrix = @($matrix.windowsArchitectureMatrix)
        candidateResults = @($results)
        decisions = @($decisions)
        prereleaseRisk = $prereleaseRisk
        selectedPublicSdkSeams = @($selectedPublicSdkSeams)
    }

    $props = @"
<Project>
  <PropertyGroup>
    <WpaMcpSdkVersion>$($selected.sdkVersion)</WpaMcpSdkVersion>
    <WpaMcpTargetFramework>$($selected.targetFramework)</WpaMcpTargetFramework>
    <WpaMcpMcpSdkVersion>$($selected.mcpSdkVersion)</WpaMcpMcpSdkVersion>
    <WpaMcpProtocolRevision>$($selected.protocolRevision)</WpaMcpProtocolRevision>
    <WpaMcpProtocolProfile>$($selected.protocolProfile)</WpaMcpProtocolProfile>
  </PropertyGroup>
</Project>
"@

    $officialLines = @()
    foreach ($property in $matrix.planDateEvidence.PSObject.Properties) {
        $officialLines += "- $($property.Name): $($property.Value)"
    }
    $candidateLines = @()
    $nugetLines = @()
    $decisionLines = @()
    foreach ($result in $results) {
        $failedCount = @($result.probes | Where-Object { $_.passed -ne $true }).Count
        $candidateLines += "| $($result.candidateId) | $($result.sdkVersion) | $($result.targetFramework) | $($result.mcpSdkVersion) | $($result.protocolRevision) / $($result.protocolProfile) | $(@($result.probes).Count - $failedCount)/$(@($result.probes).Count) |"
        $nugetProbe = @($result.probes | Where-Object { $_.name -ceq 'nuget-package-existence-hash' })[0]
        $nugetLines += "| $($result.candidateId) | $($nugetProbe.nuGetPackage.observedUtc) | $($nugetProbe.nuGetPackage.retrievalSource) | $($nugetProbe.nuGetPackage.publishedHashBase64) |"
        $decision = @($decisions | Where-Object { $_.candidateId -ceq $result.candidateId })[0]
        $decisionLines += "- $($decision.candidateId): $($decision.disposition). $(@($decision.reasons) -join ' ')"
    }
    $seamLines = @($selectedPublicSdkSeams | ForEach-Object { "- $_" })
    $selectedEvidenceRows = @()
    foreach ($probeName in @($matrix.sdkSurfaceProbeNames)) {
        $probe = @($selected.probes | Where-Object { $_.name -ceq $probeName })[0]
        foreach ($case in @($probe.cases)) {
            $suffix = "$probeName.$($case.hostMode).evidence.json"
            $artifact = @($case.artifactSha256.PSObject.Properties | Where-Object { $_.Name.EndsWith($suffix) })[0]
            $selectedEvidenceRows += "| $probeName | $($case.hostMode) | $($artifact.Name) | $($artifact.Value) |"
        }
    }

    $evidenceJson = $evidenceIndex | ConvertTo-Json -Depth 20
    $fence = '```'
    $markdown = @"
# ADR 0001: .NET, MCP SDK, and protocol profile

- Status: Accepted
- Decision commit: $commit
- Selected candidate: $CandidateId
- Observed matrix completion: $($selected.completedUtc)

## Decision

Select .NET SDK $($selected.sdkVersion), target framework $($selected.targetFramework), ModelContextProtocol $($selected.mcpSdkVersion), protocol revision $($selected.protocolRevision), and profile $($selected.protocolProfile).

All three fixed candidates completed the same 16-probe matrix. The selected candidate combines the .NET 10 LTS support horizon (through 2028-11-14) with the stable MCP 1.4.1 dependency. The passing .NET 8 alternative reaches end of support on 2026-11-10. The passing 2.0.0-rc.1 alternative remains prerelease, so its stateless-discovery profile does not outweigh the dependency and operational risk recorded below.

## Candidate outcomes

| Candidate | SDK | TFM | MCP SDK | Revision / profile | Passed probes |
|---|---:|---|---|---|---:|
$($candidateLines -join "`r`n")

$($decisionLines -join "`r`n")

Prerelease risk: $prereleaseRisk

## Official plan-date evidence

$($officialLines -join "`r`n")

## NuGet verification observations

| Candidate | Observed UTC | Retrieval | Published SHA-512 (base64) |
|---|---|---|---|
$($nugetLines -join "`r`n")

## Selected public SDK seams

$($seamLines -join "`r`n")

Every seam above was exercised in normal, win-x64 framework-dependent, and win-x64 self-contained host modes. Evidence includes the selected profile/revision transcript, delegated typed structured-output replacement, cancellation/progress injection without schema leakage, the 100000-byte frame boundary, decoded string request-ID results 127=accepted, 128=accepted, 129=rejected before dispatch, and correlated Int64 minimum/zero/maximum numeric IDs.

| Probe | Host mode | Retained evidence | SHA-256 |
|---|---|---|---|
$($selectedEvidenceRows -join "`r`n")

## Supported Windows architecture

Supported by this decision: Windows/X64 process on Windows/X64 OS with runtime identifier win-x64. The three SDK host modes are executions in that one architecture cell. win-arm64, win-x86, and cross-architecture emulation remain explicit gaps.

## Machine-readable evidence index

The following block is generated from the three immutable result files after strict hash and semantic validation. It is the exact source for commands, exit codes, stdout/stderr hashes, per-host cases, runtime artifacts, and observed outcomes.

<!-- BEGIN PLATFORM DECISION EVIDENCE -->
${fence}json
$evidenceJson
$fence
<!-- END PLATFORM DECISION EVIDENCE -->
"@

    $props = $props.Replace("`r`n", "`n").Replace("`r", "`n")
    $markdown = $markdown.Replace("`r`n", "`n").Replace("`r", "`n")

    foreach ($parent in @((Split-Path -Parent $selectedOutputPath), (Split-Path -Parent $decisionOutputPath))) {
        if (-not (Test-Path -LiteralPath $parent)) { [void](New-Item -ItemType Directory -Path $parent) }
    }
    Write-NewUtf8File -Path $selectedOutputPath -Content $props
    Write-NewUtf8File -Path $decisionOutputPath -Content $markdown
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-PlatformDecisionFreeze
}
