[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet(
        'net8-stable-stateful',
        'net10-stable-stateful',
        'net10-next-stateless')]
    [string]$CandidateId,
    [string]$OutputDirectory = 'artifacts/platform-matrix'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:MatrixPath = Join-Path $script:RepositoryRoot 'eng\platform-candidates.v1.json'
$script:ResultFields = @(
    'schemaVersion', 'candidateId', 'sdkVersion', 'targetFramework', 'mcpSdkVersion',
    'protocolRevision', 'protocolProfile', 'commit', 'startedUtc', 'completedUtc', 'probes')
$script:ProbeFields = @(
    'name', 'command', 'exitCode', 'stdoutSha256', 'stderrSha256', 'passed',
    'artifactSha256', 'cases', 'nuGetPackage')
$script:CaseFields = @(
    'hostMode', 'scenario', 'command', 'exitCode', 'stdoutSha256', 'stderrSha256',
    'passed', 'failureStage', 'artifactSha256')
$script:NuGetPackageFields = @(
    'packageId', 'packageVersion', 'registrationUrl', 'packageContentUrl',
    'hashAlgorithm', 'publishedHashBase64', 'downloadedHashBase64', 'observedUtc', 'retrievalSource')
$script:RetainedNuGetPackageFields = @(
    'packageId', 'packageVersion', 'registrationUrl', 'catalogUrl', 'packageContentUrl',
    'hashAlgorithm', 'publishedHashBase64', 'downloadedHashBase64', 'observedUtc', 'retrievalSource')
$script:RestorePackageEvidenceFields = @(
    'schemaVersion', 'probeName', 'packageId', 'packageVersion', 'candidateSource', 'configPath',
    'configSha256', 'verifiedPackagePath', 'verifiedPackageSha256', 'restoredPackagePath',
    'restoredPackageSha256', 'restoredSha512Path', 'restoredMetadataPath', 'metadataSource',
    'publishedHashBase64', 'restoreContentHashBase64', 'passed')

function Get-PlatformMatrix {
    Get-Content -LiteralPath $script:MatrixPath -Raw | ConvertFrom-Json
}

function Get-PlatformRunnerContract {
    $matrix = Get-PlatformMatrix
    @{
        requiredProbeNames = @($matrix.requiredProbeNames)
        sdkSurfaceProbeNames = @($matrix.sdkSurfaceProbeNames)
        sdkSurfaceHostModes = @($matrix.sdkSurfaceHostModes)
        windowsArchitectureMatrix = @($matrix.windowsArchitectureMatrix)
        workspaceMode = 'temporary-copied-worktree'
        editsCallerTrackedFiles = $false
        resultFileMode = 'CreateNew'
        resultFields = $script:ResultFields
        probeFields = $script:ProbeFields
        caseFields = $script:CaseFields
    }
}

function Get-CandidateExecutionPlan {
    @(Get-PlatformMatrix).requiredProbeNames
}

function Test-ExactPropertySet {
    param([Parameter(Mandatory)]$Object, [Parameter(Mandatory)][string[]]$Names)

    $actual = @($Object.PSObject.Properties.Name)
    return $actual.Count -eq $Names.Count -and
        @($actual | Where-Object { $_ -notin $Names }).Count -eq 0 -and
        @($Names | Where-Object { $_ -notin $actual }).Count -eq 0
}

function Test-Sha256Text {
    param([object]$Value)
    return $Value -is [string] -and $Value -cmatch '^[0-9a-f]{64}$'
}

function Test-JsonInteger {
    param([object]$Value)
    return $Value -is [int] -or $Value -is [long]
}

function Test-ContainedPathWithoutReparsePoint {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root)

    try {
        $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
        $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
        if ($resolvedPath -cne $resolvedRoot -and
            -not $resolvedPath.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { return $false }
        $current = $resolvedPath
        while ($true) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
            if ($current -ceq $resolvedRoot) { break }
            $parent = Split-Path -Parent $current
            if ($parent -ceq $current -or ($parent -cne $resolvedRoot -and
                -not $parent.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase))) { return $false }
            $current = $parent
        }
        return $true
    }
    catch { return $false }
}

function Test-RetainedArtifactMap {
    param([Parameter(Mandatory)]$Map, [Parameter(Mandatory)][string]$CandidateRoot)

    $rootPrefix = ((Resolve-Path -LiteralPath $CandidateRoot).Path -replace '[\\/]+$', '') + '\'
    foreach ($property in @($Map.PSObject.Properties)) {
        $artifactPath = if ($property.Name -match '^(?:[A-Za-z]:[\\/]|\\\\)' -and
            (Test-Path -LiteralPath $property.Name -PathType Leaf)) { (Resolve-Path -LiteralPath $property.Name).Path } else { '' }
        if (-not $artifactPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-ContainedPathWithoutReparsePoint -Path $artifactPath -Root $CandidateRoot) -or
            -not (Test-Sha256Text $property.Value) -or
            (Get-Sha256 $artifactPath) -cne $property.Value) {
            return $false
        }
    }
    return $true
}

function Test-ArtifactMapEqualsCaseUnion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$ArtifactMap,
        [Parameter(Mandatory)][object[]]$Cases
    )

    $expected = @{}
    foreach ($case in $Cases) {
        foreach ($property in @($case.artifactSha256.PSObject.Properties)) {
            if ($expected.ContainsKey($property.Name) -and $expected[$property.Name] -cne [string]$property.Value) {
                return $false
            }
            $expected[$property.Name] = [string]$property.Value
        }
    }
    $actual = @($ArtifactMap.PSObject.Properties)
    if ($actual.Count -ne $expected.Count) { return $false }
    foreach ($property in $actual) {
        if (-not $expected.ContainsKey($property.Name) -or
            $expected[$property.Name] -cne [string]$property.Value) { return $false }
    }
    return $true
}

function Test-ProductionStdioEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$EvidencePath,
        [Parameter(Mandatory)][string]$RawStdoutPath,
        [Parameter(Mandatory)][string]$RawStderrPath,
        [Parameter(Mandatory)][string]$ServerPath,
        [Parameter(Mandatory)][string]$PublishRoot,
        [Parameter(Mandatory)]$Candidate
    )

    try {
        foreach ($path in @($EvidencePath, $RawStdoutPath, $RawStderrPath, $ServerPath)) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
        }
        $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
        if ($evidence.schemaVersion -cne '1.0' -or $evidence.protocolRevision -cne $Candidate.protocolRevision -or
            $evidence.protocolProfile -cne $Candidate.protocolProfile -or $evidence.passed -ne $true -or
            -not (Test-JsonInteger $evidence.correlatedResponseCount) -or $evidence.correlatedResponseCount -ne 3) { return $false }
        $expectedTranscript = if ($Candidate.protocolProfile -ceq 'stateful') {
            @('initialize', 'notifications/initialized', 'tools/list', 'tools/call')
        } else { @('server/discover', 'tools/list', 'tools/call') }
        if ((@($evidence.orderedMessageMethodTranscript) -join "`n") -cne ($expectedTranscript -join "`n")) { return $false }
        if ($Candidate.protocolProfile -ceq 'stateful') {
            if (@($evidence.serializedMetadataKeysByMethod.'tools/list').Count -ne 0 -or
                @($evidence.serializedMetadataKeysByMethod.'tools/call').Count -ne 0) { return $false }
        }
        else {
            $discoverKeys = @($evidence.serializedMetadataKeysByMethod.'server/discover')
            $expectedMeta = @(
                'io.modelcontextprotocol/protocolVersion',
                'io.modelcontextprotocol/clientInfo',
                'io.modelcontextprotocol/clientCapabilities')
            if (($discoverKeys -join "`n") -cne ($expectedMeta -join "`n") -or
                ($discoverKeys -join "`n") -cne (@($evidence.serializedMetadataKeysByMethod.'tools/list') -join "`n") -or
                ($discoverKeys -join "`n") -cne (@($evidence.serializedMetadataKeysByMethod.'tools/call') -join "`n")) { return $false }
        }
        if (-not (Test-JsonInteger $evidence.observedOutcomes.listedToolCount) -or
            $evidence.observedOutcomes.listedToolCount -le 0 -or
            $evidence.observedOutcomes.unknownCallTerminalError -ne $true) { return $false }
        $launch = $evidence.launch
        $resolvedServer = (Resolve-Path -LiteralPath $ServerPath).Path
        $resolvedPublish = (Resolve-Path -LiteralPath $PublishRoot).Path
        $depsPath = Join-Path $resolvedPublish 'WpaMcp.deps.json'
        if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) { return $false }
        $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
        if ([string]$deps.runtimeTarget.name -cnotmatch '/win-x64$') { return $false }
        $publishLeafNames = @(Get-ChildItem -LiteralPath $resolvedPublish -Recurse -File | ForEach-Object { $_.Name })
        foreach ($runtimeBinary in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
            if ($runtimeBinary -cnotin $publishLeafNames) { return $false }
        }
        $actualHash = Get-Sha256 $resolvedServer
        if ($launch.path -cne $resolvedServer -or $launch.publishRoot -cne $resolvedPublish -or
            $launch.relativePath -cne 'WpaMcp.exe' -or $launch.expectedLaunchSha256 -cne $actualHash -or
            $launch.sha256Before -cne $actualHash -or $launch.sha256After -cne $actualHash -or
            -not (Test-JsonInteger $launch.processId) -or $launch.processId -le 0 -or
            $launch.childProcessArchitecture -cne 'X64' -or $launch.observerOsArchitecture -cne 'X64' -or
            $launch.requestedRuntimeIdentifier -cne 'win-x64' -or
            $launch.publishRuntimeIdentifier -cne 'win-x64') { return $false }
        $responses = @(Get-Content -LiteralPath $RawStdoutPath | Where-Object { $_.Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json })
        if ($responses.Count -ne 3) { return $false }
        $expectedIds = if ($Candidate.protocolProfile -ceq 'stateful') { @('initialize', 'list', 'unknown-call') } else { @('discover', 'list', 'unknown-call') }
        if ((@($responses.id) -join "`n") -cne ($expectedIds -join "`n")) { return $false }
        return $true
    }
    catch { return $false }
}

function Test-SdkSurfaceEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CandidateRoot,
        [Parameter(Mandatory)]$Candidate,
        [Parameter(Mandatory)]$Probe,
        [Parameter(Mandatory)]$Case
    )

    try {
        $publishLeaf = switch ($Case.hostMode) {
            'normal' { 'sdk-probe-normal' }
            'win-x64-framework-dependent' { 'sdk-probe-framework-dependent' }
            'win-x64-self-contained' { 'sdk-probe-self-contained' }
            default { return $false }
        }
        $publishRoot = (Resolve-Path -LiteralPath (Join-Path $CandidateRoot "publishes\$publishLeaf")).Path
        $hostPath = (Resolve-Path -LiteralPath (Join-Path $publishRoot 'sdkcandidateprobe.exe')).Path
        $manifestPath = (Resolve-Path -LiteralPath (Join-Path $CandidateRoot "publish-manifests\$($Case.hostMode).json")).Path
        $evidencePath = (Resolve-Path -LiteralPath (Join-Path $CandidateRoot "$($Probe.name).$($Case.hostMode).evidence.json")).Path
        $artifactNames = @($Case.artifactSha256.PSObject.Properties.Name)
        foreach ($requiredArtifact in @($hostPath, $manifestPath, $evidencePath)) {
            if ($requiredArtifact -cnotin $artifactNames) { return $false }
        }

        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.schemaVersion -cne '1.0' -or $manifest.hostMode -cne $Case.hostMode -or
            $manifest.publishRoot -cne $publishRoot -or $manifest.publishExitCode -ne 0) { return $false }
        if ($Case.hostMode -eq 'win-x64-self-contained') {
            if ($null -ne $manifest.frameworkRuntime) { return $false }
        }
        else {
            $frameworkRuntime = $manifest.frameworkRuntime
            $expectedRetainedHostFxr = (Resolve-Path -LiteralPath (Join-Path $CandidateRoot "framework-runtime\$($Case.hostMode)\hostfxr.dll")).Path
            $expectedRetainedHostPolicy = (Resolve-Path -LiteralPath (Join-Path $CandidateRoot "framework-runtime\$($Case.hostMode)\hostpolicy.dll")).Path
            $expectedDotNetRoot = (Resolve-Path -LiteralPath (Split-Path -Parent (Get-ExactDotNet $Candidate.sdkVersion))).Path
            $dotnetRootPrefix = ($expectedDotNetRoot -replace '[\/]+$', '') + '\'
            if (-not (Test-Sha256Text $frameworkRuntime.sourceHostFxrSha256) -or
                $frameworkRuntime.dotnetRoot -cne $expectedDotNetRoot -or
                -not $frameworkRuntime.sourceHostFxrPath.StartsWith($dotnetRootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                $frameworkRuntime.sourceHostFxrSha256 -cne $frameworkRuntime.retainedHostFxrSha256 -or
                (Split-Path -Leaf $frameworkRuntime.sourceHostFxrPath) -cne 'hostfxr.dll' -or
                $frameworkRuntime.retainedHostFxrPath -cne $expectedRetainedHostFxr -or
                -not (Test-ContainedPathWithoutReparsePoint -Path $expectedRetainedHostFxr -Root $CandidateRoot) -or
                (Get-Sha256 $expectedRetainedHostFxr) -cne $frameworkRuntime.retainedHostFxrSha256 -or
                $expectedRetainedHostFxr -cnotin @($Case.artifactSha256.PSObject.Properties.Name) -or
                -not (Test-Sha256Text $frameworkRuntime.sourceHostPolicySha256) -or
                -not $frameworkRuntime.sourceHostPolicyPath.StartsWith($dotnetRootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                $frameworkRuntime.sourceHostPolicySha256 -cne $frameworkRuntime.retainedHostPolicySha256 -or
                (Split-Path -Leaf $frameworkRuntime.sourceHostPolicyPath) -cne 'hostpolicy.dll' -or
                $frameworkRuntime.retainedHostPolicyPath -cne $expectedRetainedHostPolicy -or
                -not (Test-ContainedPathWithoutReparsePoint -Path $expectedRetainedHostPolicy -Root $CandidateRoot) -or
                (Get-Sha256 $expectedRetainedHostPolicy) -cne $frameworkRuntime.retainedHostPolicySha256 -or
                $expectedRetainedHostPolicy -cnotin @($Case.artifactSha256.PSObject.Properties.Name)) { return $false }
        }
        $manifestFiles = @($manifest.files)
        if ($manifestFiles.Count -lt 4 -or @($manifestFiles.relativePath | Select-Object -Unique).Count -ne $manifestFiles.Count) { return $false }
        foreach ($requiredName in @('sdkcandidateprobe.exe', 'sdkcandidateprobe.dll', 'sdkcandidateprobe.deps.json', 'sdkcandidateprobe.runtimeconfig.json')) {
            if ($requiredName -cnotin @($manifestFiles.relativePath)) { return $false }
        }
        foreach ($file in $manifestFiles) {
            if ($file.relativePath -isnot [string] -or $file.relativePath.Length -eq 0 -or
                $file.relativePath -match '^(?:[A-Za-z]:|[\\/])' -or $file.relativePath -match '[\\:]' -or
                @($file.relativePath -split '/' | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -ne 0 -or
                -not (Test-Sha256Text $file.sha256)) { return $false }
            $retainedPath = Join-Path $publishRoot ($file.relativePath -replace '/', '\')
            if (-not (Test-Path -LiteralPath $retainedPath -PathType Leaf)) { return $false }
            $resolvedRetainedPath = (Resolve-Path -LiteralPath $retainedPath).Path
            $publishPrefix = ($publishRoot -replace '[\\/]+$', '') + '\'
            if (-not $resolvedRetainedPath.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-ContainedPathWithoutReparsePoint -Path $resolvedRetainedPath -Root $publishRoot) -or
                $resolvedRetainedPath.Substring($publishPrefix.Length).Replace('\', '/') -cne $file.relativePath -or
                (Get-Sha256 $resolvedRetainedPath) -cne $file.sha256) { return $false }
        }
        $currentRelativePaths = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | ForEach-Object {
            $_.FullName.Substring((($publishRoot -replace '[\\/]+$', '') + '\').Length).Replace('\', '/')
        } | Sort-Object)
        $manifestRelativePaths = @($manifestFiles.relativePath | Sort-Object)
        if ($currentRelativePaths.Count -ne $manifestRelativePaths.Count -or
            ($currentRelativePaths -join "`n") -cne ($manifestRelativePaths -join "`n")) { return $false }

        $deps = Get-Content -LiteralPath (Join-Path $publishRoot 'sdkcandidateprobe.deps.json') -Raw | ConvertFrom-Json
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishRoot 'sdkcandidateprobe.runtimeconfig.json') -Raw | ConvertFrom-Json
        $runtimeTargetName = [string]$deps.runtimeTarget.name
        $runtimeOptions = $runtimeConfig.runtimeOptions
        $frameworkNames = @()
        $frameworkProperty = $runtimeOptions.PSObject.Properties['framework']
        if ($null -ne $frameworkProperty) { $frameworkNames += [string]$frameworkProperty.Value.name }
        $frameworksProperty = $runtimeOptions.PSObject.Properties['frameworks']
        if ($null -ne $frameworksProperty) { $frameworkNames += @($frameworksProperty.Value | ForEach-Object { [string]$_.name }) }
        $includedProperty = $runtimeOptions.PSObject.Properties['includedFrameworks']
        $includedFrameworkNames = @(if ($null -ne $includedProperty) { $includedProperty.Value | ForEach-Object { [string]$_.name } })
        $runtimeBinaries = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')
        $presentRuntimeBinaries = @($runtimeBinaries | Where-Object { $_ -cin @($manifestFiles.relativePath) })
        switch ($Case.hostMode) {
            'normal' {
                if ($runtimeTargetName -match '/win-x64$' -or 'Microsoft.NETCore.App' -cnotin $frameworkNames -or
                    $includedFrameworkNames.Count -ne 0 -or $presentRuntimeBinaries.Count -ne 0) { return $false }
            }
            'win-x64-framework-dependent' {
                if ($runtimeTargetName -cnotmatch '/win-x64$' -or 'Microsoft.NETCore.App' -cnotin $frameworkNames -or
                    $includedFrameworkNames.Count -ne 0 -or $presentRuntimeBinaries.Count -ne 0) { return $false }
            }
            'win-x64-self-contained' {
                if ($runtimeTargetName -cnotmatch '/win-x64$' -or $frameworkNames.Count -ne 0 -or
                    'Microsoft.NETCore.App' -cnotin $includedFrameworkNames -or $presentRuntimeBinaries.Count -ne $runtimeBinaries.Count) { return $false }
            }
        }

        $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        if ($evidence.schemaVersion -cne '1.0' -or $evidence.hostMode -cne $Case.hostMode -or
            $evidence.protocolRevision -cne $Candidate.protocolRevision -or
            $evidence.protocolProfile -cne $Candidate.protocolProfile -or
            $evidence.offeredRevision -cne $Candidate.protocolRevision -or
            $evidence.acceptedRevision -cne $Candidate.protocolRevision -or $evidence.passed -ne $true) { return $false }
        $expectedTranscript = if ($Candidate.protocolProfile -ceq 'stateful') {
            @('initialize', 'notifications/initialized', 'tools/list', 'tools/call')
        } else { @('server/discover', 'tools/list', 'tools/call') }
        if ((@($evidence.orderedMessageMethodTranscript) -join "`n") -cne ($expectedTranscript -join "`n")) { return $false }
        $meta = $evidence.serializedMetadataKeysByMethod
        if ($Candidate.protocolProfile -ceq 'stateful') {
            if (@($meta.initialize).Count -ne 0 -or @($meta.'tools/list').Count -ne 0 -or
                (@($meta.'tools/call') -join "`n") -cne 'progressToken') { return $false }
        }
        else {
            $profileMeta = @(
                'io.modelcontextprotocol/protocolVersion',
                'io.modelcontextprotocol/clientInfo',
                'io.modelcontextprotocol/clientCapabilities')
            if ((@($meta.'server/discover') -join "`n") -cne ($profileMeta -join "`n") -or
                (@($meta.'tools/list') -join "`n") -cne ($profileMeta -join "`n") -or
                (@($meta.'tools/call') -join "`n") -cne (@($profileMeta + 'progressToken') -join "`n")) { return $false }
        }
        if ((@($evidence.InputSchemaPropertyNames) -join "`n") -cne 'value') { return $false }
        $launch = $evidence.launchIdentity
        $runtime = $launch.runtimeIdentity
        $hostHash = Get-Sha256 $hostPath
        if ($launch.passed -ne $true -or $launch.retainedLaunchPath -cne $hostPath -or
            $launch.preLaunchSha256 -cne $hostHash -or $launch.postLaunchSha256 -cne $hostHash -or
            -not (Test-JsonInteger $launch.childProcessId) -or $launch.childProcessId -le 0 -or
            $launch.childProcessPath -cne $hostPath -or $launch.childProcessSha256 -cne $hostHash -or
            $launch.childProcessArchitecture -cne 'X64' -or $runtime.processId -ne $launch.childProcessId -or
            $runtime.processPath -cne $hostPath -or $runtime.processPathSha256 -cne $hostHash -or
            $runtime.osPlatform -cne 'Windows' -or $runtime.osArchitecture -cne 'X64' -or
            $runtime.processArchitecture -cne 'X64' -or $runtime.runtimeIdentifier -cne 'win-x64' -or
            $runtime.is64BitOperatingSystem -ne $true -or $runtime.is64BitProcess -ne $true) { return $false }
        $entryPath = (Resolve-Path -LiteralPath $runtime.entryAssemblyPath).Path
        $expectedEntryPath = (Resolve-Path -LiteralPath (Join-Path $publishRoot 'sdkcandidateprobe.dll')).Path
        if ($entryPath -cne $expectedEntryPath -or 'sdkcandidateprobe.dll' -cnotin @($manifestFiles.relativePath) -or
            $runtime.entryAssemblySha256 -cne (Get-Sha256 $entryPath)) { return $false }
        if ($Case.hostMode -eq 'win-x64-self-contained') {
            $expectedLoadedHostFxr = (Resolve-Path -LiteralPath (Join-Path $publishRoot 'hostfxr.dll')).Path
            $expectedLoadedHostPolicy = (Resolve-Path -LiteralPath (Join-Path $publishRoot 'hostpolicy.dll')).Path
            $manifestHostFxr = @($manifestFiles | Where-Object relativePath -CEQ 'hostfxr.dll')
            $manifestHostPolicy = @($manifestFiles | Where-Object relativePath -CEQ 'hostpolicy.dll')
            if ($manifestHostFxr.Count -ne 1 -or $manifestHostPolicy.Count -ne 1 -or
                $runtime.loadedHostFxrPath -cne $expectedLoadedHostFxr -or
                $runtime.loadedHostFxrSha256 -cne $manifestHostFxr[0].sha256 -or
                $runtime.loadedHostFxrSha256 -cne (Get-Sha256 $expectedLoadedHostFxr) -or
                $runtime.loadedHostPolicyPath -cne $expectedLoadedHostPolicy -or
                $runtime.loadedHostPolicySha256 -cne $manifestHostPolicy[0].sha256 -or
                $runtime.loadedHostPolicySha256 -cne (Get-Sha256 $expectedLoadedHostPolicy)) { return $false }
        }
        else {
            if ($launch.configuredDotNetRoot -cne $frameworkRuntime.dotnetRoot -or
                $launch.configuredDotNetRootX64 -cne $frameworkRuntime.dotnetRoot -or
                $runtime.loadedHostFxrPath -cne $frameworkRuntime.sourceHostFxrPath -or
                $runtime.loadedHostFxrSha256 -cne $frameworkRuntime.sourceHostFxrSha256 -or
                $runtime.loadedHostFxrSha256 -cne $frameworkRuntime.retainedHostFxrSha256 -or
                $runtime.loadedHostPolicyPath -cne $frameworkRuntime.sourceHostPolicyPath -or
                $runtime.loadedHostPolicySha256 -cne $frameworkRuntime.sourceHostPolicySha256 -or
                $runtime.loadedHostPolicySha256 -cne $frameworkRuntime.retainedHostPolicySha256) { return $false }
        }
        if ($evidence.structuredOutput.textReplaced -ne $true -or
            $evidence.structuredOutput.structuredContentReplaced -ne $true -or
            $evidence.structuredOutput.innerTextObserved -ne $true -or
            $evidence.structuredOutput.innerStructuredObserved -ne $true -or
            $evidence.structuredOutput.inputSchemaPresent -ne $true -or
            $evidence.structuredOutput.outputSchemaPresent -ne $true -or
            $evidence.structuredOutput.annotationsPresent -ne $true -or
            $evidence.structuredOutput.isError -eq $true -or
            $evidence.structuredOutput.preservedIsError -eq $true -or
            $evidence.structuredOutput.isError -ne $evidence.structuredOutput.preservedIsError -or
            $evidence.cancellationProgress.normalProgressNotificationCount -ne 1 -or
            $evidence.cancellationProgress.cancellationProgressNotificationCount -ne 1 -or
            $evidence.cancellationProgress.totalProgressNotificationCount -ne 2 -or
            $evidence.cancellationProgress.cancellationObserved -ne $true -or
            $evidence.cancellationProgress.handlerCancellationObservationCount -ne 1 -or
            $evidence.cancellationProgress.injectedParametersAbsentFromSchema -ne $true -or
            @($evidence.framingAndRequestIds.acceptedIdCases).Count -ne 7 -or
            @($evidence.framingAndRequestIds.acceptedIdCases | Where-Object { $_ -ne $true }).Count -ne 0 -or
            $evidence.framingAndRequestIds.productionFrameLimit -ne 100000 -or
            $evidence.framingAndRequestIds.decodedRequestIdLimit -ne 128 -or
            $evidence.framingAndRequestIds.ascii127Bytes -ne 127 -or
            $evidence.framingAndRequestIds.ascii128Bytes -ne 128 -or
            $evidence.framingAndRequestIds.directUtf8Bytes -ne 2 -or
            $evidence.framingAndRequestIds.escapedUtf8Bytes -ne 2 -or
            (@($evidence.framingAndRequestIds.numericIds) -join ',') -cne '-9223372036854775808,0,9223372036854775807' -or
            $evidence.framingAndRequestIds.exactProductionFrameAccepted -ne $true -or
            $evidence.framingAndRequestIds.oversizedIdRejectedBeforeDispatch -ne $true -or
            $evidence.framingAndRequestIds.oversizedFrameRejectedBeforeDeserialization -ne $true -or
            $evidence.framingAndRequestIds.oversizedFrameObservation.ExitCode -ne 2 -or
            $evidence.framingAndRequestIds.oversizedFrameObservation.Stdout -cne '' -or
            $evidence.framingAndRequestIds.oversizedFrameObservation.Stderr -cne 'sdkcandidateprobe: frame limit exceeded' -or
            $evidence.framingAndRequestIds.oversizedFrameObservation.IncomingNextCount -ne 0 -or
            $evidence.framingAndRequestIds.oversizedFrameObservation.HandlerInvocationCount -ne 0 -or
            $evidence.framingAndRequestIds.loweredCapIsolatedCrRejected -ne $true -or
            $evidence.framingAndRequestIds.bomAtStartRejected -ne $true -or
            $evidence.framingAndRequestIds.bomAnywhereRejected -ne $true -or
            $evidence.framingAndRequestIds.lfAndCrLfAccepted -ne $true -or
            -not (Test-JsonInteger $evidence.InvocationCount) -or $evidence.InvocationCount -le 0) { return $false }
        return $true
    }
    catch { return $false }
}

function Test-GoldenTraceEventEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$EvidencePath)

    try {
        if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) { return $false }
        $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
        if ($evidence.schemaVersion -cne '1.0' -or $evidence.probeName -cne 'golden-traceevent-reads' -or
            $evidence.processArchitecture -cne 'X64' -or $evidence.passed -ne $true) { return $false }
        $expectedNames = @(
            'perfview_gcevents.etl',
            'small_cpu.etl',
            'small_fileio.etl',
            'small_memory.etl',
            'small_mmap.etl',
            'small_wait_bound.etl')
        $fixtures = @($evidence.fixtures)
        if ($fixtures.Count -ne $expectedNames.Count -or
            (@($fixtures.name) -join "`n") -cne ($expectedNames -join "`n")) { return $false }
        foreach ($fixture in $fixtures) {
            if (-not (Test-Sha256Text $fixture.sourceSha256) -or
                -not (Test-Sha256Text $fixture.copySha256) -or
                $fixture.sourceSha256 -cne $fixture.copySha256 -or
                -not (Test-JsonInteger $fixture.eventCount) -or $fixture.eventCount -le 0 -or
                -not (Test-JsonInteger $fixture.durationTicks) -or $fixture.durationTicks -le 0 -or
                $fixture.temporaryCopyUsed -ne $true) { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Test-NativeLayoutEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$EvidencePath,
        [Parameter(Mandatory)][string]$PublishRoot
    )

    try {
        if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) { return $false }
        $resolvedRoot = (Resolve-Path -LiteralPath $PublishRoot).Path
        $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
        $expectedServer = Join-Path $resolvedRoot 'WpaMcp.exe'
        if ($evidence.schemaVersion -cne '1.0' -or $evidence.probeName -cne 'native-layout' -or
            $evidence.processArchitecture -cne 'X64' -or $evidence.publishRoot -cne $resolvedRoot -or
            $evidence.serverPath -cne $expectedServer -or -not (Test-Sha256Text $evidence.serverSha256) -or
            (Get-Sha256 $expectedServer) -cne $evidence.serverSha256 -or $evidence.passed -ne $true) { return $false }
        $expectedRelativePaths = @('amd64/msdia140.dll', 'amd64/KernelTraceControl.dll')
        $dependencies = @($evidence.dependencies)
        if ($dependencies.Count -ne 2 -or
            (@($dependencies.relativePath) -join "`n") -cne ($expectedRelativePaths -join "`n")) { return $false }
        foreach ($dependency in $dependencies) {
            $expectedPath = Join-Path $resolvedRoot ($dependency.relativePath.Replace('/', '\'))
            if ($dependency.path -cne $expectedPath -or $dependency.loaded -ne $true -or
                -not (Test-Sha256Text $dependency.sha256) -or
                (Get-Sha256 $expectedPath) -cne $dependency.sha256) { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Test-WindowsDiaEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$EvidencePath,
        [Parameter(Mandatory)][string]$CandidateRoot,
        [Parameter(Mandatory)][string]$MsdiaPath
    )

    try {
        if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) { return $false }
        $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
        $expectedDiaRoot = Join-Path $CandidateRoot 'dia-probe'
        $expectedImage = Join-Path $expectedDiaRoot 'platform-dia-probe.dll'
        $expectedPdb = Join-Path $expectedDiaRoot 'platform-dia-probe.pdb'
        if ($evidence.schemaVersion -cne '1.0' -or $evidence.probeName -cne 'windows-dia-pdb-resolution' -or
            $evidence.processArchitecture -cne 'X64' -or $evidence.msdiaPath -cne $MsdiaPath -or
            $evidence.nativeImagePath -cne $expectedImage -or $evidence.nativePdbPath -cne $expectedPdb -or
            -not (Test-Sha256Text $evidence.msdiaSha256) -or (Get-Sha256 $MsdiaPath) -cne $evidence.msdiaSha256 -or
            -not (Test-Sha256Text $evidence.nativeImageSha256) -or (Get-Sha256 $expectedImage) -cne $evidence.nativeImageSha256 -or
            -not (Test-Sha256Text $evidence.nativePdbSha256) -or (Get-Sha256 $expectedPdb) -cne $evidence.nativePdbSha256 -or
            -not (Test-JsonInteger $evidence.functionCount) -or $evidence.functionCount -le 0 -or
            -not (Test-JsonInteger $evidence.symbolRva) -or $evidence.symbolRva -le 0 -or
            -not (Test-JsonInteger $evidence.resolvedStartRva) -or $evidence.resolvedStartRva -ne $evidence.symbolRva -or
            [string]$evidence.enumeratedName -cnotmatch 'PlatformDiaSentinel' -or
            [string]$evidence.resolvedName -cnotmatch 'PlatformDiaSentinel' -or $evidence.passed -ne $true) { return $false }
        return $true
    }
    catch { return $false }
}

function Test-WindowsArchitectureEvidence {
    [CmdletBinding()]
    param(
        [string]$EvidencePath,
        $Evidence,
        [Parameter(Mandatory)][string]$CandidateRoot,
        [Parameter(Mandatory)]$Matrix
    )

    try {
        if ($null -ne $Evidence) {
            if ($EvidencePath) { return $false }
            $evidence = $Evidence
        }
        else {
            if (-not $EvidencePath -or -not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) { return $false }
            $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
        }
        if ($evidence.schemaVersion -cne '1.0' -or
            $evidence.probeName -cne 'windows-architecture-matrix' -or
            $evidence.passed -ne $true) { return $false }

        $expected = @($evidence.expected)
        $declared = @($Matrix.windowsArchitectureMatrix)
        if ($expected.Count -ne $declared.Count) { return $false }
        for ($index = 0; $index -lt $declared.Count; $index++) {
            foreach ($property in @('id', 'osPlatform', 'osArchitecture', 'processArchitecture', 'runtimeIdentifier')) {
                if ($expected[$index].$property -cne $declared[$index].$property) { return $false }
            }
        }

        if ($evidence.runner.osPlatform -cne 'Windows' -or
            $evidence.runner.osArchitecture -cne 'X64' -or
            $evidence.runner.processArchitecture -cne 'X64') { return $false }

        $expectedSources = @(
            [ordered]@{ Component = 'golden-traceevent-reads'; EvidencePath = (Join-Path $CandidateRoot 'golden-traceevent-reads.evidence.json'); Kind = 'direct' }
            [ordered]@{ Component = 'windows-dia-pdb-resolution'; EvidencePath = (Join-Path $CandidateRoot 'windows-dia-pdb-resolution.evidence.json'); Kind = 'direct' }
            [ordered]@{ Component = 'native-layout'; EvidencePath = (Join-Path $CandidateRoot 'native-layout.evidence.json'); Kind = 'direct' }
            [ordered]@{ Component = 'self-contained-stdio'; EvidencePath = (Join-Path $CandidateRoot 'self-contained-stdio.evidence.json'); Kind = 'production-stdio' }
            [ordered]@{ Component = 'sdk-normal'; EvidencePath = (Join-Path $CandidateRoot 'selected-profile-handshake.normal.evidence.json'); Kind = 'sdk' }
            [ordered]@{ Component = 'sdk-win-x64-framework-dependent'; EvidencePath = (Join-Path $CandidateRoot 'selected-profile-handshake.win-x64-framework-dependent.evidence.json'); Kind = 'sdk' }
            [ordered]@{ Component = 'sdk-win-x64-self-contained'; EvidencePath = (Join-Path $CandidateRoot 'selected-profile-handshake.win-x64-self-contained.evidence.json'); Kind = 'sdk' }
        )
        $observations = @($evidence.observations)
        if ($observations.Count -ne $expectedSources.Count) { return $false }
        for ($index = 0; $index -lt $expectedSources.Count; $index++) {
            $expectedSource = $expectedSources[$index]
            $observation = $observations[$index]
            if ($observation.component -cne $expectedSource.Component -or
                $observation.evidencePath -cne $expectedSource.EvidencePath -or
                $observation.processArchitecture -cne 'X64' -or
                -not (Test-Sha256Text $observation.evidenceSha256) -or
                -not (Test-Path -LiteralPath $expectedSource.EvidencePath -PathType Leaf) -or
                (Get-Sha256 $expectedSource.EvidencePath) -cne $observation.evidenceSha256) { return $false }

            $source = Get-Content -LiteralPath $expectedSource.EvidencePath -Raw | ConvertFrom-Json
            $sourceArchitecture = switch ($expectedSource.Kind) {
                'direct' { [string]$source.processArchitecture }
                'production-stdio' {
                    if ($source.launch.observerOsArchitecture -cne 'X64' -or
                        $source.launch.requestedRuntimeIdentifier -cne 'win-x64' -or
                        $source.launch.publishRuntimeIdentifier -cne 'win-x64') { return $false }
                    [string]$source.launch.childProcessArchitecture
                }
                'sdk' {
                    $runtime = $source.launchIdentity.runtimeIdentity
                    if ($source.launchIdentity.passed -ne $true -or $runtime.osPlatform -cne 'Windows' -or
                        $runtime.osArchitecture -cne 'X64' -or $runtime.runtimeIdentifier -cne 'win-x64') { return $false }
                    [string]$runtime.processArchitecture
                }
            }
            if ($sourceArchitecture -cne $observation.processArchitecture) { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Test-NuGetPackageResultEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CandidateRoot,
        [Parameter(Mandatory)]$Candidate,
        [Parameter(Mandatory)]$Probe,
        [Parameter(Mandatory)]$Case
    )

    try {
        $verification = $Probe.nuGetPackage
        if ($null -eq $verification -or -not (Test-ExactPropertySet $verification $script:NuGetPackageFields) -or
            -not (Test-ArtifactMapEqualsCaseUnion -ArtifactMap $Probe.artifactSha256 -Cases @($Case))) { return $false }
        if ($verification.packageId -cne 'ModelContextProtocol' -or
            $verification.packageVersion -cne $Candidate.mcpSdkVersion -or
            $verification.hashAlgorithm -cne 'SHA512' -or
            $verification.publishedHashBase64 -isnot [string] -or
            [string]::IsNullOrWhiteSpace($verification.publishedHashBase64) -or
            $verification.downloadedHashBase64 -cne $verification.publishedHashBase64 -or
            $verification.observedUtc -isnot [string] -or
            $verification.retrievalSource -cnotin @('Network', 'VerifiedCache')) { return $false }
        try { [void](Get-Date -Date $verification.observedUtc) }
        catch { return $false }

        $nugetRoot = Join-Path $CandidateRoot 'nuget-evidence'
        $prefix = "modelcontextprotocol.$($Candidate.mcpSdkVersion)"
        $packagePath = Join-Path $nugetRoot "$prefix.nupkg"
        $registrationPath = Join-Path $nugetRoot "$prefix.registration.json"
        $catalogPath = Join-Path $nugetRoot "$prefix.catalog.json"
        $metadataPath = Join-Path $nugetRoot "$prefix.verification.json"
        $artifactNames = @($Probe.artifactSha256.PSObject.Properties.Name)
        foreach ($path in @($packagePath, $registrationPath, $catalogPath, $metadataPath)) {
            if ($path -cnotin $artifactNames) { return $false }
        }

        $catalogEvidence = Test-NuGetCatalogEvidence -RegistrationPath $registrationPath -CatalogPath $catalogPath `
            -PackagePath $packagePath -ExpectedVersion $Candidate.mcpSdkVersion
        foreach ($name in @('packageId', 'packageVersion', 'registrationUrl', 'packageContentUrl',
            'hashAlgorithm', 'publishedHashBase64', 'downloadedHashBase64')) {
            if ($verification.$name -cne $catalogEvidence[$name]) { return $false }
        }
        $retained = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        if (-not (Test-ExactPropertySet $retained $script:RetainedNuGetPackageFields)) { return $false }
        foreach ($name in @('packageId', 'packageVersion', 'registrationUrl', 'catalogUrl', 'packageContentUrl',
            'hashAlgorithm', 'publishedHashBase64', 'downloadedHashBase64')) {
            if ($retained.$name -cne $catalogEvidence[$name]) { return $false }
        }
        foreach ($name in $script:NuGetPackageFields) {
            if ($verification.$name -cne $retained.$name) { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Test-RestorePackageResultEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CandidateRoot,
        [Parameter(Mandatory)]$Candidate,
        [Parameter(Mandatory)]$Probe,
        [Parameter(Mandatory)]$Case,
        [Parameter(Mandatory)]$NuGetVerification
    )

    try {
        if (-not (Test-ArtifactMapEqualsCaseUnion -ArtifactMap $Probe.artifactSha256 -Cases @($Case))) { return $false }
        $candidateSource = Join-Path $CandidateRoot 'nuget-evidence'
        $configPath = Join-Path $CandidateRoot 'NuGet.Config'
        $verifiedPackagePath = Join-Path $candidateSource "modelcontextprotocol.$($Candidate.mcpSdkVersion).nupkg"
        $installedRoot = Join-Path $CandidateRoot "packages\modelcontextprotocol\$($Candidate.mcpSdkVersion)"
        $restoredPackagePath = Join-Path $installedRoot "modelcontextprotocol.$($Candidate.mcpSdkVersion).nupkg"
        $restoredSha512Path = Join-Path $installedRoot "modelcontextprotocol.$($Candidate.mcpSdkVersion).nupkg.sha512"
        $restoredMetadataPath = Join-Path $installedRoot '.nupkg.metadata'
        $evidencePath = Join-Path $CandidateRoot "$($Probe.name).package.evidence.json"
        $artifactNames = @($Probe.artifactSha256.PSObject.Properties.Name)
        foreach ($path in @($candidateSource, $configPath, $verifiedPackagePath, $restoredPackagePath,
            $restoredSha512Path, $restoredMetadataPath, $evidencePath)) {
            if ($path -ceq $candidateSource) {
                if (-not (Test-Path -LiteralPath $path -PathType Container)) { return $false }
            }
            elseif ($path -cnotin $artifactNames) { return $false }
        }

        $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        if (-not (Test-ExactPropertySet $evidence $script:RestorePackageEvidenceFields) -or
            $evidence.schemaVersion -cne '1.0' -or $evidence.probeName -cne $Probe.name -or
            $evidence.packageId -cne 'ModelContextProtocol' -or
            $evidence.packageVersion -cne $Candidate.mcpSdkVersion -or
            $evidence.candidateSource -cne $candidateSource -or $evidence.metadataSource -cne $candidateSource -or
            $evidence.configPath -cne $configPath -or $evidence.verifiedPackagePath -cne $verifiedPackagePath -or
            $evidence.restoredPackagePath -cne $restoredPackagePath -or
            $evidence.restoredSha512Path -cne $restoredSha512Path -or
            $evidence.restoredMetadataPath -cne $restoredMetadataPath -or
            $evidence.publishedHashBase64 -cne $NuGetVerification.publishedHashBase64 -or
            $evidence.passed -ne $true) { return $false }
        if ($evidence.configSha256 -cne (Get-Sha256 $configPath) -or
            $evidence.verifiedPackageSha256 -cne (Get-Sha256 $verifiedPackagePath) -or
            $evidence.restoredPackageSha256 -cne (Get-Sha256 $restoredPackagePath) -or
            $evidence.verifiedPackageSha256 -cne $evidence.restoredPackageSha256 -or
            (Get-Content -LiteralPath $restoredSha512Path -Raw).Trim() -cne $NuGetVerification.publishedHashBase64) {
            return $false
        }
        $restoredMetadata = Get-Content -LiteralPath $restoredMetadataPath -Raw | ConvertFrom-Json
        if ($restoredMetadata.version -ne 2 -or [string]$restoredMetadata.source -cne $candidateSource -or
            [string]$restoredMetadata.contentHash -cne $evidence.restoreContentHashBase64 -or
            -not (Test-Sha512Base64Value -Value $evidence.restoreContentHashBase64)) { return $false }

        $candidateSourceNodes = @(Select-Xml -LiteralPath $configPath -XPath '/configuration/packageSources/add[@key="verified-candidate"]')
        $mappingNodes = @(Select-Xml -LiteralPath $configPath -XPath '/configuration/packageSourceMapping/packageSource[@key="verified-candidate"]/package[@pattern="ModelContextProtocol"]')
        if ($candidateSourceNodes.Count -ne 1 -or $candidateSourceNodes[0].Node.value -cne $candidateSource -or
            $mappingNodes.Count -ne 1) { return $false }
        return $true
    }
    catch { return $false }
}

function Test-PlatformCandidateResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$CandidateId
    )

    try {
        $matrix = Get-PlatformMatrix
        $candidate = @($matrix.candidates | Where-Object id -eq $CandidateId)
        if ($candidate.Count -ne 1) { return $false }
        $result = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        if (-not (Test-ExactPropertySet $result $script:ResultFields)) { return $false }
        if ($result.schemaVersion -ne '1.0' -or $result.candidateId -ne $CandidateId) { return $false }
        if ($result.sdkVersion -ne $candidate[0].sdkVersion -or
            $result.targetFramework -ne $candidate[0].targetFramework -or
            $result.mcpSdkVersion -ne $candidate[0].mcpSdkVersion -or
            $result.protocolRevision -ne $candidate[0].protocolRevision -or
            $result.protocolProfile -ne $candidate[0].protocolProfile) { return $false }
        if ($result.commit -notmatch '^[0-9a-f]{40}$') { return $false }
        if ($result.startedUtc -isnot [string] -or $result.completedUtc -isnot [string]) { return $false }
        try {
            $started = Get-Date -Date $result.startedUtc
            $completed = Get-Date -Date $result.completedUtc
        }
        catch { return $false }
        if ($completed -lt $started) { return $false }

        $candidateRoot = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $Path).Path) $CandidateId
        if (-not (Test-Path -LiteralPath $candidateRoot -PathType Container)) { return $false }
        $stdoutHashes = @(Get-ChildItem -LiteralPath $candidateRoot -Recurse -File -Filter '*.stdout.log' | ForEach-Object { Get-Sha256 $_.FullName })
        $stderrHashes = @(Get-ChildItem -LiteralPath $candidateRoot -Recurse -File -Filter '*.stderr.log' | ForEach-Object { Get-Sha256 $_.FullName })

        $probes = @($result.probes)
        if ($probes.Count -ne @($matrix.requiredProbeNames).Count) { return $false }
        if (@($probes.name | Select-Object -Unique).Count -ne $probes.Count) { return $false }
        if ((@($probes.name) -join "`n") -ne (@($matrix.requiredProbeNames) -join "`n")) { return $false }

        foreach ($probe in $probes) {
            if (-not (Test-ExactPropertySet $probe $script:ProbeFields)) { return $false }
            if (-not (Test-JsonInteger $probe.exitCode) -or $probe.passed -isnot [bool] -or
                -not (Test-Sha256Text $probe.stdoutSha256) -or -not (Test-Sha256Text $probe.stderrSha256) -or
                $probe.stdoutSha256 -cnotin $stdoutHashes -or $probe.stderrSha256 -cnotin $stderrHashes -or
                -not (Test-RetainedArtifactMap $probe.artifactSha256 $candidateRoot)) { return $false }
            $cases = @($probe.cases)
            foreach ($case in $cases) {
                if (-not (Test-ExactPropertySet $case $script:CaseFields)) { return $false }
                if ($case.scenario -cne $probe.name) { return $false }
                if (-not (Test-JsonInteger $case.exitCode) -or $case.passed -isnot [bool] -or
                    [bool]$case.passed -ne ($case.exitCode -eq 0) -or
                    ($case.passed -and $case.failureStage -cne 'none') -or
                    (-not $case.passed -and $case.failureStage -cnotin @('compile', 'publish', 'launch', 'profile', 'probe')) -or
                    -not (Test-Sha256Text $case.stdoutSha256) -or -not (Test-Sha256Text $case.stderrSha256) -or
                    $case.stdoutSha256 -cnotin $stdoutHashes -or $case.stderrSha256 -cnotin $stderrHashes -or
                    -not (Test-RetainedArtifactMap $case.artifactSha256 $candidateRoot)) { return $false }
                if (-not $case.passed) {
                    $allowedFailureStages = if ($case.scenario -eq 'self-contained-publish') { @('publish') }
                        elseif ($case.scenario -eq 'native-layout') { @('publish') }
                        elseif ($case.scenario -eq 'self-contained-stdio') { @('publish', 'launch', 'profile') }
                        elseif ($case.scenario -in @($matrix.sdkSurfaceProbeNames)) { @('publish', 'launch', 'profile') }
                        elseif ($case.scenario -in @('normal-restore', 'win-x64-restore', 'release-build')) { @('compile') }
                        else { @('probe') }
                    if ($case.failureStage -cnotin $allowedFailureStages) { return $false }
                }
            }

            if ($probe.name -in @($matrix.sdkSurfaceProbeNames)) {
                if ($cases.Count -ne @($matrix.sdkSurfaceHostModes).Count) { return $false }
                if (@($cases.hostMode | Select-Object -Unique).Count -ne $cases.Count) { return $false }
                if ((@($cases.hostMode) -join "`n") -ne (@($matrix.sdkSurfaceHostModes) -join "`n")) { return $false }
                if (-not (Test-ArtifactMapEqualsCaseUnion -ArtifactMap $probe.artifactSha256 -Cases $cases)) { return $false }
                foreach ($case in $cases) {
                    if (-not $case.passed) {
                        $publishLeaf = switch ($case.hostMode) {
                            'normal' { 'sdk-probe-normal' }
                            'win-x64-framework-dependent' { 'sdk-probe-framework-dependent' }
                            'win-x64-self-contained' { 'sdk-probe-self-contained' }
                        }
                        $expectedHost = Join-Path $candidateRoot "publishes\$publishLeaf\sdkcandidateprobe.exe"
                        $expectedManifest = Join-Path $candidateRoot "publish-manifests\$($case.hostMode).json"
                        $expectedEvidence = Join-Path $candidateRoot "$($probe.name).$($case.hostMode).evidence.json"
                        $caseArtifactNames = @($case.artifactSha256.PSObject.Properties | ForEach-Object { $_.Name })
                        $hasHostArtifact = $expectedHost -cin $caseArtifactNames
                        $hasManifestArtifact = $expectedManifest -cin $caseArtifactNames
                        $hasEvidenceArtifact = $expectedEvidence -cin $caseArtifactNames
                        if ((Test-Path -LiteralPath $expectedEvidence -PathType Leaf) -and -not $hasEvidenceArtifact) { return $false }
                        if ($case.failureStage -eq 'publish' -and
                            ($hasEvidenceArtifact -or ($hasHostArtifact -and $hasManifestArtifact))) { return $false }
                        if ($case.failureStage -eq 'launch' -and
                            (-not $hasHostArtifact -or ($hasManifestArtifact -and $hasEvidenceArtifact))) { return $false }
                        if ($case.failureStage -eq 'profile' -and
                            (-not $hasHostArtifact -or -not $hasManifestArtifact -or -not $hasEvidenceArtifact)) { return $false }
                    }
                    if ($case.passed -and -not (Test-SdkSurfaceEvidence -CandidateRoot $candidateRoot -Candidate $candidate[0] -Probe $probe -Case $case)) {
                        return $false
                    }
                }
            }
            elseif ($cases.Count -ne 1 -or $cases[0].hostMode -ne 'candidate-worktree') {
                return $false
            }

            if ($probe.name -ceq 'tools-list-output-schema') {
                $schemaSource = @($probes | Where-Object name -CEQ 'cancellation-progress-injection-schema')
                if ($schemaSource.Count -ne 1 -or
                    -not (Test-ArtifactMapEqualsCaseUnion -ArtifactMap $probe.artifactSha256 -Cases @($schemaSource[0].cases)) -or
                    -not (Test-ArtifactMapEqualsCaseUnion -ArtifactMap $cases[0].artifactSha256 -Cases @($schemaSource[0].cases))) {
                    return $false
                }
            }

            if ($probe.name -ceq 'nuget-package-existence-hash') {
                if ($probe.passed -and -not (Test-NuGetPackageResultEvidence -CandidateRoot $candidateRoot `
                    -Candidate $candidate[0] -Probe $probe -Case $cases[0])) { return $false }
            }
            elseif ($null -ne $probe.nuGetPackage) { return $false }

            if ($probe.name -cin @('normal-restore', 'win-x64-restore') -and $cases[0].passed) {
                $nugetProbe = @($probes | Where-Object name -CEQ 'nuget-package-existence-hash')
                if ($nugetProbe.Count -ne 1 -or $nugetProbe[0].passed -ne $true -or
                    $null -eq $nugetProbe[0].nuGetPackage -or
                    -not (Test-RestorePackageResultEvidence -CandidateRoot $candidateRoot -Candidate $candidate[0] `
                        -Probe $probe -Case $cases[0] -NuGetVerification $nugetProbe[0].nuGetPackage)) { return $false }
            }

            if ($probe.name -ceq 'self-contained-stdio' -and $cases[0].passed -and -not (Test-ProductionStdioEvidence `
                -EvidencePath (Join-Path $candidateRoot 'self-contained-stdio.evidence.json') `
                -RawStdoutPath (Join-Path $candidateRoot 'self-contained-stdio.server.stdout.log') `
                -RawStderrPath (Join-Path $candidateRoot 'self-contained-stdio.server.stderr.log') `
                -ServerPath (Join-Path $candidateRoot 'publishes\server-self-contained\WpaMcp.exe') `
                -PublishRoot (Join-Path $candidateRoot 'publishes\server-self-contained') -Candidate $candidate[0])) {
                return $false
            }
            if ($probe.name -ceq 'self-contained-stdio' -and -not $cases[0].passed) {
                $expectedServer = Join-Path $candidateRoot 'publishes\server-self-contained\WpaMcp.exe'
                $expectedEvidence = Join-Path $candidateRoot 'self-contained-stdio.evidence.json'
                if ($cases[0].failureStage -eq 'launch' -and
                    (-not (Test-Path -LiteralPath $expectedServer -PathType Leaf) -or
                     (Test-Path -LiteralPath $expectedEvidence -PathType Leaf))) { return $false }
                if ($cases[0].failureStage -eq 'profile' -and
                    (-not (Test-Path -LiteralPath $expectedServer -PathType Leaf) -or
                     -not (Test-Path -LiteralPath $expectedEvidence -PathType Leaf))) { return $false }
            }
            if ($probe.name -ceq 'self-contained-publish' -and
                ($probe.command -cnotmatch '(?:^|\s)-r\s+win-x64(?:\s|$)' -or
                 $probe.command -cnotmatch '(?:^|\s)--self-contained\s+true(?:\s|$)')) { return $false }
            if ($probe.name -ceq 'golden-traceevent-reads' -and $cases[0].passed) {
                $expectedEvidence = Join-Path $candidateRoot 'golden-traceevent-reads.evidence.json'
                $probeArtifacts = @($probe.artifactSha256.PSObject.Properties.Name)
                $caseArtifacts = @($cases[0].artifactSha256.PSObject.Properties.Name)
                if ($expectedEvidence -cnotin $probeArtifacts -or $expectedEvidence -cnotin $caseArtifacts -or
                    -not (Test-GoldenTraceEventEvidence -EvidencePath $expectedEvidence)) { return $false }
            }
            if ($probe.name -ceq 'native-layout' -and $cases[0].passed) {
                $publishRoot = Join-Path $candidateRoot 'publishes\server-self-contained'
                $expectedEvidence = Join-Path $candidateRoot 'native-layout.evidence.json'
                $expectedArtifacts = @(
                    (Join-Path $publishRoot 'WpaMcp.exe'),
                    (Join-Path $publishRoot 'amd64\msdia140.dll'),
                    (Join-Path $publishRoot 'amd64\KernelTraceControl.dll'),
                    $expectedEvidence)
                $probeArtifacts = @($probe.artifactSha256.PSObject.Properties.Name)
                $caseArtifacts = @($cases[0].artifactSha256.PSObject.Properties.Name)
                foreach ($expectedArtifact in $expectedArtifacts) {
                    if ($expectedArtifact -cnotin $probeArtifacts -or $expectedArtifact -cnotin $caseArtifacts) { return $false }
                }
                if (-not (Test-NativeLayoutEvidence -EvidencePath $expectedEvidence -PublishRoot $publishRoot)) { return $false }
            }
            if ($probe.name -ceq 'windows-dia-pdb-resolution' -and $cases[0].passed) {
                $expectedEvidence = Join-Path $candidateRoot 'windows-dia-pdb-resolution.evidence.json'
                $expectedMsdia = Join-Path $candidateRoot 'publishes\server-self-contained\amd64\msdia140.dll'
                $expectedArtifacts = @(
                    $expectedMsdia,
                    (Join-Path $candidateRoot 'dia-probe\platform-dia-probe.dll'),
                    (Join-Path $candidateRoot 'dia-probe\platform-dia-probe.pdb'),
                    $expectedEvidence)
                $probeArtifacts = @($probe.artifactSha256.PSObject.Properties.Name)
                $caseArtifacts = @($cases[0].artifactSha256.PSObject.Properties.Name)
                foreach ($expectedArtifact in $expectedArtifacts) {
                    if ($expectedArtifact -cnotin $probeArtifacts -or $expectedArtifact -cnotin $caseArtifacts) { return $false }
                }
                if (-not (Test-WindowsDiaEvidence -EvidencePath $expectedEvidence -CandidateRoot $candidateRoot -MsdiaPath $expectedMsdia)) { return $false }
            }
            if ($probe.name -ceq 'windows-architecture-matrix') {
                $expectedEvidence = Join-Path $candidateRoot 'windows-architecture-matrix.evidence.json'
                $probeArtifacts = @($probe.artifactSha256.PSObject.Properties.Name)
                $caseArtifacts = @($cases[0].artifactSha256.PSObject.Properties.Name)
                if ($expectedEvidence -cnotin $probeArtifacts -or $expectedEvidence -cnotin $caseArtifacts -or
                    -not (Test-ArtifactMapEqualsCaseUnion -ArtifactMap $probe.artifactSha256 -Cases @($cases[0]))) {
                    return $false
                }
                if ($cases[0].passed) {
                    if (-not (Test-WindowsArchitectureEvidence -EvidencePath $expectedEvidence -CandidateRoot $candidateRoot -Matrix $matrix)) {
                        return $false
                    }
                }
                else {
                    try { $failedArchitectureEvidence = Get-Content -LiteralPath $expectedEvidence -Raw | ConvertFrom-Json }
                    catch { return $false }
                    if ($failedArchitectureEvidence.schemaVersion -cne '1.0' -or
                        $failedArchitectureEvidence.probeName -cne 'windows-architecture-matrix' -or
                        $failedArchitectureEvidence.passed -isnot [bool] -or $failedArchitectureEvidence.passed -ne $false) {
                        return $false
                    }
                }
            }

            $expectedPassed = $probe.exitCode -eq 0 -and @($cases | Where-Object { -not $_.passed }).Count -eq 0
            if ([bool]$probe.passed -ne $expectedPassed) { return $false }
        }

        return $true
    }
    catch {
        Write-Verbose "Candidate result validation exception: $($_.Exception.Message)"
        return $false
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha512Base64 {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ExpectedBase64)

    $prefix = Join-Path $env:TEMP "wpamcp-sha512-$([Guid]::NewGuid().ToString('N'))"
    $base64Path = "$prefix.base64.txt"
    $publishedPath = "$prefix.published.bin"
    $hexPath = "$prefix.hex.txt"
    $computedPath = "$prefix.computed.bin"
    try {
        Set-Content -LiteralPath $base64Path -Value $ExpectedBase64 -Encoding ASCII
        & (Join-Path $env:SystemRoot 'System32\certutil.exe') -f -decode $base64Path $publishedPath *> $null
        if ($LASTEXITCODE -ne 0) { throw 'NuGet catalog SHA-512 was not valid base64.' }
        Set-Content -LiteralPath $hexPath -Value (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash -Encoding ASCII
        & (Join-Path $env:SystemRoot 'System32\certutil.exe') -f -decodehex $hexPath $computedPath *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to materialize downloaded package SHA-512.' }
        if ((Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $computedPath -Algorithm SHA256).Hash) {
            throw 'Downloaded package SHA-512 does not match NuGet catalog metadata.'
        }
        return $ExpectedBase64
    }
    finally {
        Remove-Item -LiteralPath $base64Path, $publishedPath, $hexPath, $computedPath -Force -ErrorAction SilentlyContinue
    }
}

function Test-Sha512Base64Value {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    $prefix = Join-Path $env:TEMP "wpamcp-sha512-value-$([Guid]::NewGuid().ToString('N'))"
    $base64Path = "$prefix.base64.txt"
    $decodedPath = "$prefix.bin"
    try {
        Set-Content -LiteralPath $base64Path -Value $Value -Encoding ASCII
        & (Join-Path $env:SystemRoot 'System32\certutil.exe') -f -decode $base64Path $decodedPath *> $null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $decodedPath -PathType Leaf)) { return $false }
        return (Get-Item -LiteralPath $decodedPath).Length -eq 64
    }
    finally {
        Remove-Item -LiteralPath $base64Path, $decodedPath -Force -ErrorAction SilentlyContinue
    }
}

function Test-NuGetCatalogEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RegistrationPath,
        [Parameter(Mandatory)][string]$CatalogPath,
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [string]$DownloadedHashBase64
    )

    $registration = Get-Content -LiteralPath $RegistrationPath -Raw | ConvertFrom-Json
    $catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
    $catalogUrl = $registration.catalogEntry
    if ($registration.listed -ne $true -or $catalogUrl -isnot [string] -or $catalogUrl -notmatch '^https://') {
        throw 'NuGet registration leaf is unlisted or omitted its catalogEntry URL.'
    }
    if ($catalog.'@id' -ne $catalogUrl -or $catalog.id -ne 'ModelContextProtocol' -or
        $catalog.listed -ne $true -or $catalog.version -cne $ExpectedVersion) {
        throw "NuGet catalog did not list exact ModelContextProtocol version $ExpectedVersion."
    }
    if ($catalog.packageHashAlgorithm -cne 'SHA512' -or $catalog.packageHash -isnot [string] -or
        [string]::IsNullOrWhiteSpace($catalog.packageHash)) {
        throw 'NuGet catalog omitted the exact SHA512 package hash contract.'
    }
    $downloadedHash = if ($DownloadedHashBase64) { $DownloadedHashBase64 } else { Get-Sha512Base64 $PackagePath $catalog.packageHash }
    if ($downloadedHash -cne $catalog.packageHash) {
        throw 'Downloaded package SHA-512 does not match NuGet catalog metadata.'
    }

    [ordered]@{
        packageId = 'ModelContextProtocol'
        packageVersion = $ExpectedVersion
        registrationUrl = [string]$registration.'@id'
        catalogUrl = [string]$catalogUrl
        packageContentUrl = [string]$registration.packageContent
        hashAlgorithm = 'SHA512'
        publishedHashBase64 = [string]$catalog.packageHash
        downloadedHashBase64 = $downloadedHash
    }
}

function New-Directory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        [void](New-Item -ItemType Directory -Path $Path)
    }
}

function Resolve-PlatformOutputRoot {
    param([Parameter(Mandatory)][string]$Path)

    $candidate = if ($Path -match '^(?:[A-Za-z]:[\\/]|[\\/]{2})') {
        $Path
    }
    else {
        Join-Path $script:RepositoryRoot $Path
    }
    New-Directory $candidate
    return (Resolve-Path -LiteralPath $candidate).Path
}

function New-FreshDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        throw "Evidence root already exists: $Path"
    }
    [void](New-Item -ItemType Directory -Path $Path)
}

function Write-NewUtf8File {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][AllowEmptyString()][string]$Content)

    if (Test-Path -LiteralPath $Path) { throw "CreateNew target already exists: $Path" }
    $temporaryPath = "$Path.$([Guid]::NewGuid().ToString('N')).utf8.tmp"
    $createdTarget = $false
    try {
        Set-Content -LiteralPath $temporaryPath -Value $Content -Encoding UTF8 -NoNewline
        [byte[]]$bytes = Get-Content -LiteralPath $temporaryPath -Encoding Byte -Raw
        $offset = if ($bytes.Count -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
        [void](New-Item -ItemType File -Path $Path)
        $createdTarget = $true
        if ($bytes.Count -gt $offset) {
            [byte[]]$payload = $bytes[$offset..($bytes.Count - 1)]
            Set-Content -LiteralPath $Path -Value $payload -Encoding Byte -NoNewline
        }
    }
    catch {
        if ($createdTarget) { Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue }
        throw
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Write-CandidateGlobalJson {
    param(
        [Parameter(Mandatory)][string]$Workspace,
        [Parameter(Mandatory)][string]$Content
    )

    if (-not (Test-Path -LiteralPath $Workspace -PathType Container)) {
        throw "Candidate workspace does not exist: $Workspace"
    }
    $resolvedWorkspace = (Resolve-Path -LiteralPath $Workspace).Path
    $globalJsonPath = Join-Path $resolvedWorkspace 'global.json'
    if ((Split-Path -Parent $globalJsonPath) -cne $resolvedWorkspace) {
        throw "Refusing to replace global.json outside candidate workspace '$resolvedWorkspace'."
    }
    if (Test-Path -LiteralPath $globalJsonPath) {
        if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
            throw "Candidate global.json is not a file: $globalJsonPath"
        }
        Remove-Item -LiteralPath $globalJsonPath -Force
    }
    Write-NewUtf8File -Path $globalJsonPath -Content $Content
    return (Resolve-Path -LiteralPath $globalJsonPath).Path
}

function New-PublishManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$HostMode,
        [Parameter(Mandatory)][string]$PublishRoot,
        [Parameter(Mandatory)]$PublishResult,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$RuntimeEvidencePath,
        [string]$DotNetRoot
    )

    if ($PublishResult.ExitCode -ne 0) { throw "Cannot manifest failed $HostMode publish." }
    $resolvedRoot = (Resolve-Path -LiteralPath $PublishRoot).Path
    $rootPrefix = ($resolvedRoot -replace '[\\/]+$', '') + '\'
    $required = @('sdkcandidateprobe.exe', 'sdkcandidateprobe.dll', 'sdkcandidateprobe.deps.json', 'sdkcandidateprobe.runtimeconfig.json')
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $resolvedRoot $name) -PathType Leaf)) {
            throw "$HostMode publish omitted required launch file '$name'."
        }
    }
    $files = @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            relativePath = $_.FullName.Substring($rootPrefix.Length).Replace('\', '/')
            sha256 = Get-Sha256 $_.FullName
        }
    })
    if ($files.Count -eq 0) { throw "$HostMode publish manifest was empty." }
    if (-not (Test-Path -LiteralPath $RuntimeEvidencePath -PathType Leaf)) {
        throw "$HostMode runtime evidence was not retained before manifest finalization."
    }
    $runtimeEvidence = Get-Content -LiteralPath $RuntimeEvidencePath -Raw | ConvertFrom-Json
    $launchIdentity = $runtimeEvidence.launchIdentity
    $runtimeIdentity = $launchIdentity.runtimeIdentity
    if ($runtimeEvidence.hostMode -cne $HostMode -or
        $launchIdentity.passed -ne $true -or $null -eq $runtimeIdentity) {
        throw "$HostMode runtime evidence did not prove a successful matching child launch."
    }
    $hostFxrPath = [string]$runtimeIdentity.LoadedHostFxrPath
    $hostPolicyPath = [string]$runtimeIdentity.LoadedHostPolicyPath
    if (-not (Test-Path -LiteralPath $hostFxrPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $hostPolicyPath -PathType Leaf)) {
        throw "$HostMode runtime evidence referenced missing loaded host modules."
    }
    $hostFxrPath = (Resolve-Path -LiteralPath $hostFxrPath).Path
    $hostPolicyPath = (Resolve-Path -LiteralPath $hostPolicyPath).Path
    $sourceHostFxrSha256 = Get-Sha256 $hostFxrPath
    $sourceHostPolicySha256 = Get-Sha256 $hostPolicyPath
    if ($sourceHostFxrSha256 -cne [string]$runtimeIdentity.LoadedHostFxrSha256 -or
        $sourceHostPolicySha256 -cne [string]$runtimeIdentity.LoadedHostPolicySha256) {
        throw "$HostMode loaded host module hashes changed after child observation."
    }
    $frameworkRuntime = $null
    if ($HostMode -eq 'win-x64-self-contained') {
        if ($hostFxrPath -cne (Join-Path $resolvedRoot 'hostfxr.dll') -or
            $hostPolicyPath -cne (Join-Path $resolvedRoot 'hostpolicy.dll')) {
            throw "$HostMode runtime evidence did not bind the published host modules."
        }
    }
    else {
        if (-not $DotNetRoot -or -not (Test-Path -LiteralPath $DotNetRoot -PathType Container)) {
            throw "$HostMode publish omitted its DOTNET_ROOT observation."
        }
        $resolvedDotNetRoot = (Resolve-Path -LiteralPath $DotNetRoot).Path
        $dotNetRootPrefix = ($resolvedDotNetRoot -replace '[\\/]+$', '') + '\'
        if ([string]$launchIdentity.configuredDotNetRoot -cne $resolvedDotNetRoot -or
            [string]$launchIdentity.configuredDotNetRootX64 -cne $resolvedDotNetRoot -or
            -not $hostFxrPath.StartsWith($dotNetRootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not $hostPolicyPath.StartsWith($dotNetRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$HostMode runtime evidence did not bind loaded modules to the exact configured DOTNET_ROOT."
        }
        $candidateRoot = Split-Path -Parent (Split-Path -Parent $ManifestPath)
        $retainedRuntimeRoot = Join-Path $candidateRoot "framework-runtime\$HostMode"
        New-FreshDirectory $retainedRuntimeRoot
        $retainedHostFxrPath = Join-Path $retainedRuntimeRoot 'hostfxr.dll'
        $retainedHostPolicyPath = Join-Path $retainedRuntimeRoot 'hostpolicy.dll'
        Copy-Item -LiteralPath $hostFxrPath -Destination $retainedHostFxrPath
        Copy-Item -LiteralPath $hostPolicyPath -Destination $retainedHostPolicyPath
        $retainedHostFxrSha256 = Get-Sha256 $retainedHostFxrPath
        $retainedHostPolicySha256 = Get-Sha256 $retainedHostPolicyPath
        if ($sourceHostFxrSha256 -cne $retainedHostFxrSha256) { throw "$HostMode retained hostfxr copy changed bytes." }
        if ($sourceHostPolicySha256 -cne $retainedHostPolicySha256) { throw "$HostMode retained hostpolicy copy changed bytes." }
        $frameworkRuntime = [ordered]@{
            dotnetRoot = $resolvedDotNetRoot
            sourceHostFxrPath = (Resolve-Path -LiteralPath $hostFxrPath).Path
            sourceHostFxrSha256 = $sourceHostFxrSha256
            retainedHostFxrPath = (Resolve-Path -LiteralPath $retainedHostFxrPath).Path
            retainedHostFxrSha256 = $retainedHostFxrSha256
            sourceHostPolicyPath = (Resolve-Path -LiteralPath $hostPolicyPath).Path
            sourceHostPolicySha256 = $sourceHostPolicySha256
            retainedHostPolicyPath = (Resolve-Path -LiteralPath $retainedHostPolicyPath).Path
            retainedHostPolicySha256 = $retainedHostPolicySha256
        }
    }
    $manifest = [ordered]@{
        schemaVersion = '1.0'
        hostMode = $HostMode
        publishRoot = $resolvedRoot
        publishExitCode = [int]$PublishResult.ExitCode
        frameworkRuntime = $frameworkRuntime
        files = $files
    }
    Write-NewUtf8File $ManifestPath ($manifest | ConvertTo-Json -Depth 6)
    return [ordered]@{ Path = $ManifestPath; Sha256 = Get-Sha256 $ManifestPath; Manifest = $manifest }
}

function Complete-PublishManifestEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$HostMode,
        [Parameter(Mandatory)][string]$PublishRoot,
        [Parameter(Mandatory)]$PublishResult,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$RuntimeEvidencePath,
        [Parameter(Mandatory)][string]$DotNetRoot)

    if ($PublishResult.ExitCode -ne 0) { return $null }
    try {
        return New-PublishManifest -HostMode $HostMode -PublishRoot $PublishRoot -PublishResult $PublishResult `
            -ManifestPath $ManifestPath -RuntimeEvidencePath $RuntimeEvidencePath -DotNetRoot $DotNetRoot
    }
    catch {
        Add-Content -LiteralPath $PublishResult.StderrPath -Value "Publish manifest validation failed: $($_.Exception.Message)" -Encoding UTF8
        $PublishResult.ExitCode = 1
        $PublishResult.StderrSha256 = Get-Sha256 $PublishResult.StderrPath
        return $null
    }
}

function Complete-SdkCaseRuntimeManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$HostMode,
        [Parameter(Mandatory)][string]$PublishRoot,
        [Parameter(Mandatory)][string]$HostPath,
        [Parameter(Mandatory)]$PublishResult,
        [Parameter(Mandatory)]$CaseResult,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$RuntimeEvidencePath,
        [Parameter(Mandatory)][string]$DotNetRoot)

    if ($PublishResult.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $HostPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $RuntimeEvidencePath -PathType Leaf)) { return $null }
    try {
        $runtimeEvidence = Get-Content -LiteralPath $RuntimeEvidencePath -Raw | ConvertFrom-Json
        if ($runtimeEvidence.hostMode -cne $HostMode -or $runtimeEvidence.launchIdentity.passed -ne $true -or
            $null -eq $runtimeEvidence.launchIdentity.runtimeIdentity) { return $null }
    }
    catch { return $null }

    try {
        return New-PublishManifest -HostMode $HostMode -PublishRoot $PublishRoot -PublishResult $PublishResult `
            -ManifestPath $ManifestPath -RuntimeEvidencePath $RuntimeEvidencePath -DotNetRoot $DotNetRoot
    }
    catch {
        Add-Content -LiteralPath $CaseResult.StderrPath `
            -Value "Runtime manifest finalization failed: $($_.Exception.Message)" -Encoding UTF8
        $CaseResult.ExitCode = 1
        $CaseResult.StderrSha256 = Get-Sha256 $CaseResult.StderrPath
        return $null
    }
}

function Get-SdkCaseFailureStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$HostPath,
        [Parameter(Mandatory)]$PublishResult,
        [Parameter(Mandatory)]$CaseResult,
        $HostManifest)

    if ($PublishResult.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $HostPath -PathType Leaf)) { return 'publish' }
    $startFailedProperty = $CaseResult.PSObject.Properties['StartFailed']
    if (($null -ne $startFailedProperty -and $startFailedProperty.Value -eq $true) -or $null -eq $HostManifest) { return 'launch' }
    return 'profile'
}

function Copy-TrackedCommit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        throw "Candidate workspace already exists: $Destination"
    }
    if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Commit must be a resolved 40-hex object id.' }
    $resolvedCommit = (& git -C $RepositoryRoot rev-parse $Commit).Trim()
    if ($LASTEXITCODE -ne 0 -or $resolvedCommit -cne $Commit) { throw 'Commit did not resolve to the recorded source commit.' }
    $objectType = (& git -C $RepositoryRoot cat-file -t $Commit).Trim()
    if ($LASTEXITCODE -ne 0 -or $objectType -cne 'commit') { throw 'Recorded source object was not a commit.' }

    New-Directory $Destination
    $archivePath = Join-Path (Split-Path -Parent $Destination) "source-$Commit-$([Guid]::NewGuid().ToString('N')).tar"
    try {
        & git -C $RepositoryRoot archive --format=tar --output=$archivePath $Commit
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw 'git archive failed.' }
        & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $archivePath -C $Destination
        if ($LASTEXITCODE -ne 0) { throw 'tar extraction of git archive failed.' }
    }
    finally {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    }

    @{ commit = $Commit; archiveTreeish = $Commit }
}

function Copy-TrackedWorktree {
    param([Parameter(Mandatory)][string]$Destination, [Parameter(Mandatory)][string]$Commit)
    [void](Copy-TrackedCommit -RepositoryRoot $script:RepositoryRoot -Commit $Commit -Destination $Destination)
}

function Get-ExactDotNet {
    param([Parameter(Mandatory)][string]$SdkVersion)

    $configuredHost = $env:WPAMCP_DOTNET_HOST
    if ($null -eq $configuredHost) { $configuredHost = $env:DOTNET_HOST_PATH }

    $hostPaths = @($configuredHost)
    $pathHost = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $pathHost) { $hostPaths += [string]$pathHost.Source }
    $hostPaths += Join-Path $script:RepositoryRoot ".superpowers\sdd\2026-07-29-platform-release-governance\dotnet-$SdkVersion\dotnet.exe"

    foreach ($hostPath in $hostPaths) {
        $hostPath = [string]$hostPath
        if ($hostPath -notmatch '\S' -or -not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { continue }
        try {
            $resolvedHost = (Resolve-Path -LiteralPath $hostPath).Path
            $installed = @(& $resolvedHost --list-sdks 2>$null)
            if ($LASTEXITCODE -eq 0 -and
                ($installed | Where-Object { $_ -match "^$([regex]::Escape($SdkVersion))\s" })) {
                return $resolvedHost
            }
        }
        catch { }
    }
    throw "Exact .NET SDK $SdkVersion is not installed or provisioned."
}

function ConvertTo-CommandLineArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Invoke-CapturedCommand {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$LogPrefix,
        [hashtable]$Environment = @{},
        [ValidateRange(1, 86400)][int]$TimeoutSeconds = 900,
        [string]$Stage = ''
    )

    $stdoutPath = "$LogPrefix.stdout.log"
    $stderrPath = "$LogPrefix.stderr.log"
    $argumentLine = (($Arguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' ')
    $savedEnvironment = @{}
    $missingEnvironment = @()
    foreach ($entry in $Environment.GetEnumerator()) {
        $environmentPath = "Env:$($entry.Key)"
        if (Test-Path $environmentPath) { $savedEnvironment[$entry.Key] = (Get-Item $environmentPath).Value }
        else { $missingEnvironment += $entry.Key }
        Set-Item $environmentPath ([string]$entry.Value)
    }
    $timedOut = $false
    $process = $null
    $startFailure = $null
    try {
        try {
            $process = Start-Process -FilePath $Executable -ArgumentList $argumentLine -WorkingDirectory $WorkingDirectory `
                -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
            # Bind the native handle before a fast child exits so ExitCode remains available.
            [void]$process.Handle
        }
        catch {
            $startFailure = $_.Exception.Message
        }
    }
    finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) { Set-Item "Env:$($entry.Key)" ([string]$entry.Value) }
        foreach ($name in $missingEnvironment) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
    }
    if ($null -eq $process) {
        if (-not (Test-Path -LiteralPath $stdoutPath -PathType Leaf)) { Set-Content -LiteralPath $stdoutPath -Value '' -NoNewline }
        Set-Content -LiteralPath $stderrPath -Value "Stage '$Stage' failed to start: $startFailure" -Encoding UTF8
        return [ordered]@{
            Command = "$Executable $argumentLine"
            ExitCode = 125
            TimedOut = $false
            StartFailed = $true
            StdoutPath = $stdoutPath
            StderrPath = $stderrPath
            StdoutSha256 = Get-Sha256 $stdoutPath
            StderrSha256 = Get-Sha256 $stderrPath
        }
    }
    $process | Wait-Process -Timeout $TimeoutSeconds -ErrorAction SilentlyContinue
    if (-not $process.HasExited) {
        $timedOut = $true
        & (Join-Path $env:SystemRoot 'System32\cmd.exe') /d /s /c "taskkill /PID $($process.Id) /T /F >nul 2>&1" | Out-Null
        if (-not $process.HasExited) {
            $process | Stop-Process -Force -ErrorAction SilentlyContinue
        }
        $process | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
        if (-not $process.HasExited) {
            throw "Stage '$Stage' timed out and its process tree did not terminate."
        }
    }
    if ($timedOut) {
        $stageName = if ($Stage) { $Stage } else { Split-Path -Leaf $LogPrefix }
        Add-Content -LiteralPath $stderrPath -Value "${stageName} timed out after $TimeoutSeconds seconds." -Encoding UTF8
    }
    [ordered]@{
        Command = "$Executable $argumentLine"
        ExitCode = if ($timedOut) { 124 } else { $process.ExitCode }
        TimedOut = $timedOut
        StartFailed = $false
        StdoutPath = $stdoutPath
        StderrPath = $stderrPath
        StdoutSha256 = Get-Sha256 $stdoutPath
        StderrSha256 = Get-Sha256 $stderrPath
    }
}

function Get-NuGetPackageResourceEndpoints {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Service)

    $registrationResources = @($Service.resources | Where-Object { $_.'@type' -ceq 'RegistrationsBaseUrl/3.6.0' })
    $packageResources = @($Service.resources | Where-Object { $_.'@type' -ceq 'PackageBaseAddress/3.0.0' })
    if ($registrationResources.Count -ne 1) {
        throw "NuGet service index must contain exactly one SemVer 2 registration resource; found $($registrationResources.Count)."
    }
    if ($packageResources.Count -ne 1) {
        throw "NuGet service index must contain exactly one package base address; found $($packageResources.Count)."
    }
    $registrationBase = [string]$registrationResources[0].'@id'
    $packageBase = [string]$packageResources[0].'@id'
    if ($registrationBase -cnotmatch '^https://' -or $packageBase -cnotmatch '^https://') {
        throw 'NuGet service resources must use HTTPS.'
    }
    return [ordered]@{
        registrationBase = $registrationBase
        packageBase = $packageBase
    }
}

function Get-VerifiedNuGetPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string]$CacheDirectory,
        [switch]$OfflineOnly
    )

    New-Directory $CacheDirectory
    $registrationPath = Join-Path $CacheDirectory "modelcontextprotocol.$PackageVersion.registration.json"
    $catalogPath = Join-Path $CacheDirectory "modelcontextprotocol.$PackageVersion.catalog.json"
    $metadataPath = Join-Path $CacheDirectory "modelcontextprotocol.$PackageVersion.verification.json"
    $packagePath = Join-Path $CacheDirectory "modelcontextprotocol.$PackageVersion.nupkg"
    $cachePaths = @($registrationPath, $catalogPath, $metadataPath, $packagePath)
    $existingPaths = @($cachePaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($existingPaths.Count -eq $cachePaths.Count) {
        $validated = Test-NuGetCatalogEvidence $registrationPath $catalogPath $packagePath $PackageVersion
        $retained = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        foreach ($name in @('packageId', 'packageVersion', 'registrationUrl', 'catalogUrl', 'packageContentUrl',
            'hashAlgorithm', 'publishedHashBase64', 'downloadedHashBase64')) {
            if ($retained.$name -cne $validated[$name]) {
                throw "Verified NuGet cache metadata mismatch for '$name'."
            }
        }
        if ($retained.observedUtc -isnot [string] -or $retained.retrievalSource -notin @('Network', 'VerifiedCache')) {
            throw 'Verified NuGet cache omitted immutable observation metadata.'
        }
        try { [void](Get-Date -Date $retained.observedUtc) }
        catch { throw 'Verified NuGet cache observedUtc was invalid.' }
        $observation = [ordered]@{}
        foreach ($name in @('packageId', 'packageVersion', 'registrationUrl', 'catalogUrl', 'packageContentUrl',
            'hashAlgorithm', 'publishedHashBase64', 'downloadedHashBase64')) {
            $observation[$name] = $retained.$name
        }
        $observation['observedUtc'] = [DateTimeOffset]::UtcNow.ToString('o')
        $observation['retrievalSource'] = 'VerifiedCache'
        return [ordered]@{
            Verification = $observation
            PackagePath = $packagePath
            RegistrationPath = $registrationPath
            CatalogPath = $catalogPath
            MetadataPath = $metadataPath
        }
    }
    if ($existingPaths.Count -ne 0) {
        throw "NuGet cache for ModelContextProtocol $PackageVersion is partial and cannot be reused."
    }
    if ($OfflineOnly) {
        throw "Verified NuGet cache for ModelContextProtocol $PackageVersion is unavailable offline."
    }

    $downloadPrefix = Join-Path $CacheDirectory "modelcontextprotocol.$PackageVersion.$([Guid]::NewGuid().ToString('N')).download"
    $downloadRegistration = "$downloadPrefix.registration.json"
    $downloadCatalog = "$downloadPrefix.catalog.json"
    $downloadMetadata = "$downloadPrefix.verification.json"
    $downloadPackage = "$downloadPrefix.nupkg"
    try {
        $service = Invoke-RestMethod -Uri 'https://api.nuget.org/v3/index.json' -UseBasicParsing
        $endpoints = Get-NuGetPackageResourceEndpoints -Service $service
        $registrationBase = $endpoints.registrationBase
        $packageBase = $endpoints.packageBase
        $registrationUrl = "$($registrationBase.TrimEnd('/'))/modelcontextprotocol/$PackageVersion.json"
        $registration = Invoke-RestMethod -Uri $registrationUrl -UseBasicParsing
        $registration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $downloadRegistration -Encoding UTF8
        if ($registration.catalogEntry -isnot [string]) { throw 'NuGet registration catalogEntry was not a URL.' }
        $catalog = Invoke-RestMethod -Uri $registration.catalogEntry -UseBasicParsing
        $catalog | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $downloadCatalog -Encoding UTF8
        $packageContentUrl = if ($registration.packageContent) { $registration.packageContent } else { "$($packageBase.TrimEnd('/'))/modelcontextprotocol/$PackageVersion/modelcontextprotocol.$PackageVersion.nupkg" }
        Invoke-WebRequest -Uri $packageContentUrl -OutFile $downloadPackage -UseBasicParsing
        $metadata = Test-NuGetCatalogEvidence $downloadRegistration $downloadCatalog $downloadPackage $PackageVersion
        $metadata['observedUtc'] = [DateTimeOffset]::UtcNow.ToString('o')
        $metadata['retrievalSource'] = 'Network'
        $metadata | ConvertTo-Json | Set-Content -LiteralPath $downloadMetadata -Encoding UTF8

        Move-Item -LiteralPath $downloadRegistration -Destination $registrationPath
        Move-Item -LiteralPath $downloadCatalog -Destination $catalogPath
        Move-Item -LiteralPath $downloadPackage -Destination $packagePath
        Move-Item -LiteralPath $downloadMetadata -Destination $metadataPath
    }
    finally {
        Remove-Item -LiteralPath $downloadRegistration, $downloadCatalog, $downloadPackage, $downloadMetadata -Force -ErrorAction SilentlyContinue
    }

    return [ordered]@{
        Verification = $metadata
        PackagePath = $packagePath
        RegistrationPath = $registrationPath
        CatalogPath = $catalogPath
        MetadataPath = $metadataPath
    }
}

function Copy-VerifiedNuGetEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$VerifiedPackage,
        [Parameter(Mandatory)][string]$Destination
    )

    New-FreshDirectory $Destination
    $retained = [ordered]@{}
    foreach ($name in @('PackagePath', 'RegistrationPath', 'CatalogPath')) {
        $source = [string]$VerifiedPackage[$name]
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Verified NuGet source '$name' is missing." }
        $target = Join-Path $Destination (Split-Path -Leaf $source)
        Copy-Item -LiteralPath $source -Destination $target
        if ((Get-Sha256 $source) -cne (Get-Sha256 $target)) { throw "Retained NuGet copy '$name' did not preserve bytes." }
        $retained[$name] = $target
    }
    $sourceMetadataPath = [string]$VerifiedPackage.MetadataPath
    if (-not (Test-Path -LiteralPath $sourceMetadataPath -PathType Leaf)) { throw "Verified NuGet source 'MetadataPath' is missing." }
    $retainedMetadataPath = Join-Path $Destination (Split-Path -Leaf $sourceMetadataPath)
    Write-NewUtf8File $retainedMetadataPath ($VerifiedPackage.Verification | ConvertTo-Json)
    $retained['MetadataPath'] = $retainedMetadataPath
    [void](Test-NuGetCatalogEvidence -RegistrationPath $retained.RegistrationPath -CatalogPath $retained.CatalogPath `
        -PackagePath $retained.PackagePath -ExpectedVersion $VerifiedPackage.Verification.packageVersion)
    return $retained
}

function Write-CandidateNuGetConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$CandidateSource
    )

    $resolvedSource = (Resolve-Path -LiteralPath $CandidateSource).Path
    $escapedSource = $resolvedSource.Replace('&', '&amp;').Replace('"', '&quot;')
    $content = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="verified-candidate" value="$escapedSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="verified-candidate">
      <package pattern="ModelContextProtocol" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
    Write-NewUtf8File $Path $content
}

function New-RestorePackageEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProbeName,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string]$PackagesDirectory,
        [Parameter(Mandatory)][string]$VerifiedPackagePath,
        [Parameter(Mandatory)][string]$CandidateSource,
        [Parameter(Mandatory)][string]$ConfigPath,
        [Parameter(Mandatory)][string]$PublishedHashBase64,
        [Parameter(Mandatory)][string]$EvidencePath
    )

    $resolvedSource = (Resolve-Path -LiteralPath $CandidateSource).Path
    $resolvedVerifiedPackage = (Resolve-Path -LiteralPath $VerifiedPackagePath).Path
    $resolvedConfig = (Resolve-Path -LiteralPath $ConfigPath).Path
    $installedRoot = Join-Path $PackagesDirectory "modelcontextprotocol\$PackageVersion"
    $restoredPackage = Join-Path $installedRoot "modelcontextprotocol.$PackageVersion.nupkg"
    $restoredSha512 = Join-Path $installedRoot "modelcontextprotocol.$PackageVersion.nupkg.sha512"
    $restoredMetadata = Join-Path $installedRoot '.nupkg.metadata'
    foreach ($path in @($restoredPackage, $restoredSha512, $restoredMetadata)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Restore omitted verified package evidence '$path'." }
    }
    $metadata = Get-Content -LiteralPath $restoredMetadata -Raw | ConvertFrom-Json
    $metadataSource = [string]$metadata.source
    if ($metadataSource -cne $resolvedSource) { throw "Restore consumed ModelContextProtocol from '$metadataSource' instead of the verified candidate source '$resolvedSource'." }
    $retainedContentHash = (Get-Content -LiteralPath $restoredSha512 -Raw).Trim()
    if ($retainedContentHash -cne $PublishedHashBase64) {
        throw 'Restored ModelContextProtocol package hash did not match published SHA-512 evidence.'
    }
    $restoreContentHash = [string]$metadata.contentHash
    if ($metadata.version -ne 2 -or -not (Test-Sha512Base64Value -Value $restoreContentHash)) {
        throw 'Restored ModelContextProtocol metadata omitted a valid SHA-512 content hash.'
    }
    [void](Get-Sha512Base64 -Path $restoredPackage -ExpectedBase64 $PublishedHashBase64)
    $verifiedSha256 = Get-Sha256 $resolvedVerifiedPackage
    $restoredSha256 = Get-Sha256 $restoredPackage
    if ($verifiedSha256 -cne $restoredSha256) { throw 'Restore did not consume byte-identical verified ModelContextProtocol nupkg content.' }

    $evidence = [ordered]@{
        schemaVersion = '1.0'
        probeName = $ProbeName
        packageId = 'ModelContextProtocol'
        packageVersion = $PackageVersion
        candidateSource = $resolvedSource
        configPath = $resolvedConfig
        configSha256 = Get-Sha256 $resolvedConfig
        verifiedPackagePath = $resolvedVerifiedPackage
        verifiedPackageSha256 = $verifiedSha256
        restoredPackagePath = (Resolve-Path -LiteralPath $restoredPackage).Path
        restoredPackageSha256 = $restoredSha256
        restoredSha512Path = (Resolve-Path -LiteralPath $restoredSha512).Path
        restoredMetadataPath = (Resolve-Path -LiteralPath $restoredMetadata).Path
        metadataSource = $metadataSource
        publishedHashBase64 = $PublishedHashBase64
        restoreContentHashBase64 = $restoreContentHash
        passed = $true
    }
    Write-NewUtf8File $EvidencePath ($evidence | ConvertTo-Json -Depth 5)
    return $evidence
}

function New-CaseResult {
    param(
        [string]$HostMode,
        [string]$Scenario,
        $CommandResult,
        [hashtable]$Artifacts = @{},
        [ValidateSet('compile', 'publish', 'launch', 'profile', 'probe')][string]$FailureStage = 'probe')
    [ordered]@{
        hostMode = $HostMode
        scenario = $Scenario
        command = $CommandResult.Command
        exitCode = [int]$CommandResult.ExitCode
        stdoutSha256 = $CommandResult.StdoutSha256
        stderrSha256 = $CommandResult.StderrSha256
        passed = $CommandResult.ExitCode -eq 0
        failureStage = if ($CommandResult.ExitCode -eq 0) { 'none' } else { $FailureStage }
        artifactSha256 = $Artifacts
    }
}

function New-ProbeResult {
    param([string]$Name, $CommandResult, [object[]]$Cases, [hashtable]$Artifacts = @{}, $NuGetPackage = $null)
    $passed = $CommandResult.ExitCode -eq 0 -and @($Cases | Where-Object { -not $_.passed }).Count -eq 0
    [ordered]@{
        name = $Name
        command = $CommandResult.Command
        exitCode = [int]$CommandResult.ExitCode
        stdoutSha256 = $CommandResult.StdoutSha256
        stderrSha256 = $CommandResult.StderrSha256
        passed = $passed
        artifactSha256 = $Artifacts
        cases = $Cases
        nuGetPackage = $NuGetPackage
    }
}

function Invoke-PlatformCandidate {
    $matrix = Get-PlatformMatrix
    $candidate = @($matrix.candidates | Where-Object id -eq $CandidateId)
    if ($candidate.Count -ne 1) { throw "Candidate matrix did not contain exactly one '$CandidateId'." }
    $candidate = $candidate[0]
    $startedUtc = [DateTimeOffset]::UtcNow
    $commit = (& git -C $script:RepositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Unable to resolve source commit.' }

    $outputRoot = Resolve-PlatformOutputRoot $OutputDirectory
    $resultPath = Join-Path $outputRoot "$CandidateId.result.json"
    if (Test-Path -LiteralPath $resultPath) { throw "Result already exists: $resultPath" }
    $candidateRoot = Join-Path $outputRoot $CandidateId
    New-FreshDirectory $candidateRoot
    $workspace = Join-Path $candidateRoot 'worktree'
    $logs = Join-Path $candidateRoot 'logs'
    $packages = Join-Path $candidateRoot 'packages'
    $publishes = Join-Path $candidateRoot 'publishes'
    New-Directory $logs
    New-Directory $packages
    New-Directory $publishes
    Copy-TrackedWorktree $workspace $commit

    try {
        $globalJson = [ordered]@{ sdk = [ordered]@{ version = $candidate.sdkVersion; rollForward = 'disable'; allowPrerelease = $candidate.sdkVersion -match '-' } } | ConvertTo-Json -Depth 4
        [void](Write-CandidateGlobalJson -Workspace $workspace -Content $globalJson)
        $dotnet = Get-ExactDotNet $candidate.sdkVersion
        $environment = @{
            NUGET_PACKAGES = $packages
            DOTNET_CLI_TELEMETRY_OPTOUT = '1'
            DOTNET_NOLOGO = '1'
            DOTNET_ROOT = (Split-Path -Parent $dotnet)
            DOTNET_ROOT_X64 = (Split-Path -Parent $dotnet)
        }
        $properties = @(
            "-p:WpaMcpTargetFramework=$($candidate.targetFramework)",
            "-p:WpaMcpMcpSdkVersion=$($candidate.mcpSdkVersion)",
            "-p:WpaMcpProtocolProfile=$($candidate.protocolProfile)")
        $msbuildIsolation = @('-m:1', '-nr:false', '-p:UseSharedCompilation=false')
        $probes = @()

        $nugetLog = Join-Path $logs 'nuget-package-existence-hash'
        $verification = Get-VerifiedNuGetPackage $candidate.mcpSdkVersion (Join-Path $outputRoot 'nuget-cache')
        $retainedNuGet = Copy-VerifiedNuGetEvidence $verification (Join-Path $candidateRoot 'nuget-evidence')
        $candidateNuGetSource = Split-Path -Parent $retainedNuGet.PackagePath
        $candidateNuGetConfig = Join-Path $candidateRoot 'NuGet.Config'
        Write-CandidateNuGetConfig -Path $candidateNuGetConfig -CandidateSource $candidateNuGetSource
        Write-NewUtf8File "$nugetLog.stdout.log" ($verification.Verification | ConvertTo-Json -Depth 5)
        Write-NewUtf8File "$nugetLog.stderr.log" ''
        $nugetCommand = [ordered]@{
            Command = "NuGet v3 registration and SHA-512 verification ModelContextProtocol $($candidate.mcpSdkVersion)"
            ExitCode = 0
            StdoutSha256 = Get-Sha256 "$nugetLog.stdout.log"
            StderrSha256 = Get-Sha256 "$nugetLog.stderr.log"
        }
        $nugetArtifacts = @{
            $retainedNuGet.PackagePath = Get-Sha256 $retainedNuGet.PackagePath
            $retainedNuGet.RegistrationPath = Get-Sha256 $retainedNuGet.RegistrationPath
            $retainedNuGet.CatalogPath = Get-Sha256 $retainedNuGet.CatalogPath
            $retainedNuGet.MetadataPath = Get-Sha256 $retainedNuGet.MetadataPath
        }
        $nugetCase = New-CaseResult 'candidate-worktree' 'nuget-package-existence-hash' $nugetCommand $nugetArtifacts
        $resultVerification = [ordered]@{
            packageId = $verification.Verification.packageId
            packageVersion = $verification.Verification.packageVersion
            registrationUrl = $verification.Verification.registrationUrl
            packageContentUrl = $verification.Verification.packageContentUrl
            hashAlgorithm = $verification.Verification.hashAlgorithm
            publishedHashBase64 = $verification.Verification.publishedHashBase64
            downloadedHashBase64 = $verification.Verification.downloadedHashBase64
            observedUtc = $verification.Verification.observedUtc
            retrievalSource = $verification.Verification.retrievalSource
        }
        $probes += ,(New-ProbeResult 'nuget-package-existence-hash' $nugetCommand @($nugetCase) $nugetArtifacts $resultVerification)

        $ordinaryCommands = [ordered]@{
            'normal-restore' = @('restore', 'WpaMcp.sln', '--packages', $packages, '--configfile', $candidateNuGetConfig, '--no-cache') + $properties + $msbuildIsolation
            'win-x64-restore' = @('restore', 'src\WpaMcp\WpaMcp.csproj', '-r', 'win-x64', '--packages', $packages, '--configfile', $candidateNuGetConfig, '--no-cache') + $properties + $msbuildIsolation
            'release-build' = @('build', 'WpaMcp.sln', '-c', 'Release', '--no-restore') + $properties + $msbuildIsolation
            'release-unit-tests' = @('test', 'tests\WpaMcp.Tests\WpaMcp.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
                '--filter', 'FullyQualifiedName!~WpaMcp.Tests.PlatformDecisionTests') + $properties + $msbuildIsolation
        }
        foreach ($entryName in @($ordinaryCommands.Keys)) {
            $result = Invoke-CapturedCommand $dotnet $ordinaryCommands[$entryName] $workspace (Join-Path $logs $entryName) $environment
            $ordinaryArtifacts = @{}
            if ($entryName -in @('normal-restore', 'win-x64-restore') -and $result.ExitCode -eq 0) {
                $restoreEvidencePath = Join-Path $candidateRoot "$entryName.package.evidence.json"
                try {
                    $restoreEvidence = New-RestorePackageEvidence -ProbeName $entryName `
                        -PackageVersion $candidate.mcpSdkVersion -PackagesDirectory $packages `
                        -VerifiedPackagePath $retainedNuGet.PackagePath -CandidateSource $candidateNuGetSource `
                        -ConfigPath $candidateNuGetConfig -PublishedHashBase64 $verification.Verification.publishedHashBase64 `
                        -EvidencePath $restoreEvidencePath
                    foreach ($path in @($candidateNuGetConfig, $retainedNuGet.PackagePath, $restoreEvidencePath,
                        $restoreEvidence.restoredPackagePath, $restoreEvidence.restoredSha512Path, $restoreEvidence.restoredMetadataPath)) {
                        $ordinaryArtifacts[$path] = Get-Sha256 $path
                    }
                }
                catch {
                    Add-Content -LiteralPath $result.StderrPath -Value "Restore package evidence validation failed: $($_.Exception.Message)" -Encoding UTF8
                    $result.ExitCode = 1
                    $result.StderrSha256 = Get-Sha256 $result.StderrPath
                }
            }
            $ordinaryFailureStage = if ($entryName -in @('normal-restore', 'win-x64-restore', 'release-build')) { 'compile' } else { 'probe' }
            $case = New-CaseResult 'candidate-worktree' $entryName $result $ordinaryArtifacts $ordinaryFailureStage
            $probes += ,(New-ProbeResult $entryName $result @($case) $ordinaryArtifacts)
        }

        $goldenEvidencePath = Join-Path $candidateRoot 'golden-traceevent-reads.evidence.json'
        $goldenEnvironment = @{}
        foreach ($entry in $environment.GetEnumerator()) { $goldenEnvironment[$entry.Key] = $entry.Value }
        $goldenEnvironment['WPAMCP_PLATFORM_REQUIRED'] = '1'
        $goldenEnvironment['WPAMCP_PLATFORM_GOLDEN_EVIDENCE_PATH'] = $goldenEvidencePath
        $goldenResult = Invoke-CapturedCommand $dotnet (@(
            'test', 'tests\WpaMcp.Tests\WpaMcp.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
            '--filter', 'FullyQualifiedName~PlatformNonSdkRuntimeProbeTests.GoldenTraceEventReads_OpensEveryFixtureFromTemporaryCopy') +
            $properties + $msbuildIsolation) $workspace (Join-Path $logs 'golden-traceevent-reads') $goldenEnvironment `
            -TimeoutSeconds 180 -Stage 'golden-traceevent-reads'
        if ($goldenResult.ExitCode -eq 0 -and -not (Test-GoldenTraceEventEvidence -EvidencePath $goldenEvidencePath)) {
            Add-Content -LiteralPath $goldenResult.StderrPath -Value 'Golden TraceEvent evidence failed semantic validation.' -Encoding UTF8
            $goldenResult.ExitCode = 1
            $goldenResult.StderrSha256 = Get-Sha256 $goldenResult.StderrPath
        }
        $goldenArtifacts = @{}
        if (Test-Path -LiteralPath $goldenEvidencePath -PathType Leaf) {
            $goldenArtifacts[$goldenEvidencePath] = Get-Sha256 $goldenEvidencePath
        }
        $goldenCase = New-CaseResult 'candidate-worktree' 'golden-traceevent-reads' $goldenResult $goldenArtifacts 'probe'
        $probes += ,(New-ProbeResult 'golden-traceevent-reads' $goldenResult @($goldenCase) $goldenArtifacts)

        $serverPublish = Join-Path $publishes 'server-self-contained'
        $publishResult = Invoke-CapturedCommand $dotnet (@('publish', 'src\WpaMcp\WpaMcp.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $serverPublish) + $properties + $msbuildIsolation) $workspace (Join-Path $logs 'self-contained-publish') $environment
        $publishArtifacts = @{}
        if (Test-Path -LiteralPath $serverPublish) {
            Get-ChildItem -LiteralPath $serverPublish -Recurse -File | ForEach-Object { $publishArtifacts[$_.FullName] = Get-Sha256 $_.FullName }
        }
        $productionPublishRid = $null
        if ($publishResult.ExitCode -eq 0) {
            try {
                $serverDepsPath = Join-Path $serverPublish 'WpaMcp.deps.json'
                if (-not (Test-Path -LiteralPath $serverDepsPath -PathType Leaf)) { throw 'Self-contained publish omitted WpaMcp.deps.json.' }
                $serverDeps = Get-Content -LiteralPath $serverDepsPath -Raw | ConvertFrom-Json
                $runtimeTargetName = [string]$serverDeps.runtimeTarget.name
                if ($runtimeTargetName -cnotmatch '/win-x64$') { throw "Self-contained runtimeTarget was not /win-x64: '$runtimeTargetName'." }
                $productionPublishRid = 'win-x64'
                $publishedLeafNames = @(Get-ChildItem -LiteralPath $serverPublish -Recurse -File | ForEach-Object { $_.Name })
                foreach ($runtimeBinary in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
                    if ($runtimeBinary -cnotin $publishedLeafNames) { throw "Self-contained publish omitted $runtimeBinary." }
                }
            }
            catch {
                Add-Content -LiteralPath $publishResult.StderrPath -Value "Production publish evidence validation failed: $($_.Exception.Message)" -Encoding UTF8
                $publishResult.ExitCode = 1
                $publishResult.StderrSha256 = Get-Sha256 $publishResult.StderrPath
            }
        }
        $publishCase = New-CaseResult 'candidate-worktree' 'self-contained-publish' $publishResult $publishArtifacts 'publish'
        $probes += ,(New-ProbeResult 'self-contained-publish' $publishResult @($publishCase) $publishArtifacts)

        $serverExe = Join-Path $serverPublish 'WpaMcp.exe'
        $stdioEvidence = Join-Path $candidateRoot 'self-contained-stdio.evidence.json'
        $stdioRawStdout = Join-Path $candidateRoot 'self-contained-stdio.server.stdout.log'
        $stdioRawStderr = Join-Path $candidateRoot 'self-contained-stdio.server.stderr.log'
        if ($publishResult.ExitCode -eq 0 -and (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
            $stdioEnvironment = @{}
            foreach ($entry in $environment.GetEnumerator()) { $stdioEnvironment[$entry.Key] = $entry.Value }
            $stdioEnvironment['WPAMCP_PLATFORM_SERVER_PATH'] = $serverExe
            $stdioEnvironment['WPAMCP_PLATFORM_REQUIRED'] = '1'
            $stdioEnvironment['WPAMCP_PLATFORM_PUBLISH_ROOT'] = $serverPublish
            $stdioEnvironment['WPAMCP_PLATFORM_PROTOCOL_REVISION'] = $candidate.protocolRevision
            $stdioEnvironment['WPAMCP_PLATFORM_PROTOCOL_PROFILE'] = $candidate.protocolProfile
            $stdioEnvironment['WPAMCP_PLATFORM_EVIDENCE_PATH'] = $stdioEvidence
            $stdioEnvironment['WPAMCP_PLATFORM_RAW_STDOUT_PATH'] = $stdioRawStdout
            $stdioEnvironment['WPAMCP_PLATFORM_RAW_STDERR_PATH'] = $stdioRawStderr
            $stdioEnvironment['WPAMCP_PLATFORM_EXPECTED_LAUNCH_SHA256'] = Get-Sha256 $serverExe
            $stdioEnvironment['WPAMCP_PLATFORM_REQUESTED_RID'] = 'win-x64'
            $stdioEnvironment['WPAMCP_PLATFORM_PUBLISH_RID'] = $productionPublishRid
            $stdioResult = Invoke-CapturedCommand $dotnet (@(
                'test', 'tests\WpaMcp.Tests\WpaMcp.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
                '--filter', 'FullyQualifiedName~PlatformProductionStdioTests') + $properties + $msbuildIsolation) `
                $workspace (Join-Path $logs 'self-contained-stdio') $stdioEnvironment -TimeoutSeconds 60 -Stage 'self-contained-stdio'
            if ($stdioResult.ExitCode -eq 0 -and -not (Test-ProductionStdioEvidence -EvidencePath $stdioEvidence `
                -RawStdoutPath $stdioRawStdout -RawStderrPath $stdioRawStderr -ServerPath $serverExe `
                -PublishRoot $serverPublish -Candidate $candidate)) {
                Add-Content -LiteralPath $stdioResult.StderrPath -Value 'Production stdio evidence failed semantic validation.' -Encoding UTF8
                $stdioResult.ExitCode = 1
                $stdioResult.StderrSha256 = Get-Sha256 $stdioResult.StderrPath
            }
        }
        else {
            Set-Content -LiteralPath (Join-Path $logs 'self-contained-stdio.stdout.log') -Value '' -NoNewline
            Set-Content -LiteralPath (Join-Path $logs 'self-contained-stdio.stderr.log') -Value 'Self-contained publish failed or omitted WpaMcp.exe.'
            $stdioResult = [ordered]@{
                Command = "launch retained production stdio host $serverExe"
                ExitCode = 1
                StdoutSha256 = Get-Sha256 (Join-Path $logs 'self-contained-stdio.stdout.log')
                StderrSha256 = Get-Sha256 (Join-Path $logs 'self-contained-stdio.stderr.log')
            }
        }
        $stdioArtifacts = @{}
        foreach ($path in @($serverExe, $stdioEvidence, $stdioRawStdout, $stdioRawStderr)) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { $stdioArtifacts[$path] = Get-Sha256 $path }
        }
        $stdioFailureStage = if ($publishResult.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $serverExe -PathType Leaf)) { 'publish' }
            elseif (Test-Path -LiteralPath $stdioEvidence -PathType Leaf) { 'profile' }
            else { 'launch' }
        $stdioCase = New-CaseResult 'candidate-worktree' 'self-contained-stdio' $stdioResult $stdioArtifacts $stdioFailureStage
        $probes += ,(New-ProbeResult 'self-contained-stdio' $stdioResult @($stdioCase) $stdioArtifacts)

        $nativeEvidencePath = Join-Path $candidateRoot 'native-layout.evidence.json'
        $expectedMsdiaPath = Join-Path $serverPublish 'amd64\msdia140.dll'
        $expectedKernelTraceControlPath = Join-Path $serverPublish 'amd64\KernelTraceControl.dll'
        if ($publishResult.ExitCode -eq 0) {
            $nativeEnvironment = @{}
            foreach ($entry in $environment.GetEnumerator()) { $nativeEnvironment[$entry.Key] = $entry.Value }
            $nativeEnvironment['WPAMCP_PLATFORM_REQUIRED'] = '1'
            $nativeEnvironment['WPAMCP_PLATFORM_PUBLISH_ROOT'] = $serverPublish
            $nativeEnvironment['WPAMCP_PLATFORM_NATIVE_EVIDENCE_PATH'] = $nativeEvidencePath
            $nativeResult = Invoke-CapturedCommand $dotnet (@(
                'test', 'tests\WpaMcp.Tests\WpaMcp.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
                '--filter', 'FullyQualifiedName~PlatformNonSdkRuntimeProbeTests.NativeLayout_LoadsExactProductionAmd64Dependencies') +
                $properties + $msbuildIsolation) $workspace (Join-Path $logs 'native-layout') $nativeEnvironment `
                -TimeoutSeconds 60 -Stage 'native-layout'
            if ($nativeResult.ExitCode -eq 0 -and
                -not (Test-NativeLayoutEvidence -EvidencePath $nativeEvidencePath -PublishRoot $serverPublish)) {
                Add-Content -LiteralPath $nativeResult.StderrPath -Value 'Native layout evidence failed semantic validation.' -Encoding UTF8
                $nativeResult.ExitCode = 1
                $nativeResult.StderrSha256 = Get-Sha256 $nativeResult.StderrPath
            }
        }
        else {
            Set-Content -LiteralPath (Join-Path $logs 'native-layout.stdout.log') -Value '' -NoNewline
            Set-Content -LiteralPath (Join-Path $logs 'native-layout.stderr.log') -Value 'Self-contained publish failed before native layout probe.'
            $nativeResult = [ordered]@{
                Command = 'load exact production amd64 native dependencies'
                ExitCode = 1
                StdoutSha256 = Get-Sha256 (Join-Path $logs 'native-layout.stdout.log')
                StderrSha256 = Get-Sha256 (Join-Path $logs 'native-layout.stderr.log')
            }
        }
        $nativeArtifacts = @{}
        foreach ($path in @($serverExe, $expectedMsdiaPath, $expectedKernelTraceControlPath, $nativeEvidencePath)) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { $nativeArtifacts[$path] = Get-Sha256 $path }
        }
        $nativeCase = New-CaseResult 'candidate-worktree' 'native-layout' $nativeResult $nativeArtifacts 'publish'
        $probes += ,(New-ProbeResult 'native-layout' $nativeResult @($nativeCase) $nativeArtifacts)

        $diaRoot = Join-Path $candidateRoot 'dia-probe'
        $diaEvidencePath = Join-Path $candidateRoot 'windows-dia-pdb-resolution.evidence.json'
        if ($publishResult.ExitCode -eq 0 -and (Test-Path -LiteralPath $expectedMsdiaPath -PathType Leaf)) {
            $diaEnvironment = @{}
            foreach ($entry in $environment.GetEnumerator()) { $diaEnvironment[$entry.Key] = $entry.Value }
            $diaEnvironment['WPAMCP_PLATFORM_REQUIRED'] = '1'
            $diaEnvironment['WPAMCP_PLATFORM_DIA_ROOT'] = $diaRoot
            $diaEnvironment['WPAMCP_PLATFORM_DIA_EVIDENCE_PATH'] = $diaEvidencePath
            $diaEnvironment['WPAMCP_PLATFORM_MSDIA_PATH'] = $expectedMsdiaPath
            $diaResult = Invoke-CapturedCommand $dotnet (@(
                'test', 'tests\WpaMcp.Tests\WpaMcp.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
                '--filter', 'FullyQualifiedName~PlatformNonSdkRuntimeProbeTests.WindowsDiaPdbResolution_EnumeratesFunctionAndResolvesItsRva') +
                $properties + $msbuildIsolation) $workspace (Join-Path $logs 'windows-dia-pdb-resolution') $diaEnvironment `
                -TimeoutSeconds 60 -Stage 'windows-dia-pdb-resolution'
            if ($diaResult.ExitCode -eq 0 -and
                -not (Test-WindowsDiaEvidence -EvidencePath $diaEvidencePath -CandidateRoot $candidateRoot -MsdiaPath $expectedMsdiaPath)) {
                Add-Content -LiteralPath $diaResult.StderrPath -Value 'Windows DIA evidence failed semantic validation.' -Encoding UTF8
                $diaResult.ExitCode = 1
                $diaResult.StderrSha256 = Get-Sha256 $diaResult.StderrPath
            }
        }
        else {
            Set-Content -LiteralPath (Join-Path $logs 'windows-dia-pdb-resolution.stdout.log') -Value '' -NoNewline
            Set-Content -LiteralPath (Join-Path $logs 'windows-dia-pdb-resolution.stderr.log') -Value 'Self-contained publish failed or omitted amd64/msdia140.dll.'
            $diaResult = [ordered]@{
                Command = 'open native PDB with DIA and resolve enumerated function RVA'
                ExitCode = 1
                StdoutSha256 = Get-Sha256 (Join-Path $logs 'windows-dia-pdb-resolution.stdout.log')
                StderrSha256 = Get-Sha256 (Join-Path $logs 'windows-dia-pdb-resolution.stderr.log')
            }
        }
        $diaArtifacts = @{}
        foreach ($path in @($expectedMsdiaPath, (Join-Path $diaRoot 'platform-dia-probe.dll'),
            (Join-Path $diaRoot 'platform-dia-probe.pdb'), $diaEvidencePath)) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { $diaArtifacts[$path] = Get-Sha256 $path }
        }
        $diaCase = New-CaseResult 'candidate-worktree' 'windows-dia-pdb-resolution' $diaResult $diaArtifacts 'probe'
        $probes += ,(New-ProbeResult 'windows-dia-pdb-resolution' $diaResult @($diaCase) $diaArtifacts)

        $probeProject = 'tools\sdkcandidateprobe\sdkcandidateprobe.csproj'
        $normalPublish = Join-Path $publishes 'sdk-probe-normal'
        $fdPublish = Join-Path $publishes 'sdk-probe-framework-dependent'
        $scPublish = Join-Path $publishes 'sdk-probe-self-contained'
        $normalResult = Invoke-CapturedCommand $dotnet (@('publish', $probeProject, '-c', 'Release', '--self-contained', 'false', '-o', $normalPublish) + $properties + $msbuildIsolation) $workspace (Join-Path $logs 'sdk-probe-publish-normal') $environment
        $fdResult = Invoke-CapturedCommand $dotnet (@('publish', $probeProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $fdPublish) + $properties + $msbuildIsolation) $workspace (Join-Path $logs 'sdk-probe-publish-framework-dependent') $environment
        $scResult = Invoke-CapturedCommand $dotnet (@('publish', $probeProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $scPublish) + $properties + $msbuildIsolation) $workspace (Join-Path $logs 'sdk-probe-publish-self-contained') $environment
        $hosts = [ordered]@{
            normal = (Join-Path $normalPublish 'sdkcandidateprobe.exe')
            'win-x64-framework-dependent' = (Join-Path $fdPublish 'sdkcandidateprobe.exe')
            'win-x64-self-contained' = (Join-Path $scPublish 'sdkcandidateprobe.exe')
        }
        $hostPublishes = [ordered]@{
            normal = [ordered]@{ Root = $normalPublish; Result = $normalResult }
            'win-x64-framework-dependent' = [ordered]@{ Root = $fdPublish; Result = $fdResult }
            'win-x64-self-contained' = [ordered]@{ Root = $scPublish; Result = $scResult }
        }
        $publishManifestRoot = Join-Path $candidateRoot 'publish-manifests'
        New-FreshDirectory $publishManifestRoot
        $hostManifests = [ordered]@{}
        foreach ($hostMode in @($matrix.sdkSurfaceHostModes)) {
            $hostManifests[$hostMode] = $null
        }
        $sdkCases = @{}
        foreach ($probeName in @($matrix.sdkSurfaceProbeNames)) {
            $caseList = @()
            foreach ($hostMode in @($matrix.sdkSurfaceHostModes)) {
                $hostPath = $hosts[$hostMode]
                $hostPublish = $hostPublishes[$hostMode]
                $hostManifest = $hostManifests[$hostMode]
                $evidence = Join-Path $candidateRoot "$probeName.$hostMode.evidence.json"
                $caseLog = Join-Path $logs "$probeName.$hostMode"
                if ($hostPublish.Result.ExitCode -eq 0 -and (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
                    $caseResult = Invoke-CapturedCommand $hostPath @(
                        '--run-suite', '--host-mode', $hostMode, '--host-command', $hostPath,
                        '--protocol-revision', $candidate.protocolRevision, '--protocol-profile', $candidate.protocolProfile,
                        '--evidence', $evidence) $workspace $caseLog $environment
                }
                else {
                    Set-Content -LiteralPath "$caseLog.stdout.log" -Value '' -NoNewline
                    Set-Content -LiteralPath "$caseLog.stderr.log" -Value "Publish failed or omitted retained host $hostPath"
                    $caseResult = [ordered]@{ Command = $hostPath; ExitCode = 1; StdoutSha256 = Get-Sha256 "$caseLog.stdout.log"; StderrSha256 = Get-Sha256 "$caseLog.stderr.log" }
                }
                if ($null -eq $hostManifest) {
                    $hostManifest = Complete-SdkCaseRuntimeManifest -HostMode $hostMode -PublishRoot $hostPublish.Root `
                        -HostPath $hostPath -PublishResult $hostPublish.Result -CaseResult $caseResult `
                        -ManifestPath (Join-Path $publishManifestRoot "$hostMode.json") `
                        -RuntimeEvidencePath $evidence -DotNetRoot $environment.DOTNET_ROOT
                    $hostManifests[$hostMode] = $hostManifest
                    if ($caseResult.ExitCode -eq 0 -and $null -eq $hostManifest) {
                        Add-Content -LiteralPath $caseResult.StderrPath -Value 'Runtime-bound publish manifest finalization failed.' -Encoding UTF8
                        $caseResult.ExitCode = 1
                        $caseResult.StderrSha256 = Get-Sha256 $caseResult.StderrPath
                    }
                }
                $artifacts = @{}
                $hostManifestPath = if ($null -ne $hostManifest) { $hostManifest.Path } else { $null }
                $retainedHostFxrPath = if ($null -ne $hostManifest -and $null -ne $hostManifest.Manifest.frameworkRuntime) {
                    $hostManifest.Manifest.frameworkRuntime.retainedHostFxrPath
                } else { $null }
                $retainedHostPolicyPath = if ($null -ne $hostManifest -and $null -ne $hostManifest.Manifest.frameworkRuntime) {
                    $hostManifest.Manifest.frameworkRuntime.retainedHostPolicyPath
                } else { $null }
                foreach ($path in @($hostPath, $evidence, $hostManifestPath, $retainedHostFxrPath, $retainedHostPolicyPath, $hostPublish.Result.StdoutPath, $hostPublish.Result.StderrPath)) {
                    if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) { $artifacts[$path] = Get-Sha256 $path }
                }
                $sdkFailureStage = Get-SdkCaseFailureStage -HostPath $hostPath -PublishResult $hostPublish.Result `
                    -CaseResult $caseResult -HostManifest $hostManifest
                $caseList += ,(New-CaseResult $hostMode $probeName $caseResult $artifacts $sdkFailureStage)
            }
            $sdkCases[$probeName] = @($caseList)
            $parentExit = if (@($caseList | Where-Object { -not $_.passed }).Count -eq 0) { 0 } else { 1 }
            $parent = [ordered]@{ Command = "sdkcandidateprobe --run-suite $probeName"; ExitCode = $parentExit; StdoutSha256 = $caseList[0].stdoutSha256; StderrSha256 = $caseList[0].stderrSha256 }
            $sdkAggregateArtifacts = @{}
            foreach ($sdkCase in $caseList) {
                foreach ($artifact in $sdkCase.artifactSha256.GetEnumerator()) {
                    if ($sdkAggregateArtifacts.ContainsKey($artifact.Key) -and $sdkAggregateArtifacts[$artifact.Key] -cne $artifact.Value) {
                        throw "SDK case artifact hash disagreed for '$($artifact.Key)'."
                    }
                    $sdkAggregateArtifacts[$artifact.Key] = $artifact.Value
                }
            }
            $probes += ,(New-ProbeResult $probeName $parent @($caseList) $sdkAggregateArtifacts)
        }

        $schemaLog = Join-Path $logs 'tools-list-output-schema'
        $schemaEvidence = @($sdkCases['cancellation-progress-injection-schema'] | ForEach-Object { $_.artifactSha256 })
        Write-NewUtf8File "$schemaLog.stdout.log" ($schemaEvidence | ConvertTo-Json -Depth 6)
        Write-NewUtf8File "$schemaLog.stderr.log" ''
        $schemaExit = if (@($sdkCases['cancellation-progress-injection-schema'] | Where-Object { -not $_.passed }).Count -eq 0) { 0 } else { 1 }
        $schemaResult = [ordered]@{ Command = 'verify retained tools/list schema evidence'; ExitCode = $schemaExit; StdoutSha256 = Get-Sha256 "$schemaLog.stdout.log"; StderrSha256 = Get-Sha256 "$schemaLog.stderr.log" }
        $schemaArtifacts = @{}
        foreach ($sdkCase in @($sdkCases['cancellation-progress-injection-schema'])) {
            foreach ($artifact in $sdkCase.artifactSha256.GetEnumerator()) {
                if ($schemaArtifacts.ContainsKey($artifact.Key) -and $schemaArtifacts[$artifact.Key] -cne $artifact.Value) {
                    throw "Schema case artifact hash disagreed for '$($artifact.Key)'."
                }
                $schemaArtifacts[$artifact.Key] = $artifact.Value
            }
        }
        $schemaCase = New-CaseResult 'candidate-worktree' 'tools-list-output-schema' $schemaResult $schemaArtifacts
        $probes += ,(New-ProbeResult 'tools-list-output-schema' $schemaResult @($schemaCase) $schemaArtifacts)

        $architectureLog = Join-Path $logs 'windows-architecture-matrix'
        $architectureEvidencePath = Join-Path $candidateRoot 'windows-architecture-matrix.evidence.json'
        $architectureSourceSpecs = @(
            [ordered]@{ Component = 'golden-traceevent-reads'; EvidencePath = $goldenEvidencePath; Kind = 'direct' }
            [ordered]@{ Component = 'windows-dia-pdb-resolution'; EvidencePath = $diaEvidencePath; Kind = 'direct' }
            [ordered]@{ Component = 'native-layout'; EvidencePath = $nativeEvidencePath; Kind = 'direct' }
            [ordered]@{ Component = 'self-contained-stdio'; EvidencePath = $stdioEvidence; Kind = 'production-stdio' }
            [ordered]@{ Component = 'sdk-normal'; EvidencePath = (Join-Path $candidateRoot 'selected-profile-handshake.normal.evidence.json'); Kind = 'sdk' }
            [ordered]@{ Component = 'sdk-win-x64-framework-dependent'; EvidencePath = (Join-Path $candidateRoot 'selected-profile-handshake.win-x64-framework-dependent.evidence.json'); Kind = 'sdk' }
            [ordered]@{ Component = 'sdk-win-x64-self-contained'; EvidencePath = (Join-Path $candidateRoot 'selected-profile-handshake.win-x64-self-contained.evidence.json'); Kind = 'sdk' }
        )
        $architectureObservations = @()
        $architectureArtifacts = @{}
        $sourceArchitecturesPassed = $true
        foreach ($sourceSpec in $architectureSourceSpecs) {
            $sourceArchitecture = ''
            $sourceHash = ''
            if (Test-Path -LiteralPath $sourceSpec.EvidencePath -PathType Leaf) {
                try {
                    $sourceEvidence = Get-Content -LiteralPath $sourceSpec.EvidencePath -Raw | ConvertFrom-Json
                    $sourceArchitecture = switch ($sourceSpec.Kind) {
                        'direct' { [string]$sourceEvidence.processArchitecture }
                        'production-stdio' { [string]$sourceEvidence.launch.childProcessArchitecture }
                        'sdk' { [string]$sourceEvidence.launchIdentity.runtimeIdentity.processArchitecture }
                    }
                    $sourceHash = Get-Sha256 $sourceSpec.EvidencePath
                    $architectureArtifacts[$sourceSpec.EvidencePath] = $sourceHash
                }
                catch { $sourceArchitecturesPassed = $false }
            }
            else { $sourceArchitecturesPassed = $false }
            if ($sourceArchitecture -cne 'X64') { $sourceArchitecturesPassed = $false }
            $architectureObservations += ,[ordered]@{
                component = $sourceSpec.Component
                evidencePath = $sourceSpec.EvidencePath
                evidenceSha256 = $sourceHash
                processArchitecture = $sourceArchitecture
            }
        }
        $runnerIsWindows = $env:OS -ceq 'Windows_NT'
        $architectureRunner = [ordered]@{
            osPlatform = if ($runnerIsWindows) { 'Windows' } else { 'Other' }
            osDescription = [Runtime.InteropServices.RuntimeInformation]::OSDescription
            osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            runnerImage = [string]$env:ImageOS
        }
        $architecturePassed = $runnerIsWindows -and $architectureRunner.osArchitecture -ceq 'X64' -and
            $architectureRunner.processArchitecture -ceq 'X64' -and $sourceArchitecturesPassed
        $architectureEvidence = [ordered]@{
            schemaVersion = '1.0'
            probeName = 'windows-architecture-matrix'
            expected = @($matrix.windowsArchitectureMatrix)
            runner = $architectureRunner
            observations = @($architectureObservations)
            passed = $architecturePassed
        }
        if ($architecturePassed -and -not (Test-WindowsArchitectureEvidence -Evidence $architectureEvidence -CandidateRoot $candidateRoot -Matrix $matrix)) {
            $architecturePassed = $false
            $architectureEvidence.passed = $false
        }
        Write-NewUtf8File $architectureEvidencePath ($architectureEvidence | ConvertTo-Json -Depth 8)
        $architectureArtifacts[$architectureEvidencePath] = Get-Sha256 $architectureEvidencePath
        Write-NewUtf8File "$architectureLog.stdout.log" ($architectureEvidence | ConvertTo-Json -Depth 8)
        Write-NewUtf8File "$architectureLog.stderr.log" ''
        $architectureResult = [ordered]@{ Command = 'inspect Windows/X64 OS and process architecture'; ExitCode = if ($architecturePassed) { 0 } else { 1 }; StdoutSha256 = Get-Sha256 "$architectureLog.stdout.log"; StderrSha256 = Get-Sha256 "$architectureLog.stderr.log" }
        $architectureCase = New-CaseResult 'candidate-worktree' 'windows-architecture-matrix' $architectureResult $architectureArtifacts
        $probes += ,(New-ProbeResult 'windows-architecture-matrix' $architectureResult @($architectureCase) $architectureArtifacts)

        $ordered = foreach ($requiredName in @($matrix.requiredProbeNames)) { @($probes | Where-Object name -eq $requiredName)[0] }
        $result = [ordered]@{
            schemaVersion = '1.0'
            candidateId = $candidate.id
            sdkVersion = $candidate.sdkVersion
            targetFramework = $candidate.targetFramework
            mcpSdkVersion = $candidate.mcpSdkVersion
            protocolRevision = $candidate.protocolRevision
            protocolProfile = $candidate.protocolProfile
            commit = $commit
            startedUtc = $startedUtc.ToString('o')
            completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
            probes = @($ordered)
        }
        Write-NewUtf8File $resultPath ($result | ConvertTo-Json -Depth 12)
        if (-not (Test-PlatformCandidateResult -Path $resultPath -CandidateId $CandidateId)) { throw 'Generated candidate result failed its strict contract.' }
        if (@($result.probes | Where-Object { -not $_.passed }).Count -gt 0) { return 1 }
        return 0
    }
    finally {
        if (Test-Path -LiteralPath $workspace) {
            $resolvedWorkspace = (Resolve-Path -LiteralPath $workspace).Path
            $expectedWorkspace = Join-Path (Resolve-Path -LiteralPath $candidateRoot).Path 'worktree'
            if ($resolvedWorkspace -cne $expectedWorkspace) {
                throw "Refusing to clean unexpected candidate workspace '$resolvedWorkspace'."
            }
            Remove-Item -LiteralPath $resolvedWorkspace -Recurse -Force
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-PlatformCandidate)
}
