using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Diagnostics.Tracing.Etlx;
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
    private const long DefaultDiagnoseWindowLimitUs = 60_000_000;
    private const long PageInZoomBeforeUs = 3_000_000;
    private const long PageInZoomAfterUs = 1_000_000;

    private readonly TraceCache _cache;
    public DiagnoseTools(TraceCache cache) => _cache = cache;

    [McpServerTool(
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        Destructive = false,
        UseStructuredContent = true), Description(
        "Windowed evidence composite for a specific trace interval. Aggregates per-file hard faults " +
        "by bytes and max latency, top file IO, memory-pressure samples, security-scan evidence, " +
        "and wait_analysis rows for the same pid/startUs/endUs. No root-cause verdict: compare " +
        "the facts and use NextTools for zoom-in. Guarded by maxWindowDurationUs so this is not " +
        "mistaken for a whole-trace dashboard.")]
    public DiagnoseWindowResponse DiagnoseWindow(
        [Description("Absolute path to .etl file")] string path,
        [Description("Window start in microseconds since trace start. Required.")]
        long startUs,
        [Description("Window end in microseconds since trace start (exclusive). Required.")]
        long endUs,
        [Description("Optional process ID filter. Null aggregates all processes in the window.")]
        int? pid = null,
        [Description("Top N rows per evidence section (default 10, max 1000).")]
        int top = 10,
        [Description("Maximum allowed window width in microseconds (default 60s). Wider windows return a guard warning.")]
        long maxWindowDurationUs = DefaultDiagnoseWindowLimitUs)
    {
        Validation.RequireTop(top);
        ValidateWindow(startUs, endUs);
        if (maxWindowDurationUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWindowDurationUs), "must be positive");

        var trace = _cache.Get(path);
        return BuildDiagnoseWindow(trace, startUs, endUs, pid, top, maxWindowDurationUs, callPrefix: "diagnose-window");
    }

    private static DiagnoseWindowResponse BuildDiagnoseWindow(
        TraceLog trace,
        long startUs,
        long endUs,
        int? pid,
        int top,
        long maxWindowDurationUs,
        string callPrefix)
    {
        var durationUs = endUs - startUs;
        var warnings = new List<string>();
        var notConcluded = new List<CompositeNotConcluded>();
        var nextTools = new List<CompositeNextTool>();
        var executedCalls = new List<CompositeToolCall>();
        var evidence = new List<WindowEvidenceRow>();

        if (durationUs > maxWindowDurationUs)
        {
            var warning = $"diagnose_window window is {durationUs}us, above maxWindowDurationUs={maxWindowDurationUs}; narrow the window or call analyzers individually.";
            warnings.Add(warning);
            notConcluded.Add(new CompositeNotConcluded(
                Code: "window_too_wide",
                Reason: warning,
                Pid: pid,
                BlockingCapability: null,
                RelatedCallId: null,
                MetricName: "windowDurationUs",
                MetricValue: durationUs,
                Unit: "us",
                ObservedPct: durationUs / (double)maxWindowDurationUs,
                ThresholdPct: 1.0));
            return EmptyDiagnoseWindow(startUs, endUs, pid, evidence, notConcluded, nextTools, executedCalls, warnings);
        }

        var hardFaultBytes = HardFaultByFileAnalysis.Analyze(trace, top, pid, "bytes", startUs, endUs);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.hard_fault_by_file.bytes", "hard_fault_by_file", pid, startUs, endUs, top, hardFaultBytes.Warnings, orderBy: "bytes");
        if (hardFaultBytes.Rows.FirstOrDefault() is { } topHardFaultBytes)
        {
            evidence.Add(new WindowEvidenceRow(
                EvidenceType: "hard_fault_bytes",
                Label: "Top hard-fault page-in file by bytes",
                MetricName: "pageInBytes",
                MetricValue: topHardFaultBytes.PageInBytes,
                Unit: "bytes",
                Pid: pid,
                ProcessName: null,
                File: topHardFaultBytes.File,
                TimeUs: topHardFaultBytes.MaxLatencyTimeUs,
                Details: new[]
                {
                    $"pageInCount={topHardFaultBytes.PageInCount}",
                    $"maxLatencyUs={topHardFaultBytes.MaxLatencyUs}",
                }));
        }
        else
        {
            AddNoSignal(notConcluded, "no_hard_fault_bytes", "No hard-fault page-in bytes matched this pid/window.", pid, $"{callPrefix}.hard_fault_by_file.bytes");
        }

        var hardFaultLatency = HardFaultByFileAnalysis.Analyze(trace, top, pid, "max_latency", startUs, endUs);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.hard_fault_by_file.max_latency", "hard_fault_by_file", pid, startUs, endUs, top, hardFaultLatency.Warnings, orderBy: "max_latency");
        if (hardFaultLatency.Rows.FirstOrDefault() is { } topHardFaultLatency)
        {
            evidence.Add(new WindowEvidenceRow(
                EvidenceType: "hard_fault_max_latency",
                Label: "Worst hard-fault page-in latency",
                MetricName: "maxLatencyUs",
                MetricValue: topHardFaultLatency.MaxLatencyUs,
                Unit: "us",
                Pid: pid,
                ProcessName: null,
                File: topHardFaultLatency.File,
                TimeUs: topHardFaultLatency.MaxLatencyTimeUs,
                Details: new[]
                {
                    $"pageInBytes={topHardFaultLatency.PageInBytes}",
                    $"pageInCount={topHardFaultLatency.PageInCount}",
                }));

            var zoomStartUs = Math.Max(0, topHardFaultLatency.MaxLatencyTimeUs - PageInZoomBeforeUs);
            nextTools.Add(new CompositeNextTool(
                ToolName: "diagnose_window",
                Reason: "Zoom around the worst hard-fault latency timestamp.",
                Pid: pid,
                AwakenedPid: null,
                StartUs: zoomStartUs,
                EndUs: topHardFaultLatency.MaxLatencyTimeUs + PageInZoomAfterUs,
                CompactStacks: null,
                SummaryOnly: null,
                TestsHypothesis: "Check whether file IO, waits, memory pressure, or scan events cluster around the page-in stall."));
        }
        else
        {
            AddNoSignal(notConcluded, "no_hard_fault_latency", "No hard-fault latency rows matched this pid/window.", pid, $"{callPrefix}.hard_fault_by_file.max_latency");
        }

        var fileIo = FileIoAnalysis.TopFiles(trace, top, pid, startUs, endUs);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.file_io_top_files", "file_io_top_files", pid, startUs, endUs, top, Array.Empty<string>());
        if (fileIo.Rows.FirstOrDefault() is { } topFile)
        {
            var bytes = topFile.ReadBytes + topFile.WriteBytes;
            evidence.Add(new WindowEvidenceRow(
                EvidenceType: "file_io_top_file",
                Label: "Top file IO path by read+write bytes",
                MetricName: "ioBytes",
                MetricValue: bytes,
                Unit: "bytes",
                Pid: pid,
                ProcessName: null,
                File: topFile.File,
                TimeUs: null,
                Details: new[]
                {
                    $"readBytes={topFile.ReadBytes}",
                    $"writeBytes={topFile.WriteBytes}",
                    $"readCount={topFile.ReadCount}",
                    $"writeCount={topFile.WriteCount}",
                }));
        }
        else
        {
            AddNoSignal(notConcluded, "no_file_io", "No file IO rows matched this pid/window.", pid, $"{callPrefix}.file_io_top_files");
        }

        var memory = MemoryResourceAnalysis.Analyze(trace, top, pid, startUs, endUs);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.memory_resource_analysis", "memory_resource_analysis", pid, startUs, endUs, top, memory.Warnings);
        if (memory.Pressure.MinFreeBytes is { } minFreeBytes)
        {
            evidence.Add(new WindowEvidenceRow(
                EvidenceType: "memory_pressure",
                Label: "Minimum observed free memory in the window",
                MetricName: "minFreeBytes",
                MetricValue: minFreeBytes,
                Unit: "bytes",
                Pid: pid,
                ProcessName: null,
                File: null,
                TimeUs: memory.Pressure.MinFreeTimeUs,
                Details: memory.Pressure.TopPeakWorkingSetProcesses
                    .Take(3)
                    .Select(row => $"topWorkingSet pid={row.Pid} {row.ProcessName} peakWorkingSetBytes={row.PeakWorkingSetBytes}")
                    .ToList()));
        }
        else if (memory.Pressure.ProcessSnapshotBatchCount == 0 && memory.Pressure.SystemSampleCount == 0)
        {
            AddNoSignal(notConcluded, "no_memory_samples", "No memory resource samples matched this pid/window.", pid, $"{callPrefix}.memory_resource_analysis");
        }

        var security = SecurityScanAnalysis.Analyze(trace, top, pid, startUs, endUs, processSubstring: null, pathSubstring: null, providerSubstring: null);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.security_scan_analysis", "security_scan_analysis", pid, startUs, endUs, top, security.Warnings);
        if (security.PairedScanCount > 0)
        {
            evidence.Add(new WindowEvidenceRow(
                EvidenceType: "security_scan_duration",
                Label: "Paired security scan duration in the window",
                MetricName: "scanDurationUs",
                MetricValue: security.TotalDurationUs,
                Unit: "us",
                Pid: pid,
                ProcessName: security.Rows.FirstOrDefault()?.Process,
                File: security.Rows.FirstOrDefault()?.Path,
                TimeUs: security.SlowScans.FirstOrDefault()?.StartUs,
                Details: new[]
                {
                    $"pairedScanCount={security.PairedScanCount}",
                    $"matchedEventCount={security.MatchedEventCount}",
                }));
        }
        else if (security.MatchedEventCount > 0)
        {
            evidence.Add(new WindowEvidenceRow(
                EvidenceType: "security_scan_presence",
                Label: "Security scan-like events were present but not paired into durations",
                MetricName: "matchedSecurityEvents",
                MetricValue: security.MatchedEventCount,
                Unit: "events",
                Pid: pid,
                ProcessName: security.Rows.FirstOrDefault()?.Process,
                File: security.Rows.FirstOrDefault()?.Path,
                TimeUs: null,
                Details: security.Providers.Take(3).Select(provider => $"{provider.ProviderName}:{provider.EventCount}").ToList()));
        }
        else
        {
            AddNoSignal(notConcluded, "no_security_scan_events", "No security scan-like events matched this pid/window.", pid, $"{callPrefix}.security_scan_analysis");
        }

        var waits = WaitAnalysis.Analyze(trace, top, pid, startUs, endUs);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.wait_analysis", "wait_analysis", pid, startUs, endUs, top, waits.Warnings);
        var totalBlockedUs = waits.Rows.Sum(row => row.BlockedUs);
        if (totalBlockedUs > 0)
        {
            evidence.Add(new WindowEvidenceRow(
                EvidenceType: "wait_summary",
                Label: "Total blocked time across returned wait rows",
                MetricName: "blockedUs",
                MetricValue: totalBlockedUs,
                Unit: "us",
                Pid: pid,
                ProcessName: waits.Rows.FirstOrDefault()?.ProcessName,
                File: null,
                TimeUs: null,
                Details: CollapseWaitReasons(waits.Rows, top: 3)
                    .Select(reason => $"{reason.Reason}={reason.BlockedUs}us/{reason.Count}")
                    .ToList()));

            nextTools.Add(new CompositeNextTool(
                ToolName: "wait_top_stacks",
                Reason: "Expand the window's blocked-time evidence into stack rows when CSwitch stackwalks are present.",
                Pid: pid,
                AwakenedPid: null,
                StartUs: startUs,
                EndUs: endUs,
                CompactStacks: false,
                SummaryOnly: false,
                TestsHypothesis: "Check whether blocked time maps to a specific code path rather than a broad wait-state total."));
        }
        else
        {
            AddNoSignal(notConcluded, "no_wait_rows", "No wait_analysis rows with blocked time matched this pid/window.", pid, $"{callPrefix}.wait_analysis");
        }

        return new DiagnoseWindowResponse(
            WindowStartUs: startUs,
            WindowEndUs: endUs,
            DurationUs: durationUs,
            Pid: pid,
            HardFaultsByBytes: hardFaultBytes.Rows,
            HardFaultsByMaxLatency: hardFaultLatency.Rows,
            FileIoTopFiles: fileIo.Rows,
            Pressure: memory.Pressure,
            SecurityScanTargets: security.Rows,
            SlowScans: security.SlowScans,
            SecurityMatchedEventCount: security.MatchedEventCount,
            SecurityPairedScanCount: security.PairedScanCount,
            SecurityTotalDurationUs: security.TotalDurationUs,
            Waits: waits.Rows,
            Evidence: evidence,
            NotConcluded: notConcluded,
            NextTools: nextTools,
            ExecutedToolCalls: executedCalls,
            Warnings: warnings);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Composite 'why is process X slow to start' analysis. Picks the slowest-by-wait-ratio processes " +
        "(or the ones matching nameSubstring), then runs wait_analysis (top wait reasons), image_load_timing " +
        "(first N DLLs from process start), cpu_top_functions (top hot functions in the startup window), " +
        "and diagnose_window for slow ProcessStart→first-ImageLoad gaps " +
        "for each. Equivalent to manually composing list_processes + wait_analysis + image_load_timing + " +
        "cpu_top_functions + diagnose_window but with a single tool call. No startUs/endUs: this composite derives each " +
        "candidate window from ProcessStart plus startupWindowUs.")]
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
        [Description("Top N CPU functions per candidate (default 15)")] int topCpu = 15,
        [Description("Minimum ProcessStart→first ImageLoad gap, in microseconds, before running diagnose_window (default 1s).")]
        long slowFirstImageLoadThresholdUs = 1_000_000,
        [Description("Top N rows per diagnose_window section for slow first-image-load gaps (default 10).")]
        int topWindowEvidence = 10,
        [Description("Maximum diagnose_window width for first-image-load gap evidence, in microseconds (default 60s).")]
        long maxWindowDurationUs = DefaultDiagnoseWindowLimitUs)
    {
        if (maxCandidates <= 0 || maxCandidates > 20)
            throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        if (minWaitRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(minWaitRatio));
        if (startupWindowUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(startupWindowUs));
        if (slowFirstImageLoadThresholdUs < 0)
            throw new ArgumentOutOfRangeException(nameof(slowFirstImageLoadThresholdUs));
        Validation.RequireTop(topWindowEvidence);
        if (maxWindowDurationUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWindowDurationUs), "must be positive");

        var trace = _cache.Get(path);
        var warnings = new List<string>();
        var evidence = new List<CompositeEvidence>();
        var notConcluded = new List<CompositeNotConcluded>();
        var nextTools = new List<CompositeNextTool>();
        var firstImageLoadGapEvidence = new List<StartupGapEvidenceRow>();
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
                NextTools: nextTools,
                FirstImageLoadGapEvidence: firstImageLoadGapEvidence);
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

            var hasImageLoads = imageLoadsByPid.TryGetValue(c.Pid, out var loads);
            var firstLoads = hasImageLoads
                ? (IReadOnlyList<ImageLoadRow>)loads!.Take(topImageLoads).ToList()
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

            if (hasImageLoads && loads!.Count > 0)
            {
                var firstLoad = loads[0];
                var firstImageLoadOffsetUs = Math.Max(0, firstLoad.TimeUs - c.StartUs);
                if (firstImageLoadOffsetUs >= slowFirstImageLoadThresholdUs)
                {
                    var windowStartUs = c.StartUs;
                    var windowEndUs = Math.Max(windowStartUs, firstLoad.TimeUs + 1);
                    var callId = $"slow-startup.pid-{c.Pid}.diagnose_window";
                    var window = BuildDiagnoseWindow(
                        trace,
                        windowStartUs,
                        windowEndUs,
                        c.Pid,
                        topWindowEvidence,
                        maxWindowDurationUs,
                        callPrefix: $"slow-startup.pid-{c.Pid}.first-image-load-gap");

                    executedCalls.Add(ToolCall(
                        callId,
                        "diagnose_window",
                        pid: c.Pid,
                        awakenedPid: null,
                        startUs: windowStartUs,
                        endUs: windowEndUs,
                        top: topWindowEvidence,
                        compactStacks: null,
                        summaryOnly: null,
                        whenBuckets: null,
                        warnings: window.Warnings));
                    warnings.AddRange(PrefixWarnings($"diagnose_window pid {c.Pid}", window.Warnings));
                    nextTools.AddRange(window.NextTools);
                    firstImageLoadGapEvidence.Add(new StartupGapEvidenceRow(
                        Pid: c.Pid,
                        ProcessName: c.Name,
                        ProcessStartUs: c.StartUs,
                        FirstImageLoadTimeUs: firstLoad.TimeUs,
                        FirstImageLoadOffsetUs: firstImageLoadOffsetUs,
                        Window: window));
                }
            }

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

        if (firstImageLoadGapEvidence.Count == 0)
        {
            notConcluded.Add(new CompositeNotConcluded(
                Code: "no_slow_first_image_load_gaps",
                Reason: "No candidate had a ProcessStart-to-first-ImageLoad gap meeting slowFirstImageLoadThresholdUs, so diagnose_window gap evidence was not run.",
                Pid: null,
                BlockingCapability: null,
                RelatedCallId: null,
                MetricName: "slowFirstImageLoadThresholdUs",
                MetricValue: slowFirstImageLoadThresholdUs,
                Unit: "us"));
        }

        return new DiagnoseSlowStartupResponse(
            Candidates: candidates,
            Summary: BuildSummary(candidates),
            Warnings: warnings,
            Evidence: evidence,
            NotConcluded: notConcluded,
            ExecutedToolCalls: executedCalls,
            NextTools: nextTools,
            FirstImageLoadGapEvidence: firstImageLoadGapEvidence);
    }

    [McpServerTool(
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        Destructive = false,
        UseStructuredContent = true), Description(
        "Preview high-wait composite; no root-cause field. One pid/window across subcalls; " +
        "missing stacks degrade to non-stack evidence. Candidates are ordered by total blocked " +
        "microseconds, not impact or causality. Compare same MetricName/Unit, and ObservedPct " +
        "with ThresholdPct. TimeBudgetMs bounds post-wait stack fan-out. " +
        "NextTools are optional hypothesis checks, not an ordered checklist.")]
    public DiagnoseHighWaitResponse DiagnoseHighWait(
        [Description("Absolute path to .etl file")] string path,
        [Description("Optional process ID filter. Null means analyze all non-system processes.")]
        int? pid = null,
        [Description("Window start in microseconds since trace start. Null means full trace.")]
        long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive). Null means full trace.")]
        long? endUs = null,
        [Description("How many candidate processes to return (default 5, max 20).")]
        int maxCandidates = 5,
        [Description("Top N wait-stack rows for each candidate when stackwalks are available (default 10).")]
        int topStacks = 10,
        [Description("Top N ReadyThread stack rows when scheduler wait reasons justify fan-out (default 10).")]
        int topReadyStacks = 10,
        [Description("Run ReadyThread stack fan-out when scheduler wait reasons justify it. Default false keeps preview bounded.")]
        bool includeReadyStacks = false,
        [Description("Soft budget in milliseconds for post-wait candidate stack fan-out. Exhaustion returns completed evidence plus partial warnings.")]
        int timeBudgetMs = 100_000)
    {
        if (maxCandidates <= 0 || maxCandidates > 20)
            throw new ArgumentOutOfRangeException(nameof(maxCandidates), "must be in [1, 20]");
        Validation.RequireTop(topStacks);
        Validation.RequireTop(topReadyStacks);
        Validation.RequireTimeBudgetMs(timeBudgetMs);
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

        var stackBudget = Stopwatch.StartNew();
        var budgetExhaustedKeys = new HashSet<string>(StringComparer.Ordinal);
        bool BudgetExpired() => stackBudget.ElapsedMilliseconds >= timeBudgetMs;
        void AddBudgetExhausted(int candidatePid, string skippedWork)
        {
            if (!budgetExhaustedKeys.Add($"{candidatePid}:{skippedWork}"))
                return;

            var message = $"diagnose_high_wait reached its {timeBudgetMs} ms post-wait stack budget; skipped {skippedWork} for pid {candidatePid}. Returned evidence is partial, not a complete diagnosis.";
            warnings.Add(message);
            notConcluded.Add(new CompositeNotConcluded(
                Code: "time_budget_exhausted",
                Reason: message,
                Pid: candidatePid,
                BlockingCapability: null,
                RelatedCallId: waitCallId));
        }

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

        if (capabilities.HasCSwitch && !capabilities.HasCSwitchStacks)
        {
            notConcluded.Add(new CompositeNotConcluded(
                Code: "missing_stackwalks",
                Reason: "CSwitch events were observed, but they did not carry call stacks; evidence stops at process, thread, and wait-reason level and does not claim a code path.",
                Pid: pid,
                BlockingCapability: nameof(TraceCapabilities.HasCSwitchStacks),
                RelatedCallId: waitCallId));
        }
        else if (!capabilities.HasStackWalks)
        {
            notConcluded.Add(new CompositeNotConcluded(
                Code: "missing_stackwalks",
                Reason: "No usable stack data was observed; evidence stops at process, thread, and wait-reason level and does not claim a code path.",
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
            if (capabilities.HasCSwitch && capabilities.HasCSwitchStacks)
            {
                if (BudgetExpired())
                {
                    AddBudgetExhausted(candidate.Pid, "wait_top_stacks");
                }
                else
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
            }

            string? readyThreadCallId = null;
            var schedulerWaitPct = candidate.SchedulerWaitPct;
            var shouldRunReadyThread = schedulerWaitPct >= ReadyThreadSchedulerThresholdPct;
            if (shouldRunReadyThread)
            {
                if (!includeReadyStacks)
                {
                    notConcluded.Add(new CompositeNotConcluded(
                        Code: "ready_thread_skipped_by_option",
                        Reason: "Scheduler-dispatch wait reasons met the ReadyThread fan-out threshold, but includeReadyStacks=false kept the preview bounded.",
                        Pid: candidate.Pid,
                        BlockingCapability: null,
                        RelatedCallId: waitCallId,
                        MetricName: "schedulerWaitBlockedPct",
                        MetricValue: schedulerWaitPct,
                        Unit: "ratio",
                        ObservedPct: schedulerWaitPct,
                        ThresholdPct: ReadyThreadSchedulerThresholdPct));
                }
                else if (!capabilities.HasReadyThread)
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
                else if (!capabilities.HasReadyThreadStacks)
                {
                    notConcluded.Add(new CompositeNotConcluded(
                        Code: "ready_thread_skipped_missing_stackwalks",
                        Reason: "Scheduler-dispatch wait reasons and ReadyThread events were present, but ReadyThread events did not carry call stacks.",
                        Pid: candidate.Pid,
                        BlockingCapability: nameof(TraceCapabilities.HasReadyThreadStacks),
                        RelatedCallId: waitCallId,
                        MetricName: "schedulerWaitBlockedPct",
                        MetricValue: schedulerWaitPct,
                        Unit: "ratio",
                        ObservedPct: schedulerWaitPct,
                        ThresholdPct: ReadyThreadSchedulerThresholdPct));
                }
                else if (BudgetExpired())
                {
                    AddBudgetExhausted(candidate.Pid, "ready_thread_top_stacks");
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

            if (capabilities.HasCSwitch && capabilities.HasCSwitchStacks)
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

            if (shouldRunReadyThread && capabilities.HasReadyThread && capabilities.HasReadyThreadStacks)
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

    private static DiagnoseWindowResponse EmptyDiagnoseWindow(
        long startUs,
        long endUs,
        int? pid,
        IReadOnlyList<WindowEvidenceRow> evidence,
        IReadOnlyList<CompositeNotConcluded> notConcluded,
        IReadOnlyList<CompositeNextTool> nextTools,
        IReadOnlyList<CompositeToolCall> executedCalls,
        IReadOnlyList<string> warnings)
        => new(
            WindowStartUs: startUs,
            WindowEndUs: endUs,
            DurationUs: endUs - startUs,
            Pid: pid,
            HardFaultsByBytes: Array.Empty<HardFaultFileRow>(),
            HardFaultsByMaxLatency: Array.Empty<HardFaultFileRow>(),
            FileIoTopFiles: Array.Empty<FileIoRow>(),
            Pressure: null,
            SecurityScanTargets: Array.Empty<SecurityScanTargetRow>(),
            SlowScans: Array.Empty<SecurityScanRequestRow>(),
            SecurityMatchedEventCount: 0,
            SecurityPairedScanCount: 0,
            SecurityTotalDurationUs: 0,
            Waits: Array.Empty<WaitAnalysisRow>(),
            Evidence: evidence,
            NotConcluded: notConcluded,
            NextTools: nextTools,
            ExecutedToolCalls: executedCalls,
            Warnings: warnings);

    private static void AddWindowCall(
        List<CompositeToolCall> executedCalls,
        List<string> warnings,
        string callId,
        string toolName,
        int? pid,
        long startUs,
        long endUs,
        int top,
        IReadOnlyList<string> toolWarnings,
        string? orderBy = null)
    {
        executedCalls.Add(ToolCall(
            callId,
            toolName,
            pid,
            awakenedPid: null,
            startUs,
            endUs,
            top,
            compactStacks: null,
            summaryOnly: null,
            whenBuckets: null,
            warnings: toolWarnings,
            orderBy: orderBy));
        warnings.AddRange(PrefixWarnings(toolName, toolWarnings));
    }

    private static void AddNoSignal(
        List<CompositeNotConcluded> notConcluded,
        string code,
        string reason,
        int? pid,
        string relatedCallId)
        => notConcluded.Add(new CompositeNotConcluded(
            Code: code,
            Reason: reason,
            Pid: pid,
            BlockingCapability: null,
            RelatedCallId: relatedCallId));

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
        string? internalNote = null,
        string? orderBy = null)
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
            InternalNote: internalNote,
            OrderBy: orderBy);

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
