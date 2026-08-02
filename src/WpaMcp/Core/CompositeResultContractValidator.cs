using WpaMcp.Output;

namespace WpaMcp.Core;

internal static class CompositeResultContractValidator
{
    private static readonly HashSet<string> CompositeToolNames = new(StringComparer.Ordinal)
    {
        "diagnose_high_wait",
        "diagnose_slow_startup",
        "diagnose_window",
    };

    internal static bool IsCompositeTool(string toolName) =>
        CompositeToolNames.Contains(toolName);

    internal static void Validate(object data)
    {
        switch (data)
        {
            case DiagnoseHighWaitResponse highWait:
                ValidateHighWait(highWait);
                break;
            case DiagnoseSlowStartupResponse slowStartup:
                ValidateSlowStartup(slowStartup);
                break;
            case DiagnoseWindowResponse window:
                ValidateWindow(window, "diagnose_window");
                break;
        }
    }

    private static void ValidateHighWait(DiagnoseHighWaitResponse response)
    {
        ValidateEmbeddedBoundary(
            response.CandidateBoundary,
            "/candidates",
            response.Candidates.Count,
            "diagnose_high_wait.candidateBoundary");
        if (string.Equals(response.ScopeStatus, "ok", StringComparison.Ordinal)
                != (response.CandidateBoundary.TotalState == ToolSectionTotalState.Exact))
        {
            throw new InvalidOperationException(
                "diagnose_high_wait candidate total must be exact exactly when scope resolution succeeded.");
        }
        var calls = ValidateCommon(
            response.ExecutedToolCalls,
            response.Evidence,
            response.NotConcluded,
            "diagnose_high_wait");
        var hasTimeBudgetBoundary = response.NotConcluded.Any(item =>
            string.Equals(item.Code, "time_budget_exhausted", StringComparison.Ordinal));
        if (response.Partial != hasTimeBudgetBoundary ||
            response.Partial != string.Equals(
                response.PartialCode,
                "time_budget_exhausted",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "diagnose_high_wait partial state must exactly match its typed time-budget boundary.");
        }
        foreach (var candidate in response.Candidates)
        {
            RequireCall(calls, candidate.WaitAnalysisCallId, "candidate.waitAnalysisCallId");
            RequireOptionalCall(calls, candidate.WaitStacksCallId, "candidate.waitStacksCallId");
            RequireOptionalCall(calls, candidate.ReadyThreadCallId, "candidate.readyThreadCallId");
        }
    }

    private static void ValidateSlowStartup(DiagnoseSlowStartupResponse response)
    {
        ValidateEmbeddedBoundary(
            response.CandidateBoundary,
            "/candidates",
            response.Candidates.Count,
            "diagnose_slow_startup.candidateBoundary");
        var calls = ValidateCommon(
            response.ExecutedToolCalls ?? Array.Empty<CompositeToolCall>(),
            response.Evidence ?? Array.Empty<CompositeEvidence>(),
            response.NotConcluded ?? Array.Empty<CompositeNotConcluded>(),
            "diagnose_slow_startup");
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in response.Candidates)
        {
            RequireUniqueId(evidenceIds, candidate.EvidenceId, "candidate.evidenceId");
            RequireCall(calls, candidate.CallId, "candidate.callId");
            ValidateEmbeddedBoundary(
                candidate.TopStartupWaitReasonsBoundary,
                "/topStartupWaitReasons",
                candidate.TopStartupWaitReasons.Count,
                "candidate.topStartupWaitReasonsBoundary");
            if (candidate.TopStartupWaitReasonsBoundary.TotalAvailable is not { } waitReasonTotal ||
                waitReasonTotal < candidate.TopStartupWaitReasons.Count)
            {
                throw new InvalidOperationException(
                    "diagnose_slow_startup candidate wait-reason boundary omitted its exact pre-cap total.");
            }
            ValidateEmbeddedBoundary(
                candidate.FirstStartupImageLoadsBoundary,
                "/firstStartupImageLoads",
                candidate.FirstStartupImageLoads.Count,
                "candidate.firstStartupImageLoadsBoundary");
            if (candidate.FirstStartupImageLoadsBoundary.TotalAvailable !=
                    candidate.StartupImageLoadCount ||
                candidate.FirstStartupImageLoadsBoundary.HasMore !=
                    candidate.StartupImageLoadsHasMore)
            {
                throw new InvalidOperationException(
                    "diagnose_slow_startup candidate image-load boundary contradicts its exact total or compatibility flag.");
            }
            ValidateEmbeddedBoundary(
                candidate.TopStartupCpuFunctionsBoundary,
                "/topStartupCpuFunctions",
                candidate.TopStartupCpuFunctions?.Count ?? 0,
                "candidate.topStartupCpuFunctionsBoundary");
        }
        foreach (var evidence in response.Evidence ?? Array.Empty<CompositeEvidence>())
            RequireUniqueId(evidenceIds, evidence.EvidenceId, "evidence.evidenceId");
        foreach (var gap in response.FirstImageLoadGapEvidence ?? Array.Empty<StartupGapEvidenceRow>())
        {
            RequireUniqueId(evidenceIds, gap.EvidenceId, "firstImageLoadGapEvidence.evidenceId");
            RequireCall(calls, gap.CallId, "firstImageLoadGapEvidence.callId");
            ValidateWindow(gap.Window, "diagnose_slow_startup.firstImageLoadGapEvidence.window");
            var expected = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["/hardFaultsByBytes"] = gap.Window.HardFaultsByBytes.Count,
                ["/hardFaultsByMaxLatency"] = gap.Window.HardFaultsByMaxLatency.Count,
                ["/fileIoTopFiles"] = gap.Window.FileIoTopFiles.Count,
                ["/securityScanTargets"] = gap.Window.SecurityScanTargets.Count,
                ["/slowScans"] = gap.Window.SlowScans.Count,
                ["/waits"] = gap.Window.Waits.Count,
                ["/pressure/topPeakWorkingSetProcesses"] =
                    gap.Window.Pressure?.TopPeakWorkingSetProcesses.Count ?? 0,
                ["/pressure/topPeakCommitProcesses"] =
                    gap.Window.Pressure?.TopPeakCommitProcesses.Count ?? 0,
            };
            if (gap.WindowSectionBoundaries.Count != expected.Count ||
                gap.WindowSectionBoundaries.Select(item => item.SectionPointer)
                    .Distinct(StringComparer.Ordinal).Count() != expected.Count)
            {
                throw new InvalidOperationException(
                    "diagnose_slow_startup gap evidence must expose exactly eight unique embedded window boundaries.");
            }
            foreach (var boundary in gap.WindowSectionBoundaries)
            {
                if (!expected.TryGetValue(boundary.SectionPointer, out var returned))
                {
                    throw new InvalidOperationException(
                        $"diagnose_slow_startup gap evidence exposes unknown boundary '{boundary.SectionPointer}'.");
                }
                ValidateEmbeddedBoundary(
                    boundary,
                    boundary.SectionPointer,
                    returned,
                    "firstImageLoadGapEvidence.windowSectionBoundaries");
            }
        }
        foreach (var exclusion in response.Discovery?.ExcludedSamples ?? Array.Empty<StartupProcessExclusionRow>())
        {
            RequireUniqueId(evidenceIds, exclusion.EvidenceId, "discovery.excludedSamples.evidenceId");
            RequireCall(calls, exclusion.CallId, "discovery.excludedSamples.callId");
        }
        if (response.Discovery is { } discovery)
        {
            RequireCall(calls, discovery.CallId, "discovery.callId");
            ValidateEmbeddedBoundary(
                discovery.CandidateInputBoundary,
                "/discovery/candidateInput",
                discovery.ConsideredStartupInstanceCount,
                "discovery.candidateInputBoundary");
            var exactExcludedTotal = checked(
                discovery.ExcludedUnobservedStartCount +
                discovery.OtherExcludedStartupInstanceCount);
            if (discovery.ExcludedStartupInstanceCount != exactExcludedTotal ||
                discovery.CandidateInputBoundary.TotalAvailable !=
                    discovery.EligibleStartupInstanceCount ||
                discovery.CandidateInputBoundary.HasMore != discovery.CandidateInputHasMore ||
                discovery.ExcludedSamples.Count > StartupDiscoverySummary.ExcludedSampleLimit ||
                discovery.ExcludedSamplesHasMore !=
                    (exactExcludedTotal > discovery.ExcludedSamples.Count))
            {
                throw new InvalidOperationException(
                    "diagnose_slow_startup discovery exclusion totals or sample state are inconsistent.");
            }
            if (discovery.CandidateInputHasMore !=
                (response.CandidateBoundary.TotalState == ToolSectionTotalState.Unknown))
            {
                throw new InvalidOperationException(
                    "diagnose_slow_startup candidate total must become unknown exactly when discovery input is truncated.");
            }
        }
    }

    private static void ValidateEmbeddedBoundary(
        EmbeddedTopNBoundary boundary,
        string pointer,
        int returned,
        string context)
    {
        if (!string.Equals(boundary.SectionPointer, pointer, StringComparison.Ordinal) ||
            boundary.Requested < 0 || boundary.Returned != returned ||
            boundary.Returned > boundary.Requested || boundary.ContinuationAvailable ||
            boundary.HasMore != (boundary.MoreState == ToolSectionMoreState.Present))
        {
            throw new InvalidOperationException($"{context} has inconsistent identity or counts.");
        }
        if (boundary.TotalState == ToolSectionTotalState.Exact)
        {
            if (boundary.TotalAvailable is null || boundary.TotalAvailable < boundary.Returned ||
                boundary.MoreState == ToolSectionMoreState.Unknown ||
                (boundary.HasMore != (boundary.TotalAvailable > boundary.Returned)))
            {
                throw new InvalidOperationException($"{context} has inconsistent exact-total state.");
            }
        }
        else if (boundary.TotalState == ToolSectionTotalState.Unknown)
        {
            if (boundary.TotalAvailable is not null ||
                boundary.MoreState != ToolSectionMoreState.Unknown ||
                string.IsNullOrWhiteSpace(boundary.TruncationReason))
            {
                throw new InvalidOperationException($"{context} has inconsistent unknown-total state.");
            }
        }
        else if (boundary.TotalState == ToolSectionTotalState.LowerBound)
        {
            if (boundary.TotalAvailable is null ||
                boundary.TotalAvailable <= boundary.Returned ||
                boundary.MoreState != ToolSectionMoreState.Present ||
                !boundary.HasMore || string.IsNullOrWhiteSpace(boundary.TruncationReason))
            {
                throw new InvalidOperationException($"{context} has an unwitnessed lower-bound state.");
            }
        }
        else
        {
            throw new InvalidOperationException($"{context} has an unsupported total state.");
        }
    }

    private static void ValidateWindow(DiagnoseWindowResponse response, string context)
    {
        var calls = ValidateCommon(
            response.ExecutedToolCalls,
            Array.Empty<CompositeEvidence>(),
            response.NotConcluded,
            context);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in response.Evidence)
        {
            RequireUniqueId(
                evidenceIds,
                evidence.EvidenceId ?? throw new InvalidOperationException(
                    $"{context} evidence omitted evidenceId."),
                "evidence.evidenceId");
            RequireCall(
                calls,
                evidence.CallId ?? throw new InvalidOperationException(
                    $"{context} evidence omitted callId."),
                "evidence.callId");
            if (evidence.EvidenceType == "wait_summary")
            {
                ValidateEmbeddedBoundary(
                    evidence.DetailsBoundary ?? throw new InvalidOperationException(
                        $"{context} {evidence.EvidenceType} omitted DetailsBoundary."),
                    "/details",
                    evidence.Details.Count,
                    $"{context}.evidence.detailsBoundary");
                RequireNoStructuredSamples(evidence, context);
            }
            else if (evidence.EvidenceType is
                     "security_scan_duration" or "security_scan_presence")
            {
                if (evidence.DetailsBoundary is not null)
                {
                    throw new InvalidOperationException(
                        $"{context} {evidence.EvidenceType} must keep exhaustive annotations unbounded.");
                }

                ValidateEmbeddedBoundary(
                    evidence.SamplesBoundary ?? throw new InvalidOperationException(
                        $"{context} {evidence.EvidenceType} omitted SamplesBoundary."),
                    "/samples",
                    evidence.Samples.Count,
                    $"{context}.evidence.samplesBoundary");
                foreach (var sample in evidence.Samples)
                {
                    if (sample.Representative || sample.MetricAttributable ||
                        !string.Equals(
                            sample.SampleScope,
                            "returned_rows_only",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"{context} {evidence.EvidenceType} sample overstates its evidence scope.");
                    }
                }
            }
            else if (evidence.DetailsBoundary is { } detailsBoundary)
            {
                ValidateEmbeddedBoundary(
                    detailsBoundary,
                    "/details",
                    evidence.Details.Count,
                    $"{context}.evidence.detailsBoundary");
                RequireNoStructuredSamples(evidence, context);
            }
            else
            {
                RequireNoStructuredSamples(evidence, context);
            }
        }
    }

    private static void RequireNoStructuredSamples(
        WindowEvidenceRow evidence,
        string context)
    {
        if (evidence.Samples.Count != 0 || evidence.SamplesBoundary is not null)
        {
            throw new InvalidOperationException(
                $"{context} {evidence.EvidenceType} must not publish structured samples.");
        }
    }

    private static HashSet<string> ValidateCommon(
        IReadOnlyList<CompositeToolCall> executedCalls,
        IReadOnlyList<CompositeEvidence> evidence,
        IReadOnlyList<CompositeNotConcluded> notConcluded,
        string context)
    {
        var calls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in executedCalls)
            RequireUniqueId(calls, call.CallId, $"{context}.executedToolCalls.callId");

        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            RequireUniqueId(evidenceIds, item.EvidenceId, $"{context}.evidence.evidenceId");
            RequireCall(calls, item.CallId, $"{context}.evidence.callId");
            ValidateEmbeddedBoundary(
                item.FramesBoundary ?? throw new InvalidOperationException(
                    $"{context} evidence '{item.EvidenceId}' omitted FramesBoundary."),
                "/frames",
                item.Frames.Count,
                $"{context}.evidence.framesBoundary");
            ValidateEmbeddedBoundary(
                item.TopWaitReasonsBoundary ?? throw new InvalidOperationException(
                    $"{context} evidence '{item.EvidenceId}' omitted TopWaitReasonsBoundary."),
                "/topWaitReasons",
                item.TopWaitReasons.Count,
                $"{context}.evidence.topWaitReasonsBoundary");
            var stackEvidence = item.EvidenceType is
                "wait_stack_summary" or "ready_thread_stack_summary";
            if ((stackEvidence && item.TopWaitReasons.Count != 0) ||
                (!stackEvidence && item.Frames.Count != 0))
            {
                throw new InvalidOperationException(
                    $"{context} evidence '{item.EvidenceId}' contradicts its evidence-type collection contract.");
            }
        }

        var boundaryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in notConcluded)
        {
            RequireOptionalCall(calls, item.RelatedCallId, $"{context}.notConcluded.relatedCallId");
            if (item.BoundaryId is { } boundaryId)
                RequireUniqueId(boundaryIds, boundaryId, $"{context}.notConcluded.boundaryId");
        }
        return calls;
    }

    private static void RequireOptionalCall(
        IReadOnlySet<string> calls,
        string? callId,
        string field)
    {
        if (callId is not null)
            RequireCall(calls, callId, field);
    }

    private static void RequireCall(
        IReadOnlySet<string> calls,
        string callId,
        string field)
    {
        if (string.IsNullOrWhiteSpace(callId) || !calls.Contains(callId))
            throw new InvalidOperationException($"Composite {field} references an absent executedToolCalls.callId.");
    }

    private static void RequireUniqueId(
        HashSet<string> ids,
        string id,
        string field)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Composite {field} cannot be empty.");
        if (!ids.Add(id))
            throw new InvalidOperationException($"Composite {field} must be unique.");
    }
}
