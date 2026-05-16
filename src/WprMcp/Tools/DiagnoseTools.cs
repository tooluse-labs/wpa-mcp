using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class DiagnoseTools
{
    // High enough to avoid flagging PID 4 for normal kernel background churn, low enough
    // to keep kernel/minifilter-heavy traces from silently disappearing from top candidates.
    private const double SignificantSystemBlockedPct = 0.25;
    // Preview fan-out should stay conservative: ReadyThread stacks are useful when scheduler
    // waits dominate, but can drown the evidence chain when they are only a minority signal.
    private const double ReadyThreadSchedulerThresholdPct = 0.50;

    private readonly TraceCache _cache;
    public DiagnoseTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Composite 'why is process X slow to start' analysis. Picks the slowest-by-wait-ratio processes " +
        "(or the ones matching nameSubstring), then runs wait_analysis (top wait reasons), image_load_timing " +
        "(first N DLLs from process start), and cpu_top_functions (top hot functions in the startup window) " +
        "for each. Equivalent to manually composing list_processes + wait_analysis + image_load_timing + " +
        "cpu_top_functions but with a single tool call.")]
    public DiagnoseSlowStartupResponse DiagnoseSlowStartup(
        [Description("Absolute path to .etl file")] string path,
        [Description("Match candidates whose process name contains this substring (case-insensitive). " +
                     "Empty/null = pick the top candidates by wait ratio across the whole trace.")]
        string? nameSubstring = null,
        [Description("How many candidate processes to investigate (default 5, max 20)")] int maxCandidates = 5,
        [Description("Minimum WallUs / CpuUs ratio to consider a process 'slow' (default 3.0)")]
        double minWaitRatio = 3.0,
        [Description("Startup window width from ProcessStart, in microseconds (default 5_000_000 = 5s)")]
        long startupWindowUs = 5_000_000,
        [Description("Top N image-loads per candidate (default 30)")] int topImageLoads = 30,
        [Description("Top N CPU functions per candidate (default 15)")] int topCpu = 15)
    {
        if (maxCandidates <= 0 || maxCandidates > 20)
            throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        if (minWaitRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(minWaitRatio));
        if (startupWindowUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(startupWindowUs));

        var trace = _cache.Get(path);
        var warnings = new List<string>();
        var evidence = new List<CompositeEvidence>();
        var notConcluded = new List<CompositeNotConcluded>();
        var executedCalls = new List<CompositeToolCall>
        {
            ToolCall(
                "slow-startup.list_processes",
                "list_processes",
                pid: null,
                awakenedPid: null,
                startUs: null,
                endUs: null,
                top: maxCandidates,
                compactStacks: null,
                summaryOnly: null,
                whenBuckets: null,
                warnings: Array.Empty<string>()),
        };

        // 1. Pick candidates via the shared ProcessProjection.
        IEnumerable<ProcessRow> rows = ProcessProjection.Rows(trace, includeSystem: false)
            .Where(r => r.WallUs > 0);
        if (!string.IsNullOrEmpty(nameSubstring))
            rows = rows.Where(r => r.Name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase));

        var ranked = rows
            .Where(r => r.WaitRatio is { } w && w >= minWaitRatio)
            .OrderByDescending(r => r.WaitRatio ?? 0)
            .ThenByDescending(r => r.WallUs)
            .Take(maxCandidates)
            .ToList();

        if (ranked.Count == 0)
        {
            warnings.Add(
                $"No processes matched (nameSubstring='{nameSubstring ?? "<any>"}', " +
                $"minWaitRatio={minWaitRatio}). Try lowering minWaitRatio or removing nameSubstring.");
            notConcluded.Add(new CompositeNotConcluded(
                Code: "no_candidates",
                Reason: "No process matched the configured nameSubstring and minWaitRatio filters.",
                Pid: null,
                BlockingCapability: null,
                RelatedCallId: "slow-startup.list_processes"));
            return new DiagnoseSlowStartupResponse(
                Candidates: new List<SlowStartupCandidate>(),
                Summary: "No candidates above minWaitRatio.",
                Warnings: warnings,
                Evidence: evidence,
                NotConcluded: notConcluded,
                ExecutedToolCalls: executedCalls,
                NextTools: Array.Empty<CompositeNextTool>());
        }

        var candidatePids = new HashSet<int>(ranked.Select(r => r.Pid));

        // 2. ONE wait_analysis pass for the whole trace (CSwitch ~M-events; we'd otherwise
        //    re-walk it once per candidate). top=int.MaxValue is intentional: WaitAnalysis
        //    truncates AFTER the per-thread aggregation, and a global top-N would silently
        //    drop threads belonging to a candidate PID whose global rank doesn't make the
        //    cut, distorting per-PID reason histograms. This is internal-only because
        //    public wait_analysis caps top at Validation.MaxTop.
        var waitResp = WaitAnalysis.Analyze(trace, top: int.MaxValue, pid: null, startUs: null, endUs: null);
        executedCalls.Add(ToolCall(
            "slow-startup.wait_analysis",
            "wait_analysis",
            pid: null,
            awakenedPid: null,
            startUs: null,
            endUs: null,
            top: null,
            compactStacks: null,
            summaryOnly: null,
            whenBuckets: null,
            warnings: waitResp.Warnings,
            replayable: false,
            internalTop: int.MaxValue,
            internalNote: $"Internal unbounded aggregation; public wait_analysis caps top at {Validation.MaxTop}."));
        warnings.AddRange(PrefixWarnings("wait_analysis", waitResp.Warnings));
        var waitByPid = waitResp.Rows
            .Where(r => candidatePids.Contains(r.Pid))
            .GroupBy(r => r.Pid)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<WaitAnalysisRow>)g.ToList());

        // 3. ONE image-load pass for all candidates.
        var imageLoadsByPid = ImageLoadAnalysis.ForPids(trace, candidatePids);

        // 4. CPU is per-pid (one CallTree per pid, so no shared-pass shortcut). We accept N passes here.
        var candidates = new List<SlowStartupCandidate>();
        foreach (var c in ranked)
        {
            var rowsForPid = waitByPid.TryGetValue(c.Pid, out var candidateWaitRows)
                ? candidateWaitRows
                : Array.Empty<WaitAnalysisRow>();
            var collapsedReasons = CollapseWaitReasons(rowsForPid, top: 5);
            var totalBlockedUs = rowsForPid.Sum(row => row.BlockedUs);

            var firstLoads = imageLoadsByPid.TryGetValue(c.Pid, out var loads)
                ? (IReadOnlyList<ImageLoadRow>)loads.Take(topImageLoads).ToList()
                : null;
            executedCalls.Add(ToolCall(
                $"slow-startup.pid-{c.Pid}.image_load_timing",
                "image_load_timing",
                pid: c.Pid,
                awakenedPid: null,
                startUs: null,
                endUs: null,
                top: topImageLoads,
                compactStacks: null,
                summaryOnly: null,
                whenBuckets: null,
                warnings: Array.Empty<string>()));

            IReadOnlyList<CpuFunctionRow>? topCpuRows = null;
            var cpuWarnings = new List<string>();
            try
            {
                var cpuResp = CpuAnalysis.TopFunctions(
                    trace, top: topCpu, pid: c.Pid,
                    startUs: c.StartUs, endUs: c.StartUs + startupWindowUs,
                    symbolLog: Console.Error,
                    excludeEtwSelfOverhead: true);
                topCpuRows = cpuResp.Rows;
                cpuWarnings.AddRange(cpuResp.Warnings);
                warnings.AddRange(PrefixWarnings($"cpu_top_functions pid {c.Pid}", cpuResp.Warnings));
            }
            catch (Exception ex)
            {
                var warning = $"cpu_top_functions for pid {c.Pid}: {ex.Message}";
                cpuWarnings.Add(warning);
                warnings.Add(warning);
            }
            executedCalls.Add(ToolCall(
                $"slow-startup.pid-{c.Pid}.cpu_top_functions",
                "cpu_top_functions",
                pid: c.Pid,
                awakenedPid: null,
                startUs: c.StartUs,
                endUs: c.StartUs + startupWindowUs,
                top: topCpu,
                compactStacks: null,
                summaryOnly: null,
                whenBuckets: null,
                warnings: cpuWarnings));

            candidates.Add(new SlowStartupCandidate(
                Pid: c.Pid,
                ParentPid: c.ParentPid,
                Name: c.Name,
                WallUs: c.WallUs,
                CpuUs: c.CpuUs,
                WaitRatio: c.WaitRatio,
                ImageLoadCount: c.ImageLoadCount,
                TopWaitReasons: collapsedReasons,
                FirstImageLoads: firstLoads,
                TopCpuFunctions: topCpuRows));
            evidence.Add(ProcessWaitEvidence(
                evidenceId: $"slow-startup.pid-{c.Pid}.wait-summary",
                callId: "slow-startup.wait_analysis",
                pid: c.Pid,
                processName: c.Name,
                cpuUs: c.CpuUs,
                blockedUs: totalBlockedUs,
                waitReasons: collapsedReasons));
        }

        return new DiagnoseSlowStartupResponse(
            Candidates: candidates,
            Summary: BuildSummary(candidates),
            Warnings: warnings,
            Evidence: evidence,
            NotConcluded: notConcluded,
            ExecutedToolCalls: executedCalls,
            NextTools: Array.Empty<CompositeNextTool>());
    }

    [McpServerTool(
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true), Description(
        "Preview high-wait composite; no root-cause/diagnosis field. Uses one pid/window " +
        "across subcalls and degrades missing StackWalks to non-stack evidence. Candidates " +
        "are ordered by total blocked microseconds, not impact or causality. Compare " +
        "ObservedPct with ThresholdPct; compare metrics only within the same MetricName/Unit. " +
        "NextTools are optional hypothesis checks, not an ordered checklist.")]
    public DiagnoseHighWaitResponse DiagnoseHighWait(
        [Description("Absolute path to .etl file")] string path,
        [Description("Optional process ID filter. Null means analyze all non-system processes.")]
        int? pid = null,
        [Description("Window start in microseconds since trace start. Null means full trace.")]
        long? startUs = null,
        [Description("Window end in microseconds since trace start. Null means full trace.")]
        long? endUs = null,
        [Description("How many candidate processes to return (default 5, max 20).")]
        int maxCandidates = 5,
        [Description("Top N wait-stack rows for each candidate when stackwalks are available (default 10).")]
        int topStacks = 10,
        [Description("Top N ReadyThread stack rows when scheduler wait reasons justify fan-out (default 10).")]
        int topReadyStacks = 10)
    {
        if (maxCandidates <= 0 || maxCandidates > 20)
            throw new ArgumentOutOfRangeException(nameof(maxCandidates), "must be in [1, 20]");
        Validation.RequireTop(topStacks);
        Validation.RequireTop(topReadyStacks);
        ValidateWindow(startUs, endUs);

        var trace = _cache.Get(path);
        var capabilities = _cache.GetCapabilities(path);
        var warnings = new List<string>();
        var evidence = new List<CompositeEvidence>();
        var notConcluded = new List<CompositeNotConcluded>();
        var nextTools = new List<CompositeNextTool>();
        var executedCalls = new List<CompositeToolCall>();

        const string waitCallId = "high-wait.wait_analysis";
        var waitResp = WaitAnalysis.Analyze(
            trace,
            top: int.MaxValue,
            pid: pid,
            startUs: startUs,
            endUs: endUs);
        executedCalls.Add(ToolCall(
            waitCallId,
            "wait_analysis",
            pid,
            awakenedPid: null,
            startUs,
            endUs,
            top: null,
            compactStacks: null,
            summaryOnly: null,
            whenBuckets: null,
            warnings: waitResp.Warnings,
            replayable: false,
            internalTop: int.MaxValue,
            internalNote: $"Internal unbounded aggregation; public wait_analysis caps top at {Validation.MaxTop}."));
        warnings.AddRange(PrefixWarnings("wait_analysis", waitResp.Warnings));

        if (!capabilities.HasCSwitch)
        {
            notConcluded.Add(new CompositeNotConcluded(
                Code: "missing_context_switches",
                Reason: "Context switch events were not observed; high-wait analysis cannot identify blocked threads.",
                Pid: pid,
                BlockingCapability: nameof(TraceCapabilities.HasCSwitch),
                RelatedCallId: waitCallId));
        }

        var positivePidRows = waitResp.Rows
            .Where(row => row.Pid > 0)
            .ToList();
        var totalPositivePidBlockedUs = positivePidRows.Sum(row => row.BlockedUs);
        var systemBlockedUs = positivePidRows
            .Where(row => row.Pid == 4)
            .Sum(row => row.BlockedUs);
        if (!pid.HasValue &&
            totalPositivePidBlockedUs > 0 &&
            systemBlockedUs / (double)totalPositivePidBlockedUs >= SignificantSystemBlockedPct)
        {
            notConcluded.Add(new CompositeNotConcluded(
                Code: "system_process_excluded",
                Reason: "PID 4 (System) was excluded from default high-wait candidates; pass pid=4 to inspect kernel/system blocked time directly.",
                Pid: 4,
                BlockingCapability: null,
                RelatedCallId: waitCallId,
                MetricName: "blockedUs",
                MetricValue: systemBlockedUs,
                Unit: "us",
                ObservedPct: systemBlockedUs / (double)totalPositivePidBlockedUs,
                ThresholdPct: SignificantSystemBlockedPct));
        }

        var candidateGroups = positivePidRows
            .Where(row => row.Pid > 0 && (pid.HasValue || row.Pid != 4))
            .GroupBy(row => row.Pid)
            .Select(group =>
            {
                var rowsForPid = group.ToList();
                var totalCpuUs = rowsForPid.Sum(row => row.CpuUs);
                var totalBlockedUs = rowsForPid.Sum(row => row.BlockedUs);
                var allReasons = CollapseWaitReasons(rowsForPid, top: int.MaxValue);
                var reasons = allReasons.Take(5).ToList();
                return new WaitCandidateAggregate(
                    Pid: group.Key,
                    ProcessName: rowsForPid.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.ProcessName))?.ProcessName ?? string.Empty,
                    TotalCpuUs: totalCpuUs,
                    TotalBlockedUs: totalBlockedUs,
                    WaitRatio: totalCpuUs > 0 ? totalBlockedUs / (double)totalCpuUs : null,
                    ContextSwitches: rowsForPid.Sum(row => row.ContextSwitches),
                    TopWaitReasons: reasons,
                    SchedulerWaitPct: SchedulerWaitPct(allReasons, totalBlockedUs));
            })
            .Where(candidate => candidate.TotalBlockedUs > 0)
            .OrderByDescending(candidate => candidate.TotalBlockedUs)
            .ThenByDescending(candidate => candidate.WaitRatio ?? 0)
            .Take(maxCandidates)
            .ToList();

        if (candidateGroups.Count == 0)
        {
            notConcluded.Add(new CompositeNotConcluded(
                Code: "no_wait_candidates",
                Reason: "No blocked-time rows matched the requested pid/window filters.",
                Pid: pid,
                BlockingCapability: null,
                RelatedCallId: waitCallId));
        }

        if (!capabilities.HasStackWalks)
        {
            notConcluded.Add(new CompositeNotConcluded(
                Code: "missing_stackwalks",
                Reason: "StackWalk events were not observed; evidence stops at process, thread, and wait-reason level and does not claim a code path.",
                Pid: pid,
                BlockingCapability: nameof(TraceCapabilities.HasStackWalks),
                RelatedCallId: waitCallId));
        }

        var candidates = new List<HighWaitCandidate>();
        foreach (var candidate in candidateGroups)
        {
            evidence.Add(ProcessWaitEvidence(
                evidenceId: $"high-wait.pid-{candidate.Pid}.wait-summary",
                callId: waitCallId,
                pid: candidate.Pid,
                processName: candidate.ProcessName,
                cpuUs: candidate.TotalCpuUs,
                blockedUs: candidate.TotalBlockedUs,
                waitReasons: candidate.TopWaitReasons));

            foreach (var (reason, reasonIndex) in candidate.TopWaitReasons.Select((reason, index) => (reason, index)))
            {
                evidence.Add(new CompositeEvidence(
                    EvidenceId: $"high-wait.pid-{candidate.Pid}.reason-{reasonIndex}-{SanitizeId(reason.Reason)}",
                    CallId: waitCallId,
                    EvidenceType: "wait_reason",
                    Pid: candidate.Pid,
                    Tid: null,
                    ProcessName: candidate.ProcessName,
                    Label: reason.Reason,
                    MetricName: "blockedUs",
                    MetricValue: reason.BlockedUs,
                    Unit: "us",
                    TopWaitReasons: new[] { reason },
                    Frames: Array.Empty<FrameMetric>()));
            }

            string? waitStacksCallId = null;
            if (capabilities.HasCSwitch && capabilities.HasStackWalks)
            {
                waitStacksCallId = $"high-wait.pid-{candidate.Pid}.wait_top_stacks";
                var effectiveTopStacks = StackResponseOptions.EffectiveTop(
                    topStacks, compactStacks: false, summaryOnly: true);
                var stackResp = BlockedTimeStackAnalysis.TopBlockedStacks(
                    trace,
                    effectiveTopStacks,
                    pid: candidate.Pid,
                    startUs: startUs,
                    endUs: endUs,
                    symbolLog: Console.Error);
                executedCalls.Add(ToolCall(
                    waitStacksCallId,
                    "wait_top_stacks",
                    pid: candidate.Pid,
                    awakenedPid: null,
                    startUs,
                    endUs,
                    top: topStacks,
                    compactStacks: false,
                    summaryOnly: true,
                    whenBuckets: 0,
                    warnings: stackResp.Warnings,
                    effectiveTop: effectiveTopStacks));
                warnings.AddRange(PrefixWarnings($"wait_top_stacks pid {candidate.Pid}", stackResp.Warnings));

                evidence.Add(new CompositeEvidence(
                    EvidenceId: $"high-wait.pid-{candidate.Pid}.wait-stacks",
                    CallId: waitStacksCallId,
                    EvidenceType: "wait_stack_summary",
                    Pid: candidate.Pid,
                    Tid: null,
                    ProcessName: candidate.ProcessName,
                    Label: "Top blocked-time stack frames",
                    MetricName: "blockedUs",
                    MetricValue: stackResp.TotalBlockedUs,
                    Unit: "us",
                    TopWaitReasons: Array.Empty<WaitReasonBucket>(),
                    Frames: stackResp.Rows
                        .Select(row => new FrameMetric(
                            Function: row.Function,
                            ExclusiveMetric: row.ExclusiveBlockedUs,
                            InclusiveMetric: row.InclusiveBlockedUs,
                            Unit: "us"))
                        .ToList()));
            }

            string? readyThreadCallId = null;
            var schedulerWaitPct = candidate.SchedulerWaitPct;
            var shouldRunReadyThread = schedulerWaitPct >= ReadyThreadSchedulerThresholdPct;
            if (shouldRunReadyThread)
            {
                if (!capabilities.HasReadyThread)
                {
                    notConcluded.Add(new CompositeNotConcluded(
                        Code: "missing_ready_thread",
                        Reason: "Scheduler-dispatch wait reasons were present, but ReadyThread events were not observed.",
                        Pid: candidate.Pid,
                        BlockingCapability: nameof(TraceCapabilities.HasReadyThread),
                        RelatedCallId: waitCallId,
                        MetricName: "schedulerWaitBlockedPct",
                        MetricValue: schedulerWaitPct,
                        Unit: "ratio",
                        ObservedPct: schedulerWaitPct,
                        ThresholdPct: ReadyThreadSchedulerThresholdPct));
                }
                else if (!capabilities.HasStackWalks)
                {
                    notConcluded.Add(new CompositeNotConcluded(
                        Code: "ready_thread_skipped_missing_stackwalks",
                        Reason: "Scheduler-dispatch wait reasons were present, but missing StackWalk events prevent ReadyThread call-path evidence.",
                        Pid: candidate.Pid,
                        BlockingCapability: nameof(TraceCapabilities.HasStackWalks),
                        RelatedCallId: waitCallId,
                        MetricName: "schedulerWaitBlockedPct",
                        MetricValue: schedulerWaitPct,
                        Unit: "ratio",
                        ObservedPct: schedulerWaitPct,
                        ThresholdPct: ReadyThreadSchedulerThresholdPct));
                }
                else
                {
                    readyThreadCallId = $"high-wait.pid-{candidate.Pid}.ready_thread_top_stacks";
                    var effectiveTopReadyStacks = StackResponseOptions.EffectiveTop(
                        topReadyStacks, compactStacks: false, summaryOnly: true);
                    var readyResp = ReadyThreadStackAnalysis.TopStacks(
                        trace,
                        effectiveTopReadyStacks,
                        awakenedPid: candidate.Pid,
                        startUs: startUs,
                        endUs: endUs,
                        symbolLog: Console.Error);
                    executedCalls.Add(ToolCall(
                        readyThreadCallId,
                        "ready_thread_top_stacks",
                        pid: null,
                        awakenedPid: candidate.Pid,
                        startUs,
                        endUs,
                        top: topReadyStacks,
                        compactStacks: false,
                        summaryOnly: true,
                        whenBuckets: 0,
                        warnings: readyResp.Warnings,
                        effectiveTop: effectiveTopReadyStacks));
                    warnings.AddRange(PrefixWarnings($"ready_thread_top_stacks awakenedPid {candidate.Pid}", readyResp.Warnings));

                    evidence.Add(new CompositeEvidence(
                        EvidenceId: $"high-wait.pid-{candidate.Pid}.ready-stacks",
                        CallId: readyThreadCallId,
                        EvidenceType: "ready_thread_stack_summary",
                        Pid: candidate.Pid,
                        Tid: null,
                        ProcessName: candidate.ProcessName,
                        Label: "Top readier stack frames for this process",
                        MetricName: "readyEvents",
                        MetricValue: readyResp.TotalReadyCount,
                        Unit: "events",
                        TopWaitReasons: Array.Empty<WaitReasonBucket>(),
                        Frames: readyResp.Rows
                            .Select(row => new FrameMetric(
                                Function: row.Function,
                                ExclusiveMetric: row.ExclusiveReadyCount,
                                InclusiveMetric: row.InclusiveReadyCount,
                                Unit: "events"))
                            .ToList()));
                }
            }
            else if (schedulerWaitPct > 0)
            {
                notConcluded.Add(new CompositeNotConcluded(
                    Code: "ready_thread_below_threshold",
                    Reason: "Scheduler-dispatch wait reasons were observed but did not meet the preview threshold for ReadyThread fan-out.",
                    Pid: candidate.Pid,
                    BlockingCapability: null,
                    RelatedCallId: waitCallId,
                    MetricName: "schedulerWaitBlockedPct",
                    MetricValue: schedulerWaitPct,
                    Unit: "ratio",
                    ObservedPct: schedulerWaitPct,
                    ThresholdPct: ReadyThreadSchedulerThresholdPct));
            }

            nextTools.Add(new CompositeNextTool(
                ToolName: "wait_analysis",
                Reason: "Inspect the candidate process's blocked threads and wait reasons directly.",
                Pid: candidate.Pid,
                AwakenedPid: null,
                StartUs: startUs,
                EndUs: endUs,
                CompactStacks: null,
                SummaryOnly: null,
                TestsHypothesis: "Verify whether this candidate's blocked time is concentrated in specific threads or wait reasons."));

            if (capabilities.HasCSwitch && capabilities.HasStackWalks)
            {
                nextTools.Add(new CompositeNextTool(
                    ToolName: "wait_top_stacks",
                    Reason: "Expand beyond the preview summary to inspect detailed blocked-time stack rows.",
                    Pid: candidate.Pid,
                    AwakenedPid: null,
                    StartUs: startUs,
                    EndUs: endUs,
                    CompactStacks: false,
                    SummaryOnly: false,
                    TestsHypothesis: "Verify whether blocked time maps to a specific code path or is spread across unrelated waits."));
            }

            if (shouldRunReadyThread && capabilities.HasReadyThread && capabilities.HasStackWalks)
            {
                nextTools.Add(new CompositeNextTool(
                    ToolName: "ready_thread_top_stacks",
                    Reason: "Scheduler-dispatch wait reasons were present; inspect who woke this process's threads.",
                    Pid: null,
                    AwakenedPid: candidate.Pid,
                    StartUs: startUs,
                    EndUs: endUs,
                    CompactStacks: false,
                    SummaryOnly: false,
                    TestsHypothesis: "Verify whether scheduler-related waits are explained by a specific readier or wake-up path."));
            }

            candidates.Add(new HighWaitCandidate(
                Pid: candidate.Pid,
                ProcessName: candidate.ProcessName,
                TotalCpuUs: candidate.TotalCpuUs,
                TotalBlockedUs: candidate.TotalBlockedUs,
                WaitRatio: candidate.WaitRatio,
                ContextSwitches: candidate.ContextSwitches,
                TopWaitReasons: candidate.TopWaitReasons,
                WaitAnalysisCallId: waitCallId,
                WaitStacksCallId: waitStacksCallId,
                ReadyThreadCallId: readyThreadCallId));
        }

        return new DiagnoseHighWaitResponse(
            Candidates: candidates,
            Evidence: evidence,
            NotConcluded: notConcluded,
            NextTools: nextTools,
            ExecutedToolCalls: executedCalls,
            Warnings: warnings);
    }

    private static string BuildSummary(IReadOnlyList<SlowStartupCandidate> candidates)
    {
        if (candidates.Count == 0) return "No candidates.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {candidates.Count} slow-startup candidate(s):");
        foreach (var c in candidates)
        {
            var ratioStr = c.WaitRatio is { } r ? $"{r:F1}x" : "n/a";
            sb.AppendLine($"  - pid {c.Pid} ({c.Name}): wall={c.WallUs / 1000.0:F1}ms, cpu={c.CpuUs / 1000.0:F1}ms, wait_ratio={ratioStr}");
            if (c.TopWaitReasons.Count > 0)
            {
                var reasons = string.Join(", ", c.TopWaitReasons.Take(3).Select(b => b.Reason));
                sb.AppendLine($"    top wait reasons: {reasons}");
            }
        }
        return sb.ToString();
    }

    private static CompositeToolCall ToolCall(
        string callId,
        string toolName,
        int? pid,
        int? awakenedPid,
        long? startUs,
        long? endUs,
        int? top,
        bool? compactStacks,
        bool? summaryOnly,
        int? whenBuckets,
        IReadOnlyList<string> warnings,
        int? effectiveTop = null,
        bool replayable = true,
        int? internalTop = null,
        string? internalNote = null)
        => new(
            CallId: callId,
            ToolName: toolName,
            Pid: pid,
            AwakenedPid: awakenedPid,
            StartUs: startUs,
            EndUs: endUs,
            Top: top,
            CompactStacks: compactStacks,
            SummaryOnly: summaryOnly,
            WhenBuckets: whenBuckets,
            Warnings: warnings.ToList(),
            EffectiveTop: effectiveTop,
            Replayable: replayable,
            InternalTop: internalTop,
            InternalNote: internalNote);

    private static CompositeEvidence ProcessWaitEvidence(
        string evidenceId,
        string callId,
        int pid,
        string processName,
        long cpuUs,
        long blockedUs,
        IReadOnlyList<WaitReasonBucket> waitReasons)
        => new(
            EvidenceId: evidenceId,
            CallId: callId,
            EvidenceType: "process_wait_summary",
            Pid: pid,
            Tid: null,
            ProcessName: processName,
            Label: $"pid {pid} ({processName}) blocked-time summary; cpuUs={cpuUs}",
            MetricName: "blockedUs",
            MetricValue: blockedUs,
            Unit: "us",
            TopWaitReasons: waitReasons,
            Frames: Array.Empty<FrameMetric>());

    private static IReadOnlyList<WaitReasonBucket> CollapseWaitReasons(
        IEnumerable<WaitAnalysisRow> rows,
        int top)
        => rows
            .SelectMany(row => row.TopWaitReasons)
            .GroupBy(reason => reason.Reason)
            .Select(group => new WaitReasonBucket(
                Reason: group.Key,
                BlockedUs: group.Sum(reason => reason.BlockedUs),
                Count: group.Sum(reason => reason.Count)))
            .OrderByDescending(reason => reason.BlockedUs)
            .Take(top)
            .ToList();

    private static double SchedulerWaitPct(IReadOnlyList<WaitReasonBucket> reasons, long totalBlockedUs)
    {
        if (totalBlockedUs <= 0) return 0;

        var schedulerBlocked = reasons
            .Where(reason => IsSchedulerWaitReason(reason.Reason))
            .Sum(reason => reason.BlockedUs);
        return schedulerBlocked / (double)totalBlockedUs;
    }

    private static bool IsSchedulerWaitReason(string reason) =>
        reason is "WrDispatchInt" or "WrPreempted" or "WrQuantumEnd" or "WrDeferredPreempt";

    private static IEnumerable<string> PrefixWarnings(string source, IReadOnlyList<string> warnings) =>
        warnings.Select(warning => $"{source}: {warning}");

    private static void ValidateWindow(long? startUs, long? endUs)
    {
        if (startUs is < 0)
            throw new ArgumentOutOfRangeException(nameof(startUs), "must be non-negative");
        if (endUs is < 0)
            throw new ArgumentOutOfRangeException(nameof(endUs), "must be non-negative");
        if (startUs is { } start && endUs is { } end && end < start)
            throw new ArgumentException("endUs must be greater than or equal to startUs", nameof(endUs));
    }

    private static string SanitizeId(string value)
    {
        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private sealed record WaitCandidateAggregate(
        int Pid,
        string ProcessName,
        long TotalCpuUs,
        long TotalBlockedUs,
        double? WaitRatio,
        long ContextSwitches,
        IReadOnlyList<WaitReasonBucket> TopWaitReasons,
        double SchedulerWaitPct);
}
