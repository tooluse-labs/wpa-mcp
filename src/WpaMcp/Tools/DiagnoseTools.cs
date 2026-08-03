using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Diagnostics.Tracing.Etlx;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tools;

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

    private static readonly Lazy<QueryPlanner> Planner = new(
        static () => new QueryPlanner(ActiveToolCatalog.LoadAndValidate()),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly TraceCache _cache;
    private readonly IPrivacyLogSink _privacyLog;
    public DiagnoseTools(TraceCache cache, IPrivacyLogSink? privacyLog = null)
    {
        _cache = cache;
        _privacyLog = privacyLog ?? PassThroughPrivacyLogSink.Instance;
    }

    [McpServerTool(
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true), Description(
        "Windowed evidence composite for a specific trace interval. Aggregates per-file hard faults " +
        "by bytes and max latency, top file IO, memory-pressure samples, security-scan evidence, " +
        "and wait_analysis rows for one shared process selector and window. processStartUs selects " +
        "one lifetime; PID-only reuse is labeled as an aggregate. No root-cause verdict: compare " +
        "the facts and use NextTools for bounded hypothesis checks. PlannerExecution reports that " +
        "shared dispatch is not yet admitted; planner pass/scan/match counts remain unavailable rather than fabricated.")]
    public DiagnoseWindowResponse DiagnoseWindow(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Window start in microseconds since trace start. Required.")]
        long startUs,
        [Description("Window end in microseconds since trace start (exclusive). Required.")]
        long endUs,
        [Description("Optional process ID filter. Null aggregates all processes in the window.")]
        int? pid = null,
        [Description("Top N rows per evidence section (default 10, max 1000).")]
        int top = 10,
        [Description("Maximum allowed window width in microseconds (default 60s). Wider windows return a guard warning.")]
        long maxWindowDurationUs = DefaultDiagnoseWindowLimitUs,
        [Description("Optional exact process start in trace-relative microseconds; requires pid. PID-only calls explicitly aggregate reused lifetimes.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        if (maxWindowDurationUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWindowDurationUs), "must be positive");

        if (BuildWideWindowGuard(
                startUs, endUs, pid, maxWindowDurationUs, processStartUs) is { } guarded)
            return AttachPlannerBoundary(guarded);

        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxWindowDurationUs);
        var scope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, TraceIdentityIndex.For(trace));
        return AttachPlannerBoundary(BuildDiagnoseWindow(
            trace, scope, top, maxWindowDurationUs,
            callPrefix: "diagnose-window"));
    }

    private static DiagnoseWindowResponse? BuildWideWindowGuard(
        long startUs,
        long endUs,
        int? pid,
        long maxWindowDurationUs,
        long? processStartUs = null,
        ProcessAnalysisScope? scope = null)
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
                    ThresholdPct: 1.0,
                    ProcessStartUs: processStartUs,
                    ScopeStatus: scope?.ScopeStatus ?? "not_evaluated",
                    CapabilityStatus: "unknown",
                    NoDataReason: "window_too_wide"),
            },
            Array.Empty<CompositeNextTool>(),
            Array.Empty<CompositeToolCall>(),
            new[] { warning },
            selectedProcess: scope?.SelectedProcess,
            scopeMode: scope?.ScopeMode ?? "not_evaluated",
            pidReuseObserved: scope?.PidReuseObserved ?? false,
            includedProcesses: scope?.Pid.HasValue == true
                ? scope.IncludedProcesses
                : Array.Empty<ProcessInstanceKey>(),
            scopeStatus: scope?.ScopeStatus ?? "not_evaluated",
            capabilityStatus: "unknown",
            matchedEventCount: 0,
            noDataReason: "window_too_wide");
    }

    private static DiagnoseWindowResponse BuildDiagnoseWindow(
        TraceLog trace,
        ProcessAnalysisScope scope,
        int top,
        long maxWindowDurationUs,
        string callPrefix)
    {
        var startUs = scope.Window.StartUs;
        var endUs = scope.Window.EndUs;
        var pid = scope.Pid;
        var processStartUs = scope.SelectedProcess?.StartUs ?? scope.ProcessStartUs;
        var includedProcesses = pid.HasValue
            ? scope.IncludedProcesses
            : Array.Empty<ProcessInstanceKey>();
        var durationUs = endUs - startUs;
        var warnings = new List<string>();
        var notConcluded = new List<CompositeNotConcluded>();
        var nextTools = new List<CompositeNextTool>();
        var executedCalls = new List<CompositeToolCall>();
        var evidence = new List<WindowEvidenceRow>();

        if (BuildWideWindowGuard(
                startUs, endUs, pid, maxWindowDurationUs, processStartUs, scope) is { } guarded)
            return guarded;

        if (!scope.IsResolved)
        {
            var warning = ProcessAnalysisScope.ResolutionFailureWarning(
                scope.ScopeStatus);
            return EmptyDiagnoseWindow(
                startUs,
                endUs,
                pid,
                Array.Empty<WindowEvidenceRow>(),
                new[]
                {
                    new CompositeNotConcluded(
                        Code: scope.ScopeStatus,
                        Reason: warning,
                        Pid: pid,
                        BlockingCapability: null,
                        RelatedCallId: null,
                        ProcessStartUs: processStartUs,
                        ScopeStatus: scope.ScopeStatus,
                        CapabilityStatus: "unknown",
                        NoDataReason: scope.ScopeStatus),
                },
                Array.Empty<CompositeNextTool>(),
                Array.Empty<CompositeToolCall>(),
                new[] { warning },
                selectedProcess: scope.SelectedProcess,
                scopeMode: scope.ScopeMode,
                pidReuseObserved: scope.PidReuseObserved,
                includedProcesses: includedProcesses,
                scopeStatus: scope.ScopeStatus,
                capabilityStatus: "unknown",
                matchedEventCount: 0,
                noDataReason: scope.ScopeStatus);
        }

        var hardFaultBytes = HardFaultByFileAnalysis.Analyze(
            trace, top, pid, "bytes", startUs, endUs, processStartUs);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.hard_fault_by_file.bytes", "hard_fault_by_file", pid, startUs, endUs, top, hardFaultBytes.Warnings, orderBy: "bytes", processStartUs: processStartUs);
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
                TimeUs: null,
                Details: new[]
                {
                    $"pageInCount={topHardFaultBytes.PageInCount}",
                    $"maxLatencyUs={topHardFaultBytes.MaxLatencyUs}",
                    $"nonRepresentativePointOnly: maxLatencyTimeUs={topHardFaultBytes.MaxLatencyTimeUs} is the timestamp of the largest single fault for this file; aggregate pageInBytes is not anchored to that point.",
                },
                Samples: Array.Empty<WindowEvidenceSample>(),
                SamplesBoundary: null,
                ProcessStartUs: processStartUs,
                ScopeMode: scope.ScopeMode,
                EvidenceId: $"{callPrefix}.evidence.hard-fault-bytes",
                CallId: $"{callPrefix}.hard_fault_by_file.bytes"));
        }
        else
        {
            AddChildNotConcluded(
                notConcluded,
                fallbackCode: "no_hard_fault_bytes",
                fallbackReason: "No hard-fault page-in bytes matched this process scope/window.",
                pid,
                processStartUs,
                relatedCallId: $"{callPrefix}.hard_fault_by_file.bytes",
                eventFamily: "MemoryHardFault",
                hardFaultBytes.ScopeStatus,
                hardFaultBytes.CapabilityStatus,
                hardFaultBytes.NoDataReason);
        }

        var hardFaultLatency = HardFaultByFileAnalysis.Analyze(
            trace, top, pid, "max_latency", startUs, endUs, processStartUs);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.hard_fault_by_file.max_latency", "hard_fault_by_file", pid, startUs, endUs, top, hardFaultLatency.Warnings, orderBy: "max_latency", processStartUs: processStartUs);
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
                },
                Samples: Array.Empty<WindowEvidenceSample>(),
                SamplesBoundary: null,
                ProcessStartUs: processStartUs,
                ScopeMode: scope.ScopeMode,
                EvidenceId: $"{callPrefix}.evidence.hard-fault-max-latency",
                CallId: $"{callPrefix}.hard_fault_by_file.max_latency"));

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
                TestsHypothesis: "Check whether file IO, waits, memory pressure, or scan events cluster around the page-in stall; coincidence alone does not establish cause.",
                ProcessStartUs: processStartUs));
        }
        else
        {
            AddChildNotConcluded(
                notConcluded,
                fallbackCode: "no_hard_fault_latency",
                fallbackReason: "No hard-fault latency rows matched this process scope/window.",
                pid,
                processStartUs,
                relatedCallId: $"{callPrefix}.hard_fault_by_file.max_latency",
                eventFamily: "MemoryHardFault",
                hardFaultLatency.ScopeStatus,
                hardFaultLatency.CapabilityStatus,
                hardFaultLatency.NoDataReason);
        }

        var fileIo = FileIoAnalysis.TopFiles(
            trace, top, pid, startUs, endUs, processStartUs);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.file_io_top_files", "file_io_top_files", pid, startUs, endUs, top, fileIo.Warnings ?? Array.Empty<string>(), processStartUs: processStartUs);
        if (fileIo.Rows.FirstOrDefault() is { } topFile)
        {
            var bytes = checked(topFile.ReadBytes + topFile.WriteBytes);
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
                },
                Samples: Array.Empty<WindowEvidenceSample>(),
                SamplesBoundary: null,
                ProcessStartUs: processStartUs,
                ScopeMode: scope.ScopeMode,
                EvidenceId: $"{callPrefix}.evidence.file-io-top-file",
                CallId: $"{callPrefix}.file_io_top_files"));
        }
        else
        {
            AddChildNotConcluded(
                notConcluded,
                fallbackCode: "no_file_io",
                fallbackReason: "No file IO rows matched this process scope/window.",
                pid,
                processStartUs,
                relatedCallId: $"{callPrefix}.file_io_top_files",
                eventFamily: "FileIO Read/Write",
                fileIo.ScopeStatus,
                fileIo.CapabilityStatus,
                fileIo.NoDataReason);
        }

        var memory = MemoryResourceAnalysis.Analyze(
            trace, top, pid, startUs, endUs, processStartUs);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.memory_resource_analysis", "memory_resource_analysis", pid, startUs, endUs, top, memory.Warnings, processStartUs: processStartUs);
        if (memory.Pressure.MinFreeBytes is { } minFreeBytes)
        {
            evidence.Add(new WindowEvidenceRow(
                EvidenceType: "memory_pressure",
                Label: "Window-global minimum observed free memory; not process attribution",
                MetricName: "minFreeBytes",
                MetricValue: minFreeBytes,
                Unit: "bytes",
                Pid: null,
                ProcessName: null,
                File: null,
                TimeUs: memory.Pressure.MinFreeTimeUs,
                Details:
                [
                    $"systemSampleCount={memory.Pressure.SystemSampleCount}",
                    "This system-memory sample is window-global and does not attribute pressure to the selected process.",
                ],
                Samples: Array.Empty<WindowEvidenceSample>(),
                SamplesBoundary: null,
                ProcessStartUs: null,
                ScopeMode: "window_global",
                EvidenceScope: "window_global",
                EvidenceId: $"{callPrefix}.evidence.memory-pressure",
                CallId: $"{callPrefix}.memory_resource_analysis"));
        }
        if (memory.NoDataReason is not null ||
            (memory.Pressure.ProcessSnapshotBatchCount == 0 &&
             memory.Pressure.SystemSampleCount == 0))
        {
            AddChildNotConcluded(
                notConcluded,
                fallbackCode: "no_memory_samples",
                fallbackReason: "No process-scoped memory resource events matched this process scope/window.",
                pid,
                processStartUs,
                relatedCallId: $"{callPrefix}.memory_resource_analysis",
                eventFamily: "memory resource",
                memory.ScopeStatus,
                memory.CapabilityStatus,
                memory.NoDataReason);
        }

        var securityDetailed = SecurityScanAnalysis.AnalyzeDetailed(
            trace, top, pid, startUs, endUs,
            processSubstring: null,
            pathSubstring: null,
            providerSubstring: null,
            targetProcessStartUs: processStartUs);
        var security = securityDetailed.Response;
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.security_scan_analysis", "security_scan_analysis", pid, startUs, endUs, top, security.Warnings, targetProcessStartUs: processStartUs);
        var hasSecurityEvidence = false;
        if (security.PairedScanCount > 0)
        {
            evidence.Add(BuildSecurityDurationEvidence(
                security,
                pid) with
            {
                EvidenceId = $"{callPrefix}.evidence.security-scan-duration",
                CallId = $"{callPrefix}.security_scan_analysis",
            });
            hasSecurityEvidence = true;
        }

        var securityPresence = BuildSecurityPresenceEvidence(
            securityDetailed,
            pid)
            .Select((item, index) => item with
            {
                EvidenceId = $"{callPrefix}.evidence.security-scan-presence-{index}",
                CallId = $"{callPrefix}.security_scan_analysis",
            })
            .ToArray();
        evidence.AddRange(securityPresence);
        hasSecurityEvidence |= securityPresence.Length > 0;

        if (!hasSecurityEvidence)
        {
            AddChildNotConcluded(
                notConcluded,
                fallbackCode: "no_security_scan_events",
                fallbackReason: "No security scan evidence matched this process scope/window.",
                pid,
                processStartUs,
                relatedCallId: $"{callPrefix}.security_scan_analysis",
                eventFamily: "security scan evidence",
                security.ScopeStatus,
                security.CapabilityStatus,
                security.NoDataReason);
        }

        var identities = TraceIdentityIndex.For(trace);
        var waitScopeResolution = ThreadAnalysisScope.Resolve(
            scope.Window,
            pid,
            tid: null,
            processStartUs,
            threadStartUs: null,
            identities);
        var waits = waitScopeResolution.Status == InstanceResolutionStatus.Resolved &&
                    waitScopeResolution.Value.HasValue
            ? WaitAnalysis.Analyze(
                trace, top, waitScopeResolution.Value.Value, scope)
            : WaitAnalysis.EmptyResolutionFailure(
                $"thread_scope_{waitScopeResolution.Status.ToString().ToLowerInvariant()}",
                scope);
        AddWindowCall(executedCalls, warnings, $"{callPrefix}.wait_analysis", "wait_analysis", pid, startUs, endUs, top, waits.Warnings, processStartUs: processStartUs);
        var waitSummary = BuildWaitSummaryEvidence(
            waits,
            pid);
        if (waitSummary is not null)
        {
            evidence.Add(waitSummary with
            {
                EvidenceId = $"{callPrefix}.evidence.wait-summary",
                CallId = $"{callPrefix}.wait_analysis",
            });

            if (waits.HasContextSwitchBlockingStacks)
            {
                nextTools.Add(new CompositeNextTool(
                    ToolName: "wait_top_stacks",
                    Reason: "Expand this scope's blocked-time evidence into captured CSwitch blocking-stack rows.",
                    Pid: pid,
                    AwakenedPid: null,
                    StartUs: startUs,
                    EndUs: endUs,
                    CompactStacks: false,
                    SummaryOnly: false,
                    TestsHypothesis: "Check whether blocked time maps to a specific code path rather than a broad wait-state total.",
                    ProcessStartUs: processStartUs));
            }
            else
            {
                notConcluded.Add(new CompositeNotConcluded(
                    Code: "scoped_wait_stacks_unavailable",
                    Reason: "Blocked time was observed, but no selected CSwitch blocking stack was observed in this process scope/window.",
                    Pid: pid,
                    BlockingCapability: "CSwitch blocking stacks",
                    RelatedCallId: $"{callPrefix}.wait_analysis",
                    MetricName: "scopedStackedSwitches",
                    MetricValue: waits.ScopedStackedSwitches,
                    Unit: "events",
                    ProcessStartUs: processStartUs,
                    ScopeStatus: waits.ScopeStatus,
                    CapabilityStatus: "unknown",
                    NoDataReason: "stacks_unavailable"));
            }
        }
        else
        {
            AddChildNotConcluded(
                notConcluded,
                fallbackCode: "no_wait_rows",
                fallbackReason: "No wait_analysis rows with blocked time matched this process scope/window.",
                pid,
                processStartUs,
                relatedCallId: $"{callPrefix}.wait_analysis",
                eventFamily: "CSwitch",
                waits.ScopeStatus,
                waits.CapabilityStatus,
                waits.NoDataReason);
        }

        var matchedEventCount = hardFaultBytes.MatchedEventCount +
                                fileIo.MatchedEventCount +
                                memory.MatchedEventCount +
                                security.MatchedEventCount +
                                waits.MatchedEventCount;
        var childCapabilityStatuses = new[]
        {
            hardFaultBytes.CapabilityStatus,
            fileIo.CapabilityStatus,
            memory.CapabilityStatus,
            security.CapabilityStatus,
            waits.CapabilityStatus,
        };
        var capabilityStatus = matchedEventCount > 0
            ? "observed"
            : childCapabilityStatuses.All(status => status == "not_observed")
                ? "not_observed"
                : "unknown";
        var childNoDataReasons = new[]
        {
            hardFaultBytes.NoDataReason,
            fileIo.NoDataReason,
            memory.NoDataReason,
            security.NoDataReason,
            waits.NoDataReason,
        };
        var noDataReason = matchedEventCount > 0
            ? null
            : childNoDataReasons.All(reason => reason == "event_class_not_observed")
                ? "event_class_not_observed"
                : "no_events_in_scope";

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
            Warnings: warnings,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: includedProcesses,
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: capabilityStatus,
            MatchedEventCount: matchedEventCount,
            NoDataReason: noDataReason);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Composite startup evidence analysis; it does not return a root-cause verdict. Includes only process instances with an " +
        "observed ProcessStart, ranks them from CPU and wall time inside one bounded startup window, and " +
        "projects wait reasons and image loads from that same process-instance window. CPU functions use " +
        "the identical scope. A sufficiently slow first ImageLoad may add a contained diagnose_window child. " +
        "No startUs/endUs: this composite derives each checked half-open window from ProcessStart and " +
        "startupWindowUs; lifetime metrics are auxiliary and never affect ranking.")]
    public DiagnoseSlowStartupResponse DiagnoseSlowStartup(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
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
        if (maxCandidates <= 0 || maxCandidates > ToolOverfetchExecutionContext.MaximumAllowed(20))
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

        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
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

        return AttachPlannerBoundary(ComposeSlowStartup(
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
                symbolLog: _privacyLog.Writer,
                excludeEtwSelfOverhead: false),
            diagnoseWindow: (candidate, child, prefix) => BuildDiagnoseWindow(
                    trace,
                    ProcessAnalysisScope.Resolve(
                        child,
                        candidate.Process.Pid,
                        candidate.Process.StartUs,
                        identities),
                    topWindowEvidence,
                    maxWindowDurationUs,
                    callPrefix: $"{prefix}.first-image-load-gap")),
            toolName: "diagnose_slow_startup");
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

        const string discoveryCallId = "slow-startup.startup-candidate-discovery";
        var excludedSamples = catalog.Excluded
            .Take(StartupDiscoverySummary.ExcludedSampleLimit)
            .Select(exclusion => new StartupProcessExclusionRow(
                EvidenceId:
                    $"slow-startup.pid-{exclusion.Process.Pid}.start-{exclusion.Process.StartUs}.exclusion-sample",
                CallId: discoveryCallId,
                Pid: exclusion.Process.Pid,
                ProcessStartUs: exclusion.Process.StartUs,
                ProcessName: exclusion.ProcessName,
                Code: exclusion.Code))
            .ToList();
        var discovery = new StartupDiscoverySummary(
            CallId: discoveryCallId,
            EligibleStartupInstanceCount: catalog.TotalEligibleCount,
            ConsideredStartupInstanceCount: catalog.Eligible.Count,
            CandidateInputHasMore: catalog.EligibleHasMore,
            CandidateInputBoundary: new EmbeddedTopNBoundary(
                SectionPointer: "/discovery/candidateInput",
                Requested: Validation.MaxCollectionItems,
                Returned: catalog.Eligible.Count,
                TotalAvailable: catalog.TotalEligibleCount,
                TotalState: ToolSectionTotalState.Exact,
                MoreState: catalog.EligibleHasMore
                    ? ToolSectionMoreState.Present
                    : ToolSectionMoreState.Absent,
                HasMore: catalog.EligibleHasMore,
                ContinuationAvailable: false,
                TruncationReason: catalog.EligibleHasMore ? "fixed_source_limit" : null,
                SortKey: "process_start_us_asc",
                SortDirection: ToolSortDirection.Ascending,
                TieBreakers: ["pid_asc"]),
            ExcludedStartupInstanceCount: checked(
                catalog.TotalUnobservedStartCount + catalog.TotalOtherExcludedCount),
            ExcludedUnobservedStartCount: catalog.TotalUnobservedStartCount,
            OtherExcludedStartupInstanceCount: catalog.TotalOtherExcludedCount,
            ExcludedSamples: excludedSamples,
            ExcludedSamplesHasMore: catalog.ExcludedHasMore ||
                catalog.Excluded.Count > StartupDiscoverySummary.ExcludedSampleLimit);

        var warnings = new List<string>();
        var evidence = new List<CompositeEvidence>();
        var notConcluded = new List<CompositeNotConcluded>();
        var nextTools = new List<CompositeNextTool>();
        var firstImageLoadGapEvidence = new List<StartupGapEvidenceRow>();
        var executedCalls = new List<CompositeToolCall>();
        executedCalls.Add(ToolCall(
            discoveryCallId,
            "startup_candidate_discovery",
            pid: null,
            awakenedPid: null,
            startUs: null,
            endUs: null,
            top: Validation.MaxCollectionItems,
            compactStacks: null,
            summaryOnly: null,
            whenBuckets: null,
            warnings: Array.Empty<string>(),
            replayable: false,
            internalTop: Validation.MaxCollectionItems,
            internalNote:
                "Internal process-instance startup discovery; public replay uses diagnose_slow_startup with the original selectors."));

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
                    BoundaryId: exclusionId));
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
                BoundaryId: "slow-startup.discovery.startup-starts-not-observed"));
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

        var ranking = SlowStartupProjection.RankDetailed(
            catalog.Eligible,
            scheduler,
            imageLoads.ByProcess,
            nameSubstring,
            minWaitRatio,
            maxCandidates);
        var ranked = ranking.Candidates;
        var candidateBoundary = catalog.EligibleHasMore
            ? UnknownEmbeddedBoundary(
                "/candidates",
                maxCandidates,
                ranked.Count,
                "source_limit_saturated",
                "startup_wait_ratio_desc",
                ["observed_startup_wall_us_desc", "process_start_us_asc", "pid_asc"])
            : ExactEmbeddedBoundary(
                "/candidates",
                maxCandidates,
                ranked.Count,
                ranking.QualifiedCandidateCount,
                "startup_wait_ratio_desc",
                ToolSortDirection.Descending,
                ["observed_startup_wall_us_desc", "process_start_us_asc", "pid_asc"]);

        if (catalog.EligibleHasMore)
        {
            var partialReason =
                $"Startup candidate input was capped at {catalog.Eligible.Count:N0} of " +
                $"{catalog.TotalEligibleCount:N0} eligible process instances. " +
                $"qualifiedCandidateCount={ranking.QualifiedCandidateCount:N0} is a lower bound " +
                "(totalState=lower_bound); omitted instances were not ranked.";
            warnings.Add($"slow-startup.discovery: upstream_candidate_input_truncated: {partialReason}");
            notConcluded.Add(new CompositeNotConcluded(
                Code: "upstream_candidate_input_truncated",
                Reason: partialReason,
                Pid: null,
                BlockingCapability: null,
                RelatedCallId: null,
                MetricName: "qualifiedCandidateCountLowerBound",
                MetricValue: ranking.QualifiedCandidateCount,
                Unit: "process_instances",
                BoundaryId: "slow-startup.discovery.candidate-input-truncated",
                CapabilityStatus: "partial",
                NoDataReason: "upstream_candidate_input_truncated"));
        }

        if (ranked.Count == 0)
        {
            var inputWasTruncated = catalog.EligibleHasMore;
            var noCandidateCode = inputWasTruncated
                ? "no_candidates_in_retained_input"
                : "no_candidates";
            var noCandidateReason = inputWasTruncated
                ? "No candidate in the retained startup input matched the configured nameSubstring and minWaitRatio filters; omitted eligible instances were not evaluated, so global absence is not concluded."
                : "No observed-start process instance matched the configured nameSubstring and minWaitRatio filters.";
            warnings.Add(inputWasTruncated
                ? $"No processes in the retained input matched (nameSubstring='{nameSubstring ?? "<any>"}', minWaitRatio={minWaitRatio}); the global result is partial because eligible input was truncated."
                : $"No processes matched (nameSubstring='{nameSubstring ?? "<any>"}', minWaitRatio={minWaitRatio}) using observed startup-window metrics. Try lowering minWaitRatio or removing nameSubstring.");
            notConcluded.Add(new CompositeNotConcluded(
                Code: noCandidateCode,
                Reason: noCandidateReason,
                Pid: null,
                BlockingCapability: null,
                RelatedCallId: null,
                BoundaryId: "slow-startup.no-candidates",
                CapabilityStatus: inputWasTruncated ? "partial" : null,
                NoDataReason: noCandidateCode));
            return new DiagnoseSlowStartupResponse(
                Candidates: Array.Empty<SlowStartupCandidate>(),
                CandidateBoundary: candidateBoundary,
                Summary: inputWasTruncated
                    ? "No candidates in considered startup input; global result is partial."
                    : "No candidates above minWaitRatio.",
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
                warnings: Array.Empty<string>(),
                internalNote: schedulerWarnings.Count == 0
                    ? null
                    : "Scheduler warnings are exposed once in outer Warnings with the discovery prefix.",
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
                internalNote: "Instance-scoped startup projection; processStartUs can be replayed publicly, but this internal projection also applies the candidate window that image_load_timing does not expose.",
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
                warnings: Array.Empty<string>(),
                internalNote: cpuWarnings.Count == 0
                    ? null
                    : "CPU warnings are exposed once in outer Warnings with the candidate prefix.",
                processStartUs: c.Process.StartUs));

            candidates.Add(new SlowStartupCandidate(
                EvidenceId: $"{prefix}.candidate",
                CallId: projectionCallId,
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
                TopStartupWaitReasonsBoundary: new EmbeddedTopNBoundary(
                    SectionPointer: "/topStartupWaitReasons",
                    Requested: 5,
                    Returned: collapsedReasons.Count,
                    TotalAvailable: c.StartupBlockedUsByReason.Count,
                    TotalState: ToolSectionTotalState.Exact,
                    MoreState: c.StartupBlockedUsByReason.Count > collapsedReasons.Count
                        ? ToolSectionMoreState.Present
                        : ToolSectionMoreState.Absent,
                    HasMore: c.StartupBlockedUsByReason.Count > collapsedReasons.Count,
                    ContinuationAvailable: false,
                    TruncationReason: c.StartupBlockedUsByReason.Count > collapsedReasons.Count
                        ? "fixed_source_limit"
                        : null,
                    SortKey: "blocked_us_desc",
                    SortDirection: ToolSortDirection.Descending,
                    TieBreakers: ["reason_ordinal_asc"]),
                FirstStartupImageLoads: c.StartupImageLoads,
                FirstStartupImageLoadsBoundary: new EmbeddedTopNBoundary(
                    SectionPointer: "/firstStartupImageLoads",
                    Requested: topImageLoads,
                    Returned: c.StartupImageLoads.Count,
                    TotalAvailable: c.StartupImageLoadCount,
                    TotalState: ToolSectionTotalState.Exact,
                    MoreState: c.StartupImageLoadsHasMore
                        ? ToolSectionMoreState.Present
                        : ToolSectionMoreState.Absent,
                    HasMore: c.StartupImageLoadsHasMore,
                    ContinuationAvailable: false,
                    TruncationReason: c.StartupImageLoadsHasMore ? "requested_top" : null,
                    SortKey: "time_us_asc",
                    SortDirection: ToolSortDirection.Ascending,
                    TieBreakers: ["file_path_ordinal_asc", "image_base_asc"]),
                TopStartupCpuFunctions: topCpuRows,
                TopStartupCpuFunctionsBoundary: BuildConservativeEmbeddedBoundary(
                    "/topStartupCpuFunctions",
                    topCpuRows?.Count ?? 0,
                    topCpu,
                    "exclusive_metric_desc",
                    ["function_ordinal_asc"],
                    unavailable: topCpuRows is null),
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
                processStartUs: c.Process.StartUs,
                totalWaitReasonCount: c.StartupBlockedUsByReason.Count));

            if (plan.NotConcludedCode is not null)
            {
                notConcluded.Add(new CompositeNotConcluded(
                    Code: plan.NotConcludedCode,
                    Reason: "No instance-resolved ImageLoad event was observed inside this startup window.",
                    Pid: c.Process.Pid,
                    BlockingCapability: null,
                    RelatedCallId: imageCallId,
                    ProcessStartUs: c.Process.StartUs,
                    BoundaryId: $"{prefix}.first-image-load"));
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
                    warnings: Array.Empty<string>(),
                    replayable: false,
                    internalNote: "Uses caller maxWindowDurationUs. Child warnings, next tools, calls, and evidence remain under FirstImageLoadGapEvidence[].Window only.",
                    processStartUs: c.Process.StartUs,
                    parentStartUs: bounds.StartUs,
                    parentEndUs: bounds.EndUs));
                firstImageLoadGapEvidence.Add(new StartupGapEvidenceRow(
                    EvidenceId: $"{prefix}.first-image-load-gap",
                    CallId: callId,
                    Pid: c.Process.Pid,
                    ProcessStartUs: c.Process.StartUs,
                    ProcessName: c.Name,
                    FirstImageLoadTimeUs: firstLoad.TimeUs,
                    FirstImageLoadOffsetUs: firstLoad.TimeFromProcessStartUs
                        ?? throw new InvalidOperationException(
                            "A startup candidate with an observed ProcessStart must expose a startup-relative image-load offset."),
                    ParentWindow: provenance,
                    ChildStartUs: child.StartUs,
                    ChildEndUs: child.EndUs,
                    Window: window,
                    WindowSectionBoundaries: BuildEmbeddedWindowBoundaries(
                        window,
                        topWindowEvidence)));
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
                BoundaryId: "slow-startup.no-slow-first-image-load-gaps"));
        }

        return new DiagnoseSlowStartupResponse(
            Candidates: candidates,
            CandidateBoundary: candidateBoundary,
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
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true), Description(
        "Preview high-wait composite; no root-cause field. One pid/window across subcalls; " +
        "missing stacks degrade to non-stack evidence. Candidates are ordered by total blocked " +
        "microseconds, not impact or causality. Compare same MetricName/Unit, and ObservedPct " +
        "with ThresholdPct. TimeBudgetMs bounds post-wait stack fan-out. " +
        "NextTools are optional hypothesis checks, not an ordered checklist.")]
    public DiagnoseHighWaitResponse DiagnoseHighWait(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
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
        int timeBudgetMs = 100_000,
        [Description("Optional exact trace-relative process start; requires pid. Candidates and every PID-targeted subcall preserve this instance scope.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        if (maxCandidates <= 0 || maxCandidates > ToolOverfetchExecutionContext.MaximumAllowed(20))
            throw new ArgumentOutOfRangeException(nameof(maxCandidates), "must be in [1, 20]");
        Validation.RequireTop(topStacks);
        Validation.RequireTop(topReadyStacks);
        Validation.RequireTimeBudgetMs(timeBudgetMs);

        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        startUs = window.StartUs;
        endUs = window.EndUs;
        var capabilities = traceLease.Capabilities;
        var warnings = new List<string>();
        var evidence = new List<CompositeEvidence>();
        var notConcluded = new List<CompositeNotConcluded>();
        var nextTools = new List<CompositeNextTool>();
        var executedCalls = new List<CompositeToolCall>();
        const string waitCallId = "high-wait.wait_analysis";
        var identities = TraceIdentityIndex.For(trace);
        var processScope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        if (!processScope.IsResolved)
        {
            var warning = ProcessAnalysisScope.ResolutionFailureWarning(
                processScope.ScopeStatus);
            return AttachPlannerBoundary(new DiagnoseHighWaitResponse(
                Candidates: Array.Empty<HighWaitCandidate>(),
                CandidateBoundary: UnknownEmbeddedBoundary(
                    "/candidates",
                    maxCandidates,
                    returned: 0,
                    "scope_not_resolved",
                    "total_blocked_us_desc",
                    ["wait_ratio_desc_nulls_last", "pid_asc", "process_start_us_asc"]),
                Evidence: Array.Empty<CompositeEvidence>(),
                NotConcluded:
                [
                    new CompositeNotConcluded(
                        Code: processScope.ScopeStatus,
                        Reason: warning,
                        Pid: pid,
                        BlockingCapability: null,
                        RelatedCallId: null,
                        ProcessStartUs: processStartUs,
                        ScopeStatus: processScope.ScopeStatus,
                        CapabilityStatus: "unknown",
                        NoDataReason: processScope.ScopeStatus),
                ],
                NextTools: Array.Empty<CompositeNextTool>(),
                ExecutedToolCalls: Array.Empty<CompositeToolCall>(),
                Warnings: [warning],
                SelectedProcess: processScope.SelectedProcess,
                ScopeMode: processScope.ScopeMode,
                PidReuseObserved: processScope.PidReuseObserved,
                IncludedProcesses: processScope.IncludedProcesses,
                ScopeStatus: processScope.ScopeStatus,
                CapabilityStatus: "unknown",
                MatchedEventCount: 0,
                NoDataReason: processScope.ScopeStatus));
        }

        var waitScope = ThreadAnalysisScope.ResolveRequired(
            window, pid, tid: null, processStartUs, threadStartUs: null, identities);
        var waitDetailed = WaitAnalysis.AnalyzeDetailed(
            trace, top: int.MaxValue, waitScope);
        var waitResp = waitDetailed.Response;
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
            internalNote: $"Internal unbounded aggregation; public wait_analysis caps top at {Validation.MaxTop}.",
            processStartUs: processStartUs));
        warnings.AddRange(PrefixWarnings("wait_analysis", waitResp.Warnings));

        var stackBudget = Stopwatch.StartNew();
        var budgetExhaustedKeys = new HashSet<string>(StringComparer.Ordinal);
        bool BudgetExpired() => stackBudget.ElapsedMilliseconds >= timeBudgetMs;
        void AddBudgetExhausted(int candidatePid, long candidateProcessStartUs, string skippedWork)
        {
            if (!budgetExhaustedKeys.Add($"{candidatePid}:{candidateProcessStartUs}:{skippedWork}"))
                return;

            var message = $"diagnose_high_wait reached its {timeBudgetMs} ms post-wait stack budget; skipped {skippedWork} for pid {candidatePid} processStartUs {candidateProcessStartUs}. Returned evidence is partial, not a complete diagnosis.";
            warnings.Add(message);
            notConcluded.Add(new CompositeNotConcluded(
                Code: "time_budget_exhausted",
                Reason: message,
                Pid: candidatePid,
                BlockingCapability: null,
                RelatedCallId: waitCallId,
                ProcessStartUs: candidateProcessStartUs));
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

        var positivePidRows = waitDetailed.CompleteRows
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

        var allCandidateGroups = BuildHighWaitCandidateAggregates(
            positivePidRows,
            requestedPid: pid,
            maxCandidates: int.MaxValue);
        var candidateGroups = allCandidateGroups.Take(maxCandidates).ToList();
        var candidateBoundary = ExactEmbeddedBoundary(
            "/candidates",
            maxCandidates,
            candidateGroups.Count,
            allCandidateGroups.Count,
            "total_blocked_us_desc",
            ToolSortDirection.Descending,
            ["wait_ratio_desc_nulls_last", "pid_asc", "process_start_us_asc"]);

        if (candidateGroups.Count == 0)
        {
            notConcluded.Add(new CompositeNotConcluded(
                Code: "no_wait_candidates",
                Reason: "No blocked-time rows matched the requested pid/window filters.",
                Pid: pid,
                BlockingCapability: null,
                RelatedCallId: waitCallId));
        }

        var hasScopedBlockingStacks = waitResp.ScopedStackedSwitches > 0;
        if (capabilities.HasCSwitch && waitResp.ScopedCSwitches > 0 && !hasScopedBlockingStacks)
        {
            notConcluded.Add(new CompositeNotConcluded(
                Code: "missing_stackwalks",
                Reason: "CSwitch events were observed in the requested scope, but none of the selected switch-out events carried blocking stacks; evidence stops at process, thread, and wait-reason level and does not claim a code path.",
                Pid: pid,
                BlockingCapability: nameof(WaitAnalysisResponse.ScopedStackedSwitches),
                RelatedCallId: waitCallId));
        }

        var candidates = new List<HighWaitCandidate>();
        foreach (var candidate in candidateGroups)
        {
            var candidateId = $"pid-{candidate.Pid}-start-{candidate.ProcessStartUs}";
            evidence.Add(ProcessWaitEvidence(
                evidenceId: $"high-wait.{candidateId}.wait-summary",
                callId: waitCallId,
                pid: candidate.Pid,
                processName: candidate.ProcessName,
                cpuUs: candidate.TotalCpuUs,
                blockedUs: candidate.TotalBlockedUs,
                waitReasons: candidate.TopWaitReasons,
                processStartUs: candidate.ProcessStartUs));

            foreach (var (reason, reasonIndex) in candidate.TopWaitReasons.Select((reason, index) => (reason, index)))
            {
                evidence.Add(new CompositeEvidence(
                    EvidenceId: $"high-wait.{candidateId}.reason-{reasonIndex}-{SanitizeId(reason.Reason)}",
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
                    Frames: Array.Empty<FrameMetric>(),
                    ProcessStartUs: candidate.ProcessStartUs,
                    FramesBoundary: ExactEmbeddedBoundary(
                        "/frames", 0, 0, 0, "not_applicable", ToolSortDirection.NotApplicable, []),
                    TopWaitReasonsBoundary: ExactEmbeddedBoundary(
                        "/topWaitReasons", 1, 1, 1, "blocked_us_desc",
                        ToolSortDirection.Descending, ["reason_ordinal_asc"])));
            }

            string? waitStacksCallId = null;
            var candidateHasWaitStacks = false;
            if (capabilities.HasCSwitch && hasScopedBlockingStacks)
            {
                if (BudgetExpired())
                {
                    AddBudgetExhausted(candidate.Pid, candidate.ProcessStartUs, "wait_top_stacks");
                }
                else
                {
                    waitStacksCallId = $"high-wait.{candidateId}.wait_top_stacks";
                    var effectiveTopStacks = StackResponseOptions.EffectiveTop(
                        topStacks, compactStacks: false, summaryOnly: true);
                    var candidateScope = ThreadAnalysisScope.ResolveRequired(
                        window,
                        candidate.Pid,
                        tid: null,
                        processStartUs: candidate.ProcessStartUs,
                        threadStartUs: null,
                        TraceIdentityIndex.For(trace));
                    var stackResp = BlockedTimeStackAnalysis.TopBlockedStacks(
                        trace,
                        effectiveTopStacks,
                        candidateScope,
                        symbolLog: _privacyLog.Writer);
                    var attemptedWaitStacksCallId = waitStacksCallId;
                    executedCalls.Add(ToolCall(
                        attemptedWaitStacksCallId,
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
                        effectiveTop: effectiveTopStacks,
                        processStartUs: candidate.ProcessStartUs));
                    warnings.AddRange(PrefixWarnings($"wait_top_stacks pid {candidate.Pid} start {candidate.ProcessStartUs}", stackResp.Warnings));

                    var stackCoverage = stackResp.StackCoverage;
                    if (stackCoverage?.StackedEventCount > 0)
                    {
                        candidateHasWaitStacks = true;
                        var stackFrames = stackResp.Rows
                            .Where(row => row.Function is not "?!?" and not "ROOT")
                            .Select(row => new FrameMetric(
                                Function: row.Function,
                                ExclusiveMetric: row.ExclusiveBlockedUs,
                                InclusiveMetric: row.InclusiveBlockedUs,
                                Unit: "us"))
                            .ToList();
                        evidence.Add(new CompositeEvidence(
                            EvidenceId: $"high-wait.{candidateId}.wait-stacks",
                            CallId: attemptedWaitStacksCallId,
                            EvidenceType: "wait_stack_summary",
                            Pid: candidate.Pid,
                            Tid: null,
                            ProcessName: candidate.ProcessName,
                            Label: "Top stack-covered blocked-time frames",
                            MetricName: "stackedBlockedUs",
                            MetricValue: stackCoverage.StackedMetric,
                            Unit: "us",
                            TopWaitReasons: Array.Empty<WaitReasonBucket>(),
                            Frames: stackFrames,
                            ProcessStartUs: candidate.ProcessStartUs,
                            FramesBoundary: StackFramesBoundary(
                                stackFrames.Count, stackResp.Rows.Count, effectiveTopStacks),
                            TopWaitReasonsBoundary: ExactEmbeddedBoundary(
                                "/topWaitReasons", 0, 0, 0, "blocked_us_desc",
                                ToolSortDirection.Descending, ["reason_ordinal_asc"])));

                        if (stackCoverage.CoverageState == "partial")
                        {
                            notConcluded.Add(new CompositeNotConcluded(
                                Code: "partial_stack_coverage",
                                Reason: $"Only {stackCoverage.StackedEventCount} of {stackCoverage.TotalEventCount} blocked interval samples for this candidate carried blocking stacks; stack evidence covers {stackCoverage.MetricStackCoveragePct:0.##}% of blocked microseconds.",
                                Pid: candidate.Pid,
                                BlockingCapability: nameof(DomainStackCoverage.StackedEventCount),
                                RelatedCallId: attemptedWaitStacksCallId,
                                MetricName: "stackedBlockedUs",
                                MetricValue: stackCoverage.StackedMetric,
                                Unit: "us",
                                ProcessStartUs: candidate.ProcessStartUs));
                        }
                    }
                    else
                    {
                        waitStacksCallId = null;
                        notConcluded.Add(new CompositeNotConcluded(
                            Code: "missing_stackwalks",
                            Reason: "The candidate's blocked intervals were analyzed, but none carried a blocking stack; synthetic ?!? rows are not reported as code-path evidence.",
                            Pid: candidate.Pid,
                            BlockingCapability: nameof(DomainStackCoverage.StackedEventCount),
                            RelatedCallId: attemptedWaitStacksCallId,
                            ProcessStartUs: candidate.ProcessStartUs));
                    }
                }
            }

            string? readyThreadCallId = null;
            var candidateHasReadyStacks = false;
            var schedulerWaitPct = candidate.SchedulerWaitPct;
            var shouldRunReadyThread = ShouldRunReadyThread(schedulerWaitPct);
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
                        ThresholdPct: ReadyThreadSchedulerThresholdPct,
                        ProcessStartUs: candidate.ProcessStartUs));
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
                        ThresholdPct: ReadyThreadSchedulerThresholdPct,
                        ProcessStartUs: candidate.ProcessStartUs));
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
                        ThresholdPct: ReadyThreadSchedulerThresholdPct,
                        ProcessStartUs: candidate.ProcessStartUs));
                }
                else if (BudgetExpired())
                {
                    AddBudgetExhausted(candidate.Pid, candidate.ProcessStartUs, "ready_thread_top_stacks");
                }
                else
                {
                    readyThreadCallId = $"high-wait.{candidateId}.ready_thread_top_stacks";
                    var attemptedReadyThreadCallId = readyThreadCallId;
                    var effectiveTopReadyStacks = StackResponseOptions.EffectiveTop(
                        topReadyStacks, compactStacks: false, summaryOnly: true);
                    var readyResp = ReadyThreadStackAnalysis.TopStacks(
                        trace,
                        effectiveTopReadyStacks,
                        awakenedPid: candidate.Pid,
                        startUs: startUs,
                        endUs: endUs,
                        symbolLog: _privacyLog.Writer,
                        awakenedProcessStartUs: candidate.ProcessStartUs);
                    executedCalls.Add(ToolCall(
                        attemptedReadyThreadCallId,
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
                        effectiveTop: effectiveTopReadyStacks,
                        awakenedProcessStartUs: candidate.ProcessStartUs));
                    warnings.AddRange(PrefixWarnings($"ready_thread_top_stacks awakenedPid {candidate.Pid} start {candidate.ProcessStartUs}", readyResp.Warnings));
                    var readyCoverage = readyResp.StackCoverage;
                    if (readyCoverage?.StackedEventCount > 0)
                    {
                        candidateHasReadyStacks = true;
                        var readyFrames = readyResp.Rows
                            .Where(row => row.Function is not "?!?" and not "ROOT")
                            .Select(row => new FrameMetric(
                                Function: row.Function,
                                ExclusiveMetric: row.ExclusiveReadyCount,
                                InclusiveMetric: row.InclusiveReadyCount,
                                Unit: "events"))
                            .ToList();
                        evidence.Add(new CompositeEvidence(
                            EvidenceId: $"high-wait.{candidateId}.ready-stacks",
                            CallId: attemptedReadyThreadCallId,
                            EvidenceType: "ready_thread_stack_summary",
                            Pid: candidate.Pid,
                            Tid: null,
                            ProcessName: candidate.ProcessName,
                            Label: "Associated readier stack frames for this process/window",
                            MetricName: "stackedReadyEvents",
                            MetricValue: readyCoverage.StackedMetric,
                            Unit: "events",
                            TopWaitReasons: Array.Empty<WaitReasonBucket>(),
                            Frames: readyFrames,
                            ProcessStartUs: candidate.ProcessStartUs,
                            FramesBoundary: StackFramesBoundary(
                                readyFrames.Count, readyResp.Rows.Count, effectiveTopReadyStacks),
                            TopWaitReasonsBoundary: ExactEmbeddedBoundary(
                                "/topWaitReasons", 0, 0, 0, "blocked_us_desc",
                                ToolSortDirection.Descending, ["reason_ordinal_asc"])));

                        if (readyCoverage.CoverageState == "partial")
                        {
                            notConcluded.Add(new CompositeNotConcluded(
                                Code: "partial_ready_thread_stack_coverage",
                                Reason: $"stack_coverage_state=partial;domain=ready_thread;stack_coverage_pct={readyCoverage.StackCoveragePct:0.##};stacked_event_count={readyCoverage.StackedEventCount};total_event_count={readyCoverage.TotalEventCount}. Associated readier evidence is incomplete and cannot establish a fully paired cause.",
                                Pid: candidate.Pid,
                                BlockingCapability: nameof(DomainStackCoverage.StackedEventCount),
                                RelatedCallId: attemptedReadyThreadCallId,
                                MetricName: "stackedReadyEvents",
                                MetricValue: readyCoverage.StackedMetric,
                                Unit: "events",
                                ProcessStartUs: candidate.ProcessStartUs));
                        }
                    }
                    else
                    {
                        readyThreadCallId = null;
                        var coverageState = readyCoverage?.CoverageState ?? "unknown";
                        notConcluded.Add(new CompositeNotConcluded(
                            Code: "ready_thread_skipped_missing_scoped_stacks",
                            Reason: $"stack_coverage_state={coverageState};domain=ready_thread. No captured ReadyThread stack evidence matched this candidate/window; synthetic ?!? is not reported as readier evidence.",
                            Pid: candidate.Pid,
                            BlockingCapability: nameof(DomainStackCoverage.StackedEventCount),
                            RelatedCallId: attemptedReadyThreadCallId,
                            ProcessStartUs: candidate.ProcessStartUs));
                    }
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
                    ThresholdPct: ReadyThreadSchedulerThresholdPct,
                    ProcessStartUs: candidate.ProcessStartUs));
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
                TestsHypothesis: "Verify whether this candidate's blocked time is concentrated in specific threads or wait reasons.",
                ProcessStartUs: candidate.ProcessStartUs));

            if (candidateHasWaitStacks)
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
                    TestsHypothesis: "Verify whether blocked time maps to a specific code path or is spread across unrelated waits.",
                    ProcessStartUs: candidate.ProcessStartUs));
            }

            if (shouldRunReadyThread && candidateHasReadyStacks)
            {
                nextTools.Add(new CompositeNextTool(
                    ToolName: "ready_thread_top_stacks",
                    Reason: "Scheduler-dispatch wait reasons were present; inspect associated readier/wakeup stack evidence in the same process/window.",
                    Pid: null,
                    AwakenedPid: candidate.Pid,
                    StartUs: startUs,
                    EndUs: endUs,
                    CompactStacks: false,
                    SummaryOnly: false,
                    TestsHypothesis: "Check whether a readier or wake-up path is prominent in the same scope; this does not pair it to a specific wait.",
                    AwakenedProcessStartUs: candidate.ProcessStartUs));
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
                ReadyThreadCallId: readyThreadCallId,
                ProcessStartUs: candidate.ProcessStartUs));
        }

        return AttachPlannerBoundary(new DiagnoseHighWaitResponse(
            Candidates: candidates,
            CandidateBoundary: candidateBoundary,
            Evidence: evidence,
            NotConcluded: notConcluded,
            NextTools: nextTools,
            ExecutedToolCalls: executedCalls,
            Warnings: warnings,
            SelectedProcess: processScope.SelectedProcess,
            ScopeMode: processScope.ScopeMode,
            PidReuseObserved: processScope.PidReuseObserved,
            IncludedProcesses: processScope.IncludedProcesses,
            ScopeStatus: processScope.ScopeStatus,
            CapabilityStatus: waitResp.CapabilityStatus,
            MatchedEventCount: waitResp.MatchedEventCount,
            NoDataReason: waitResp.NoDataReason,
            Partial: budgetExhaustedKeys.Count > 0,
            PartialCode: budgetExhaustedKeys.Count > 0
                ? "time_budget_exhausted"
                : null));
    }

    private static DiagnoseWindowResponse AttachPlannerBoundary(
        DiagnoseWindowResponse response) => response with
        {
            PlannerExecution = Planner.Value.DescribeNotAdmitted("diagnose_window"),
        };

    private static DiagnoseHighWaitResponse AttachPlannerBoundary(
        DiagnoseHighWaitResponse response) => response with
        {
            PlannerExecution = Planner.Value.DescribeNotAdmitted("diagnose_high_wait"),
        };

    private static DiagnoseSlowStartupResponse AttachPlannerBoundary(
        DiagnoseSlowStartupResponse response,
        string toolName) => response with
        {
            PlannerExecution = Planner.Value.DescribeNotAdmitted(toolName),
            FirstImageLoadGapEvidence = response.FirstImageLoadGapEvidence?
                .Select(row => row with
                {
                    Window = AttachPlannerBoundary(row.Window),
                })
                .ToArray(),
        };

    private static DiagnoseWindowResponse EmptyDiagnoseWindow(
        long startUs,
        long endUs,
        int? pid,
        IReadOnlyList<WindowEvidenceRow> evidence,
        IReadOnlyList<CompositeNotConcluded> notConcluded,
        IReadOnlyList<CompositeNextTool> nextTools,
        IReadOnlyList<CompositeToolCall> executedCalls,
        IReadOnlyList<string> warnings,
        ProcessInstanceKey? selectedProcess = null,
        string scopeMode = "not_evaluated",
        bool pidReuseObserved = false,
        IReadOnlyList<ProcessInstanceKey>? includedProcesses = null,
        string scopeStatus = "not_evaluated",
        string capabilityStatus = "unknown",
        long matchedEventCount = 0,
        string? noDataReason = null)
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
            Warnings: warnings,
            SelectedProcess: selectedProcess,
            ScopeMode: scopeMode,
            PidReuseObserved: pidReuseObserved,
            IncludedProcesses: includedProcesses ?? Array.Empty<ProcessInstanceKey>(),
            ScopeStatus: scopeStatus,
            CapabilityStatus: capabilityStatus,
            MatchedEventCount: matchedEventCount,
            NoDataReason: noDataReason);

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
        string? orderBy = null,
        long? processStartUs = null,
        long? targetProcessStartUs = null)
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
            orderBy: orderBy,
            processStartUs: processStartUs,
            targetProcessStartUs: targetProcessStartUs));
        warnings.AddRange(PrefixWarnings(toolName, toolWarnings));
    }

    private static void AddChildNotConcluded(
        List<CompositeNotConcluded> notConcluded,
        string fallbackCode,
        string fallbackReason,
        int? pid,
        long? processStartUs,
        string relatedCallId,
        string eventFamily,
        string scopeStatus,
        string capabilityStatus,
        string? noDataReason)
        => notConcluded.Add(new CompositeNotConcluded(
            Code: noDataReason ?? fallbackCode,
            Reason: noDataReason switch
            {
                "scope_not_found" =>
                    $"{eventFamily}: the requested process instance did not resolve in this half-open window.",
                "event_class_not_observed" =>
                    $"{eventFamily}: this event class was not observed in the trace; that does not by itself prove a capture keyword was disabled.",
                "no_events_in_scope" =>
                    $"{eventFamily}: no event matched this process scope/window; this alone does not establish trace-wide absence or capture configuration.",
                null => fallbackReason,
                _ => $"{eventFamily}: analyzer returned no data ({noDataReason}).",
            },
            Pid: pid,
            BlockingCapability: capabilityStatus == "not_observed"
                ? eventFamily
                : null,
            RelatedCallId: relatedCallId,
            ProcessStartUs: processStartUs,
            ScopeStatus: scopeStatus,
            CapabilityStatus: capabilityStatus,
            NoDataReason: noDataReason));

    private static string BuildSummary(IReadOnlyList<SlowStartupCandidate> candidates)
    {
        if (candidates.Count == 0) return "No candidates.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {candidates.Count} slow-startup candidate(s):");
        foreach (var c in candidates)
        {
            var ratioStr = c.ObservedStartupWallToCpuRatio is { } r ? $"{r:F1}x" : "n/a";
            sb.AppendLine(
                $"  - pid {c.Pid} start={c.ProcessStartUs} ({c.Name}): " +
                $"startup_wall={c.ObservedStartupWallUs / 1000.0:F1}ms, " +
                $"startup_cpu={c.StartupCpuUs / 1000.0:F1}ms, " +
                $"startup_wait_ratio={ratioStr}");
            if (c.TopStartupWaitReasons.Count > 0)
            {
                var reasons = string.Join(
                    ", ",
                    c.TopStartupWaitReasons.Select(bucket => bucket.Reason));
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
        long? parentEndUs = null,
        long? awakenedProcessStartUs = null,
        long? targetProcessStartUs = null)
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
            ParentEndUs: parentEndUs,
            AwakenedProcessStartUs: awakenedProcessStartUs,
            TargetProcessStartUs: targetProcessStartUs);

    private static CompositeEvidence ProcessWaitEvidence(
        string evidenceId,
        string callId,
        int pid,
        string processName,
        long cpuUs,
        long blockedUs,
        IReadOnlyList<WaitReasonBucket> waitReasons,
        long? processStartUs = null,
        int? totalWaitReasonCount = null)
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
            ProcessStartUs: processStartUs,
            FramesBoundary: ExactEmbeddedBoundary(
                "/frames", 0, 0, 0, "not_applicable", ToolSortDirection.NotApplicable, []),
            TopWaitReasonsBoundary: ExactEmbeddedBoundary(
                "/topWaitReasons",
                totalWaitReasonCount.HasValue ? 5 : waitReasons.Count,
                waitReasons.Count,
                totalWaitReasonCount ?? waitReasons.Count,
                "blocked_us_desc",
                ToolSortDirection.Descending,
                ["reason_ordinal_asc"]));

    internal static WindowEvidenceRow? BuildWaitSummaryEvidence(
        WaitAnalysisResponse waits,
        int? pid)
    {
        ArgumentNullException.ThrowIfNull(waits);
        if (waits.TotalBlockedUs <= 0)
            return null;

        var selectedProcess = string.Equals(
                waits.ScopeMode,
                "single_process",
                StringComparison.Ordinal)
            ? waits.SelectedProcess
            : null;
        var selectedProcessName = selectedProcess is { } key
            ? waits.Rows.FirstOrDefault(row =>
                row.Pid == key.Pid &&
                row.ProcessStartUs == key.StartUs)?.ProcessName
            : null;
        var sampledReasons = CollapseWaitReasons(waits.Rows, top: 3);

        return new WindowEvidenceRow(
            EvidenceType: "wait_summary",
            Label: "Total blocked time in the selected scope; reason details are returned-row samples",
            MetricName: "blockedUs",
            MetricValue: waits.TotalBlockedUs,
            Unit: "us",
            Pid: selectedProcess?.Pid ??
                (string.Equals(waits.ScopeMode, "pid_aggregate", StringComparison.Ordinal)
                    ? pid
                    : null),
            // The metric covers the complete selected scope, not just returned rows.
            // A singular process label is therefore safe only for an exact process scope;
            // all-process and PID-aggregate totals must not inherit the first top-N row's
            // name and appear attributed to that process.
            ProcessName: selectedProcessName,
            File: null,
            TimeUs: null,
            Details: sampledReasons
                .Select(reason => $"{reason.Reason}={reason.BlockedUs}us/{reason.Count}")
                .ToList(),
            Samples: Array.Empty<WindowEvidenceSample>(),
            SamplesBoundary: null,
            ProcessStartUs: selectedProcess?.StartUs,
            ScopeMode: waits.ScopeMode,
            DetailsBoundary: new EmbeddedTopNBoundary(
                "/details",
                3,
                sampledReasons.Count,
                TotalAvailable: null,
                ToolSectionTotalState.Unknown,
                ToolSectionMoreState.Unknown,
                HasMore: false,
                ContinuationAvailable: false,
                TruncationReason: "returned_rows_sample",
                "blocked_us_desc",
                ToolSortDirection.Descending,
                ["reason_ordinal_asc"]));
    }

    internal static WindowEvidenceRow BuildSecurityDurationEvidence(
        SecurityScanAnalysisResponse security,
        int? pid)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (security.PairedScanCount <= 0)
        {
            throw new ArgumentException(
                "security_scan_duration requires at least one paired scan.",
                nameof(security));
        }

        var selectedProcess = string.Equals(
                security.ScopeMode,
                "single_process",
                StringComparison.Ordinal)
            ? security.SelectedProcess
            : null;
        var selectedProcessName = selectedProcess is { } key
            ? security.SlowScans.FirstOrDefault(row =>
                row.Pid == key.Pid &&
                row.ProcessStartUs == key.StartUs)?.Process
            : null;
        var details = new List<string>
        {
            $"pairedScanCount={security.PairedScanCount}",
            $"responseMatchedEventCount={security.MatchedEventCount} (all evidence classifications; not a duration denominator)",
            "Known-schema interval evidence does not by itself prove performance impact or root cause.",
        };
        var samples = security.SlowScans.FirstOrDefault() is { } sample
            ? new[]
            {
                new WindowEvidenceSample(
                    sample.ProviderName,
                    sample.Process,
                    sample.Path,
                    sample.StartUs,
                    EventCount: null,
                    sample.Pid,
                    sample.ProcessStartUs,
                    Representative: false,
                    MetricAttributable: false,
                    SampleScope: "returned_rows_only"),
            }
            : Array.Empty<WindowEvidenceSample>();
        var samplesBoundary = security.SlowScansHasMore
            ? new EmbeddedTopNBoundary(
                "/samples",
                1,
                samples.Length,
                checked((long)security.SlowScans.Count + 1L),
                ToolSectionTotalState.LowerBound,
                ToolSectionMoreState.Present,
                HasMore: true,
                ContinuationAvailable: false,
                TruncationReason: "source_top_plus_one_witness",
                "source_rank_asc",
                ToolSortDirection.Ascending,
                ["sample_index_asc"])
            : ExactEmbeddedBoundary(
                "/samples",
                1,
                samples.Length,
                security.SlowScans.Count,
                "source_rank_asc",
                ToolSortDirection.Ascending,
                ["sample_index_asc"]);

        return new WindowEvidenceRow(
            EvidenceType: "security_scan_duration",
            Label: "Duration from paired Microsoft Defender scan-request endpoints",
            MetricName: "scanDurationUs",
            MetricValue: security.TotalDurationUs,
            Unit: "us",
            Pid: selectedProcess?.Pid ??
                (string.Equals(security.ScopeMode, "pid_aggregate", StringComparison.Ordinal)
                    ? pid
                    : null),
            ProcessName: selectedProcessName,
            File: null,
            TimeUs: null,
            Details: details,
            Samples: samples,
            SamplesBoundary: samplesBoundary,
            EvidenceKind: "paired_interval",
            Provenance: "known_defender_schema",
            Confidence: "high",
            ProcessStartUs: selectedProcess?.StartUs,
            ScopeMode: security.ScopeMode,
            DetailsBoundary: null);
    }

    internal static IReadOnlyList<WindowEvidenceRow> BuildSecurityPresenceEvidence(
        SecurityScanDetailedResult detailed,
        int? pid)
    {
        ArgumentNullException.ThrowIfNull(detailed);

        var security = detailed.Response;
        var evidence = new List<WindowEvidenceRow>();
        var selectedProcess = string.Equals(
                security.ScopeMode,
                "single_process",
                StringComparison.Ordinal)
            ? security.SelectedProcess
            : null;
        var selectedProcessName = selectedProcess is { } key
            ? security.Rows.FirstOrDefault(row =>
                row.Pid == key.Pid &&
                row.ProcessStartUs == key.StartUs)?.Process
            : null;
        foreach (var summary in detailed.EvidenceClassSummaries.Where(summary =>
                     !(security.PairedScanCount > 0 &&
                       summary.EvidenceKind == "paired_interval")))
        {
            var returnedSamples = security.Rows
                .Where(row =>
                    string.Equals(
                        row.EvidenceKind ?? "unknown",
                        summary.EvidenceKind,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        row.Provenance ?? "unknown",
                        summary.Provenance,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        row.Confidence ?? "unknown",
                        summary.Confidence,
                        StringComparison.Ordinal))
                .ToArray();
            var details = new List<string>();
            details.Add(
                $"eventCountTotalState={summary.TotalState}; eventCountSource=pre_pagination_evidence_class_aggregation.");
            var samples = returnedSamples.Select(row => new WindowEvidenceSample(
                    row.ProviderName,
                    row.Process,
                    row.Path,
                    TimeUs: null,
                    row.EventCount,
                    row.Pid,
                    row.ProcessStartUs,
                    Representative: false,
                    MetricAttributable: false,
                    SampleScope: "returned_rows_only"))
                .ToArray();
            var samplesBoundary = security.RowsHasMore
                ? UnknownEmbeddedBoundary(
                    "/samples",
                    security.Rows.Count,
                    samples.Length,
                    "source_limit_saturated",
                    "source_rank_asc",
                    ["sample_index_asc"],
                    ToolSortDirection.Ascending)
                : ExactEmbeddedBoundary(
                    "/samples",
                    security.Rows.Count,
                    samples.Length,
                    samples.Length,
                    "source_rank_asc",
                    ToolSortDirection.Ascending,
                    ["sample_index_asc"]);

            var isLowConfidence = string.Equals(
                summary.Confidence,
                "low",
                StringComparison.Ordinal);
            evidence.Add(new WindowEvidenceRow(
                EvidenceType: "security_scan_presence",
                Label: isLowConfidence
                    ? "Low-confidence scan-like name matches; presence only, not duration or root cause"
                    : "Known security scan result events were present; no paired duration was established",
                MetricName: "matchedSecurityEvents",
                MetricValue: summary.EventCount,
                Unit: "events",
                Pid: selectedProcess?.Pid ??
                    (string.Equals(security.ScopeMode, "pid_aggregate", StringComparison.Ordinal)
                        ? pid
                        : null),
                ProcessName: selectedProcessName,
                File: null,
                TimeUs: null,
                Details: details,
                Samples: samples,
                SamplesBoundary: samplesBoundary,
                EvidenceKind: summary.EvidenceKind,
                Provenance: summary.Provenance,
                Confidence: summary.Confidence,
                ProcessStartUs: selectedProcess?.StartUs,
                ScopeMode: security.ScopeMode,
                DetailsBoundary: null));
        }

        return evidence;
    }

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
            .ThenBy(reason => reason.Reason, StringComparer.Ordinal)
            .Take(top)
            .ToList();

    private static IReadOnlyList<EmbeddedTopNBoundary> BuildEmbeddedWindowBoundaries(
        DiagnoseWindowResponse window,
        int requested)
    {
        return
        [
            BuildConservativeEmbeddedBoundary("/hardFaultsByBytes", window.HardFaultsByBytes.Count, requested,
                "page_in_bytes_desc", ["page_in_count_desc", "file_path_ordinal_asc"]),
            BuildConservativeEmbeddedBoundary("/hardFaultsByMaxLatency", window.HardFaultsByMaxLatency.Count, requested,
                "max_latency_us_desc", ["page_in_bytes_desc", "page_in_count_desc", "file_path_ordinal_asc"]),
            BuildConservativeEmbeddedBoundary("/fileIoTopFiles", window.FileIoTopFiles.Count, requested,
                "total_bytes_desc", ["file_path_ordinal_asc"]),
            BuildConservativeEmbeddedBoundary("/securityScanTargets", window.SecurityScanTargets.Count, requested,
                "total_accounted_duration_us_desc", ["event_count_desc", "paired_scan_count_desc",
                "source_ordinal_asc", "provider_name_ordinal_asc", "process_name_ordinal_asc",
                "pid_asc", "path_ordinal_asc", "process_start_us_asc_nulls_first",
                "target_identity_source_ordinal_asc", "evidence_kind_ordinal_asc",
                "provenance_ordinal_asc", "confidence_ordinal_asc"]),
            BuildConservativeEmbeddedBoundary("/slowScans", window.SlowScans.Count, requested,
                "accounted_duration_us_desc", ["start_us_asc", "source_ordinal_asc",
                "provider_name_ordinal_asc", "id_ordinal_asc", "pid_asc",
                "process_start_us_asc_nulls_first", "process_name_ordinal_asc",
                "path_ordinal_asc", "stop_us_asc", "evidence_kind_ordinal_asc",
                "provenance_ordinal_asc", "confidence_ordinal_asc",
                "target_identity_source_ordinal_asc", "reason_ordinal_asc"]),
            BuildConservativeEmbeddedBoundary("/waits", window.Waits.Count, requested,
                "blocked_us_desc", ["cpu_us_desc", "pid_asc", "process_start_us_asc",
                "tid_asc", "thread_generation_asc"]),
            BuildConservativeEmbeddedBoundary("/pressure/topPeakWorkingSetProcesses",
                window.Pressure?.TopPeakWorkingSetProcesses.Count ?? 0, requested,
                "peak_working_set_bytes_desc", ["peak_commit_bytes_desc", "pid_asc",
                "process_start_us_asc"]),
            BuildConservativeEmbeddedBoundary("/pressure/topPeakCommitProcesses",
                window.Pressure?.TopPeakCommitProcesses.Count ?? 0, requested,
                "peak_commit_bytes_desc", ["peak_working_set_bytes_desc", "pid_asc",
                "process_start_us_asc"]),
        ];
    }

    private static EmbeddedTopNBoundary BuildConservativeEmbeddedBoundary(
        string pointer,
        int returned,
        int requested,
        string sortKey,
        IReadOnlyList<string> tieBreakers,
        bool unavailable = false)
    {
        var saturated = returned == requested;
        var unknown = unavailable || saturated;
        return new EmbeddedTopNBoundary(
            pointer,
            requested,
            returned,
            unknown ? null : returned,
            unknown ? ToolSectionTotalState.Unknown : ToolSectionTotalState.Exact,
            unknown ? ToolSectionMoreState.Unknown : ToolSectionMoreState.Absent,
            HasMore: false,
            ContinuationAvailable: false,
            TruncationReason: unavailable ? "analysis_unavailable" :
                saturated ? "source_limit_saturated" : null,
            sortKey,
            ToolSortDirection.Descending,
            tieBreakers);
    }

    private static EmbeddedTopNBoundary UnknownEmbeddedBoundary(
        string pointer,
        int requested,
        int returned,
        string truncationReason,
        string sortKey,
        IReadOnlyList<string> tieBreakers,
        ToolSortDirection direction = ToolSortDirection.Descending) => new(
            pointer,
            requested,
            returned,
            TotalAvailable: null,
            ToolSectionTotalState.Unknown,
            ToolSectionMoreState.Unknown,
            HasMore: false,
            ContinuationAvailable: false,
            truncationReason,
            sortKey,
            direction,
            tieBreakers);

    private static EmbeddedTopNBoundary ExactEmbeddedBoundary(
        string pointer,
        int requested,
        int returned,
        int total,
        string sortKey,
        ToolSortDirection direction,
        IReadOnlyList<string> tieBreakers) => new(
            pointer,
            requested,
            returned,
            total,
            ToolSectionTotalState.Exact,
            total > returned ? ToolSectionMoreState.Present : ToolSectionMoreState.Absent,
            HasMore: total > returned,
            ContinuationAvailable: false,
            TruncationReason: total > returned ? "fixed_source_limit" : null,
            sortKey == "not_applicable" ? "construction_sequence_asc" : sortKey,
            direction == ToolSortDirection.NotApplicable
                ? ToolSortDirection.Ascending
                : direction,
            tieBreakers);

    private static EmbeddedTopNBoundary StackFramesBoundary(
        int returned,
        int rawReturned,
        int requested)
    {
        var unknown = rawReturned >= requested || rawReturned != returned;
        return new EmbeddedTopNBoundary(
            "/frames",
            requested,
            returned,
            unknown ? null : returned,
            unknown ? ToolSectionTotalState.Unknown : ToolSectionTotalState.Exact,
            unknown ? ToolSectionMoreState.Unknown : ToolSectionMoreState.Absent,
            HasMore: false,
            ContinuationAvailable: false,
            TruncationReason: unknown ? "source_limit_saturated_or_synthetic_rows_filtered" : null,
            "exclusive_metric_desc",
            ToolSortDirection.Descending,
            ["function_ordinal_asc"]);
    }

    internal static IReadOnlyList<WaitCandidateAggregate> BuildHighWaitCandidateAggregates(
        IEnumerable<WaitAnalysisRow> rows,
        int? requestedPid,
        int maxCandidates) =>
        rows
            .Where(row =>
                row.Pid > 0 &&
                (!requestedPid.HasValue || row.Pid == requestedPid.Value) &&
                (requestedPid.HasValue || row.Pid != 4))
            .GroupBy(row => new ProcessInstanceKey(row.Pid, row.ProcessStartUs))
            .Select(group =>
            {
                var rowsForProcess = group.ToList();
                var totalCpuUs = rowsForProcess.Sum(row => row.CpuUs);
                var totalBlockedUs = rowsForProcess.Sum(row => row.BlockedUs);
                var allReasons = CollapseWaitReasons(rowsForProcess, top: int.MaxValue);
                return new WaitCandidateAggregate(
                    Pid: group.Key.Pid,
                    ProcessStartUs: group.Key.StartUs,
                    ProcessName: rowsForProcess.FirstOrDefault(
                        row => !string.IsNullOrWhiteSpace(row.ProcessName))?.ProcessName ?? string.Empty,
                    TotalCpuUs: totalCpuUs,
                    TotalBlockedUs: totalBlockedUs,
                    WaitRatio: totalCpuUs > 0 ? totalBlockedUs / (double)totalCpuUs : null,
                    ContextSwitches: rowsForProcess.Sum(row => row.ContextSwitches),
                    TopWaitReasons: allReasons,
                    SchedulerWaitPct: SchedulerWaitPct(allReasons, totalBlockedUs));
            })
            .Where(candidate => candidate.TotalBlockedUs > 0)
            .OrderByDescending(candidate => candidate.TotalBlockedUs)
            .ThenByDescending(candidate => candidate.WaitRatio ?? 0)
            .ThenBy(candidate => candidate.Pid)
            .ThenBy(candidate => candidate.ProcessStartUs)
            .Take(maxCandidates)
            .ToList();

    private static double SchedulerWaitPct(IReadOnlyList<WaitReasonBucket> reasons, long totalBlockedUs)
    {
        if (totalBlockedUs <= 0) return 0;

        var schedulerBlocked = reasons
            .Where(reason => IsSchedulerWaitReason(reason.Reason))
            .Sum(reason => reason.BlockedUs);
        return schedulerBlocked / (double)totalBlockedUs;
    }

    internal static bool ShouldRunReadyThread(double schedulerWaitPct) =>
        schedulerWaitPct >= ReadyThreadSchedulerThresholdPct;

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

    internal sealed record WaitCandidateAggregate(
        int Pid,
        long ProcessStartUs,
        string ProcessName,
        long TotalCpuUs,
        long TotalBlockedUs,
        double? WaitRatio,
        long ContextSwitches,
        IReadOnlyList<WaitReasonBucket> TopWaitReasons,
        double SchedulerWaitPct);
}
