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
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequirePidTid(pid, tid: null);
        Validation.RequireTop(top);
        if (maxWindowDurationUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWindowDurationUs), "must be positive");

        if (BuildWideWindowGuard(startUs, endUs, pid, maxWindowDurationUs) is { } guarded)
            return guarded;

        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxWindowDurationUs);
        return BuildDiagnoseWindow(
            trace, window.StartUs, window.EndUs, pid, top, maxWindowDurationUs,
            callPrefix: "diagnose-window");
    }

    private static DiagnoseWindowResponse? BuildWideWindowGuard(
        long startUs,
        long endUs,
        int? pid,
        long maxWindowDurationUs)
    {
        var durationUs = endUs - startUs;
        if (durationUs <= maxWindowDurationUs)
            return null;

        var warning = $"diagnose_window window is {durationUs}us, above maxWindowDurationUs={maxWindowDurationUs}; narrow the window or call analyzers individually.";
        return EmptyDiagnoseWindow(
            startUs,
            endUs,
            pid,
            Array.Empty<WindowEvidenceRow>(),
            new[]
            {
                new CompositeNotConcluded(
                    Code: "window_too_wide",
                    Reason: warning,
                    Pid: pid,
                    BlockingCapability: null,
                    RelatedCallId: null,
                    MetricName: "windowDurationUs",
                    MetricValue: durationUs,
                    Unit: "us",
                    ObservedPct: durationUs / (double)maxWindowDurationUs,
                    ThresholdPct: 1.0),
            },
            Array.Empty<CompositeNextTool>(),
            Array.Empty<CompositeToolCall>(),
            new[] { warning });
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

        if (BuildWideWindowGuard(startUs, endUs, pid, maxWindowDurationUs) is { } guarded)
            return guarded;

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
        "Composite 'why is process X slow to start' analysis. Includes only process instances with an " +
        "observed ProcessStart, ranks them from CPU and wall time inside one bounded startup window, and " +
        "projects wait reasons and image loads from that same process-instance window. CPU functions use " +
        "the identical scope. A sufficiently slow first ImageLoad may add a contained diagnose_window child. " +
        "No startUs/endUs: this composite derives each checked half-open window from ProcessStart and " +
        "startupWindowUs; lifetime metrics are auxiliary and never affect ranking.")]
    public DiagnoseSlowStartupResponse DiagnoseSlowStartup(
        [Description("Absolute path to .etl file")] string path,
        [Description("Match candidates whose process name contains this substring (case-insensitive). " +
                     "Empty/null = rank all observed-start candidates by startup-window wait ratio.")]
        string? nameSubstring = null,
        [Description("How many candidate processes to investigate (default 5, max 20)")] int maxCandidates = 5,
        [Description("Minimum ObservedStartupWallUs / StartupCpuUs ratio to consider a process 'slow' (default 3.0)")]
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
        if (nameSubstring is not null)
            Validation.RequireText(nameSubstring, allowEmpty: true);
        if (maxCandidates <= 0 || maxCandidates > 20)
            throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        if (minWaitRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(minWaitRatio));
        if (startupWindowUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(startupWindowUs));
        if (slowFirstImageLoadThresholdUs < 0)
            throw new ArgumentOutOfRangeException(nameof(slowFirstImageLoadThresholdUs));
        Validation.RequireTop(topImageLoads);
        Validation.RequireTop(topCpu);
        Validation.RequireTop(topWindowEvidence);
        if (maxWindowDurationUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWindowDurationUs), "must be positive");

        var trace = _cache.Get(path);
        var identities = TraceIdentityIndex.For(trace);
        var catalog = StartupProcessCatalog.FromTrace(
            trace,
            identities,
            startupWindowUs,
            nameSubstring,
            Validation.MaxCollectionItems);
        var startupMetrics = new StartupMetricsAccumulator(catalog.Eligible);
        var schedulerStream = SchedulerIntervalTraceReader.Read(
            trace, identities, [startupMetrics]);
        var scheduler = startupMetrics.Complete();
        var schedulerWarnings = WaitAnalysis.BuildSchedulerWarnings(
            schedulerStream.Completion,
            schedulerStream.IdentityDiagnosticCount);
        var imageLoads = StartupImageLoadAnalysis.Collect(
            trace,
            identities,
            catalog.Eligible,
            maxRowsPerProcess: topImageLoads);

        return ComposeSlowStartup(
            identities,
            catalog,
            scheduler,
            schedulerWarnings,
            imageLoads,
            nameSubstring,
            maxCandidates,
            minWaitRatio,
            topImageLoads,
            topCpu,
            slowFirstImageLoadThresholdUs,
            topWindowEvidence,
            analyzeCpu: scope => CpuAnalysis.TopFunctions(
                trace,
                top: topCpu,
                scope,
                symbolLog: Console.Error,
                excludeEtwSelfOverhead: false),
            diagnoseWindow: (candidate, child, prefix) => BuildDiagnoseWindow(
                trace,
                child.StartUs,
                child.EndUs,
                candidate.Process.Pid,
                topWindowEvidence,
                maxWindowDurationUs,
                callPrefix: $"{prefix}.first-image-load-gap"));
    }

    internal static DiagnoseSlowStartupResponse ComposeSlowStartup(
        TraceIdentityIndex identities,
        StartupProcessCatalogResult catalog,
        IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> scheduler,
        IReadOnlyList<string> schedulerWarnings,
        StartupImageLoadResult imageLoads,
        string? nameSubstring,
        int maxCandidates,
        double minWaitRatio,
        int topImageLoads,
        int topCpu,
        long slowFirstImageLoadThresholdUs,
        int topWindowEvidence,
        Func<ThreadAnalysisScope, CpuTopFunctionsResponse> analyzeCpu,
        Func<SlowStartupCandidateData, TimeWindow, string, DiagnoseWindowResponse>
            diagnoseWindow)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(schedulerWarnings);
        ArgumentNullException.ThrowIfNull(imageLoads);
        ArgumentNullException.ThrowIfNull(analyzeCpu);
        ArgumentNullException.ThrowIfNull(diagnoseWindow);

        var excludedSamples = catalog.Excluded
            .Select(exclusion => new StartupProcessExclusionRow(
                EvidenceId:
                    $"slow-startup.pid-{exclusion.Process.Pid}.start-{exclusion.Process.StartUs}.exclusion-sample",
                Pid: exclusion.Process.Pid,
                ProcessStartUs: exclusion.Process.StartUs,
                ProcessName: exclusion.ProcessName,
                Code: exclusion.Code))
            .ToList();
        var discovery = new StartupDiscoverySummary(
            EligibleStartupInstanceCount: catalog.TotalEligibleCount,
            ConsideredStartupInstanceCount: catalog.Eligible.Count,
            CandidateInputHasMore: catalog.EligibleHasMore,
            ExcludedUnobservedStartCount: catalog.TotalUnobservedStartCount,
            OtherExcludedStartupInstanceCount: catalog.TotalOtherExcludedCount,
            ExcludedSamples: excludedSamples,
            ExcludedSamplesHasMore: catalog.ExcludedHasMore);

        var warnings = new List<string>();
        var evidence = new List<CompositeEvidence>();
        var notConcluded = new List<CompositeNotConcluded>();
        var nextTools = new List<CompositeNextTool>();
        var firstImageLoadGapEvidence = new List<StartupGapEvidenceRow>();
        var executedCalls = new List<CompositeToolCall>();

        if (catalog.ExplicitNameTarget)
        {
            foreach (var exclusion in catalog.Excluded.Where(
                         exclusion => exclusion.Code == "startup_start_not_observed"))
            {
                var exclusionId =
                    $"slow-startup.pid-{exclusion.Process.Pid}.start-{exclusion.Process.StartUs}.startup-start";
                notConcluded.Add(new CompositeNotConcluded(
                    Code: exclusion.Code,
                    Reason: exclusion.Reason,
                    Pid: exclusion.Process.Pid,
                    BlockingCapability: null,
                    RelatedCallId: null,
                    ProcessStartUs: exclusion.Process.StartUs,
                    EvidenceId: exclusionId));
            }
        }
        else if (catalog.TotalUnobservedStartCount > 0)
        {
            notConcluded.Add(new CompositeNotConcluded(
                Code: "startup_starts_not_observed",
                Reason: "Some process instances were already present without an observed ProcessStart and were excluded.",
                Pid: null,
                BlockingCapability: null,
                RelatedCallId: null,
                MetricName: "excludedUnobservedStartCount",
                MetricValue: catalog.TotalUnobservedStartCount,
                Unit: "process_instances",
                EvidenceId: "slow-startup.discovery.startup-starts-not-observed"));
        }

        warnings.AddRange(PrefixWarnings("slow-startup.discovery", schedulerWarnings));
        if (imageLoads.UnresolvedProcessInstanceCount > 0)
        {
            warnings.Add(
                $"slow-startup.discovery: image_load_process_unresolved: {imageLoads.UnresolvedProcessInstanceCount:N0} event(s) were not attributed to a process instance.");
        }
        if (imageLoads.AmbiguousProcessInstanceCount > 0)
        {
            warnings.Add(
                $"slow-startup.discovery: image_load_process_ambiguous: {imageLoads.AmbiguousProcessInstanceCount:N0} event(s) matched multiple process instances.");
        }

        var ranked = SlowStartupProjection.Rank(
            catalog.Eligible,
            scheduler,
            imageLoads.ByProcess,
            nameSubstring,
            minWaitRatio,
            maxCandidates);

        if (ranked.Count == 0)
        {
            warnings.Add(
                $"No processes matched (nameSubstring='{nameSubstring ?? "<any>"}', " +
                $"minWaitRatio={minWaitRatio}) using observed startup-window metrics. " +
                "Try lowering minWaitRatio or removing nameSubstring.");
            notConcluded.Add(new CompositeNotConcluded(
                Code: "no_candidates",
                Reason: "No observed-start process instance matched the configured nameSubstring and minWaitRatio filters.",
                Pid: null,
                BlockingCapability: null,
                RelatedCallId: null,
                EvidenceId: "slow-startup.no-candidates"));
            return new DiagnoseSlowStartupResponse(
                Candidates: Array.Empty<SlowStartupCandidate>(),
                Summary: "No candidates above minWaitRatio.",
                Warnings: warnings,
                Evidence: evidence,
                NotConcluded: notConcluded,
                ExecutedToolCalls: executedCalls,
                NextTools: nextTools,
                FirstImageLoadGapEvidence: firstImageLoadGapEvidence,
                Discovery: discovery);
        }

        var candidates = new List<SlowStartupCandidate>();
        foreach (var c in ranked)
        {
            var plan = SlowStartupProjection.PlanEvidence(
                c, slowFirstImageLoadThresholdUs);
            var prefix = plan.EvidenceIdPrefix;
            var bounds = plan.ParentWindow;
            var provenance = StartupProvenance(c.StartupWindow);
            var scope = ThreadAnalysisScope.ResolveRequired(
                bounds,
                c.Process.Pid,
                tid: null,
                c.Process.StartUs,
                threadStartUs: null,
                identities);
            var collapsedReasons = StartupWaitReasons(
                c,
                scheduler[c.Process],
                top: 5);

            var projectionCallId = $"{prefix}.startup-candidate-projection";
            var waitCallId = $"{prefix}.wait-analysis";
            var imageCallId = $"{prefix}.image-load-timing";
            var cpuCallId = $"{prefix}.cpu-top-functions";

            executedCalls.Add(ToolCall(
                projectionCallId,
                "startup_candidate_projection",
                pid: c.Process.Pid,
                awakenedPid: null,
                startUs: bounds.StartUs,
                endUs: bounds.EndUs,
                top: null,
                compactStacks: null,
                summaryOnly: null,
                whenBuckets: null,
                warnings: Array.Empty<string>(),
                replayable: false,
                internalNote: "Internal startup-window candidate projection.",
                processStartUs: c.Process.StartUs));
            executedCalls.Add(ToolCall(
                waitCallId,
                "wait_analysis",
                pid: c.Process.Pid,
                awakenedPid: null,
                startUs: bounds.StartUs,
                endUs: bounds.EndUs,
                top: Validation.MaxTop,
                compactStacks: null,
                summaryOnly: null,
                whenBuckets: null,
                warnings: schedulerWarnings,
                processStartUs: c.Process.StartUs));
            executedCalls.Add(ToolCall(
                imageCallId,
                "image_load_timing",
                pid: c.Process.Pid,
                awakenedPid: null,
                startUs: bounds.StartUs,
                endUs: bounds.EndUs,
                top: topImageLoads,
                compactStacks: null,
                summaryOnly: null,
                whenBuckets: null,
                warnings: Array.Empty<string>(),
                replayable: false,
                internalNote: "Instance-scoped startup projection; the public image_load_timing surface has no processStartUs/window selector.",
                processStartUs: c.Process.StartUs));

            IReadOnlyList<CpuFunctionRow>? topCpuRows = null;
            var cpuWarnings = new List<string>();
            try
            {
                var cpuResp = analyzeCpu(scope);
                topCpuRows = cpuResp.Rows;
                cpuWarnings.AddRange(cpuResp.Warnings);
                warnings.AddRange(PrefixWarnings(prefix, cpuResp.Warnings));
            }
            catch (Exception ex)
            {
                var warning = $"cpu_top_functions: {ex.Message}";
                cpuWarnings.Add(warning);
                warnings.Add($"{prefix}: {warning}");
            }
            executedCalls.Add(ToolCall(
                cpuCallId,
                "cpu_top_functions",
                pid: c.Process.Pid,
                awakenedPid: null,
                startUs: bounds.StartUs,
                endUs: bounds.EndUs,
                top: topCpu,
                compactStacks: null,
                summaryOnly: null,
                whenBuckets: null,
                warnings: cpuWarnings,
                processStartUs: c.Process.StartUs));

            candidates.Add(new SlowStartupCandidate(
                EvidenceId: $"{prefix}.candidate",
                Pid: c.Process.Pid,
                ProcessStartUs: c.Process.StartUs,
                ParentPid: c.ParentPid,
                Name: c.Name,
                StartupEndUs: bounds.EndUs,
                ObservedStartupWallUs: c.ObservedStartupWallUs,
                StartupCpuUs: c.StartupCpuUs,
                StartupBlockedUs: c.StartupBlockedUs,
                StartupWaitRatio: c.StartupWaitRatio,
                StartupImageLoadCount: c.StartupImageLoadCount,
                StartupImageLoadsHasMore: c.StartupImageLoadsHasMore,
                TopStartupWaitReasons: collapsedReasons,
                FirstStartupImageLoads: c.StartupImageLoads,
                TopStartupCpuFunctions: topCpuRows,
                Window: provenance,
                LifetimeWallUs: c.LifetimeWallUs,
                LifetimeCpuUs: c.LifetimeCpuUs,
                LifetimeWaitRatio: c.LifetimeWaitRatio,
                LifetimeImageLoadCount: c.LifetimeImageLoadCount));
            evidence.Add(ProcessWaitEvidence(
                evidenceId: $"{prefix}.wait-summary",
                callId: waitCallId,
                pid: c.Process.Pid,
                processName: c.Name,
                cpuUs: c.StartupCpuUs,
                blockedUs: c.StartupBlockedUs,
                waitReasons: collapsedReasons,
                processStartUs: c.Process.StartUs));

            if (plan.NotConcludedCode is not null)
            {
                notConcluded.Add(new CompositeNotConcluded(
                    Code: plan.NotConcludedCode,
                    Reason: "No instance-resolved ImageLoad event was observed inside this startup window.",
                    Pid: c.Process.Pid,
                    BlockingCapability: null,
                    RelatedCallId: imageCallId,
                    ProcessStartUs: c.Process.StartUs,
                    EvidenceId: $"{prefix}.first-image-load"));
            }
            else if (plan.FirstImageChildWindow is { } child)
            {
                var firstLoad = c.StartupImageLoads[0];
                var callId = $"{prefix}.first-image-load-gap.diagnose-window";
                var window = diagnoseWindow(c, child, prefix);

                executedCalls.Add(ToolCall(
                    callId,
                    "diagnose_window",
                    pid: c.Process.Pid,
                    awakenedPid: null,
                    startUs: child.StartUs,
                    endUs: child.EndUs,
                    top: topWindowEvidence,
                    compactStacks: null,
                    summaryOnly: null,
                    whenBuckets: null,
                    warnings: window.Warnings,
                    replayable: false,
                    internalNote: "Uses caller-supplied maxWindowDurationUs, which is not represented in this audit call.",
                    processStartUs: c.Process.StartUs,
                    parentStartUs: bounds.StartUs,
                    parentEndUs: bounds.EndUs));
                warnings.AddRange(PrefixWarnings(prefix, window.Warnings));
                nextTools.AddRange(window.NextTools);
                firstImageLoadGapEvidence.Add(new StartupGapEvidenceRow(
                    EvidenceId: $"{prefix}.first-image-load-gap",
                    CallId: callId,
                    Pid: c.Process.Pid,
                    ProcessStartUs: c.Process.StartUs,
                    ProcessName: c.Name,
                    FirstImageLoadTimeUs: firstLoad.TimeUs,
                    FirstImageLoadOffsetUs: firstLoad.TimeFromProcessStartUs,
                    ParentWindow: provenance,
                    ChildStartUs: child.StartUs,
                    ChildEndUs: child.EndUs,
                    Window: window));
            }
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
                Unit: "us",
                EvidenceId: "slow-startup.no-slow-first-image-load-gaps"));
        }

        return new DiagnoseSlowStartupResponse(
            Candidates: candidates,
            Summary: BuildSummary(candidates),
            Warnings: warnings,
            Evidence: evidence,
            NotConcluded: notConcluded,
            ExecutedToolCalls: executedCalls,
            NextTools: nextTools,
            FirstImageLoadGapEvidence: firstImageLoadGapEvidence,
            Discovery: discovery);
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
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequirePidTid(pid, tid: null);
        if (maxCandidates <= 0 || maxCandidates > 20)
            throw new ArgumentOutOfRangeException(nameof(maxCandidates), "must be in [1, 20]");
        Validation.RequireTop(topStacks);
        Validation.RequireTop(topReadyStacks);
        Validation.RequireTimeBudgetMs(timeBudgetMs);

        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        startUs = window.StartUs;
        endUs = window.EndUs;
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
            var ratioStr = c.StartupWaitRatio is { } r ? $"{r:F1}x" : "n/a";
            sb.AppendLine(
                $"  - pid {c.Pid} start={c.ProcessStartUs} ({c.Name}): " +
                $"startup_wall={c.ObservedStartupWallUs / 1000.0:F1}ms, " +
                $"startup_cpu={c.StartupCpuUs / 1000.0:F1}ms, " +
                $"startup_wait_ratio={ratioStr}");
            if (c.TopStartupWaitReasons.Count > 0)
            {
                var reasons = string.Join(
                    ", ",
                    c.TopStartupWaitReasons.Take(3).Select(bucket => bucket.Reason));
                sb.AppendLine($"    top wait reasons: {reasons}");
            }
        }
        return sb.ToString();
    }

    private static StartupWindowProvenance StartupProvenance(
        StartupWindow window) =>
        new(
            Pid: window.Process.Pid,
            ProcessStartUs: window.Process.StartUs,
            StartUs: window.Bounds.StartUs,
            EndUs: window.Bounds.EndUs,
            RequestedEndUs: window.RequestedEndUs,
            TraceDurationUs: window.TraceDurationUs,
            ProcessStartObserved: window.ProcessStartObserved,
            ProcessEndObserved: window.ProcessEndObserved,
            Status: window.Status,
            Code: window.Code);

    private static IReadOnlyList<WaitReasonBucket> StartupWaitReasons(
        SlowStartupCandidateData candidate,
        StartupSchedulerMetrics metrics,
        int top) =>
        candidate.StartupBlockedUsByReason
            .Select(item => new WaitReasonBucket(
                Reason: item.Key,
                BlockedUs: item.Value,
                Count: metrics.BlockedCountByReason is not null &&
                       metrics.BlockedCountByReason.TryGetValue(
                           item.Key, out var count)
                    ? count
                    : 0))
            .OrderByDescending(reason => reason.BlockedUs)
            .ThenBy(reason => reason.Reason, StringComparer.Ordinal)
            .Take(top)
            .ToList();

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
        string? orderBy = null,
        long? processStartUs = null,
        long? parentStartUs = null,
        long? parentEndUs = null)
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
            OrderBy: orderBy,
            ProcessStartUs: processStartUs,
            ParentStartUs: parentStartUs,
            ParentEndUs: parentEndUs);

    private static CompositeEvidence ProcessWaitEvidence(
        string evidenceId,
        string callId,
        int pid,
        string processName,
        long cpuUs,
        long blockedUs,
        IReadOnlyList<WaitReasonBucket> waitReasons,
        long? processStartUs = null)
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
            Frames: Array.Empty<FrameMetric>(),
            ProcessStartUs: processStartUs);

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
