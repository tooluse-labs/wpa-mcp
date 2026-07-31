using System.ComponentModel;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class DiagnoseToolsTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";
    private const string MmapFixturePath = "fixtures/small_mmap.etl";

    [Fact]
    public void DiagnoseSlowStartup_RejectsBadArguments()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", maxCandidates: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", maxCandidates: 21));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", minWaitRatio: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", startupWindowUs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", slowFirstImageLoadThresholdUs: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", topWindowEvidence: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", maxWindowDurationUs: 0));
    }

    [Fact]
    public void DiagnoseSlowStartup_TopInputsEnforceSharedBoundary()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        Assert.Throws<FileNotFoundException>(() => tools.DiagnoseSlowStartup(
            "missing-before-validation.etl",
            topImageLoads: Validation.MaxTop,
            topCpu: Validation.MaxTop));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseSlowStartup(
            "missing-before-validation.etl",
            topImageLoads: Validation.MaxTop + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseSlowStartup(
            "missing-before-validation.etl",
            topCpu: Validation.MaxTop + 1));
    }

    [Fact]
    public void DiagnoseSlowStartup_ReturnsCandidatesOrEmptyWithWarning()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));
        // Aggressive threshold = many candidates; fall through to "no candidates" warning if not.
        var resp = tools.DiagnoseSlowStartup(FixturePath, minWaitRatio: 1.0, maxCandidates: 3);
        Assert.NotNull(resp.Warnings);
        if (resp.Candidates.Count == 0)
            Assert.Contains(resp.Warnings, w => w.Contains("No processes matched"));
        else
            Assert.All(resp.Candidates, c => Assert.True(c.StartupWaitRatio >= 1.0));
    }

    [Fact]
    public void DiagnoseSlowStartup_PrimaryCallsShareCandidateProcessAndWindow()
    {
        var summary = typeof(DiagnoseSlowStartupResponse).GetProperty("Summary");
        Assert.NotNull(summary);
        Assert.NotNull(summary.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false).SingleOrDefault());

        ThreadAnalysisScope? cpuScope = null;
        var resp = ComposeDeterministicSlowStartup(
            onCpu: scope => cpuScope = scope);

        Assert.NotNull(resp.ExecutedToolCalls);
        var onlyCandidate = Assert.Single(resp.Candidates);
        Assert.Equal(onlyCandidate.Window.StartUs, cpuScope?.Window.StartUs);
        Assert.Equal(onlyCandidate.Window.EndUs, cpuScope?.Window.EndUs);
        Assert.Equal(
            new ProcessInstanceKey(onlyCandidate.Pid, onlyCandidate.ProcessStartUs),
            cpuScope?.Process?.Key);
        foreach (var candidate in resp.Candidates)
        {
            var calls = resp.ExecutedToolCalls!
                .Where(call =>
                    call.ProcessStartUs == candidate.ProcessStartUs &&
                    call.Pid == candidate.Pid &&
                    call.ParentStartUs is null)
                .ToList();

            Assert.Equal(4, calls.Count);
            Assert.Contains(calls, call =>
                call.ToolName == "startup_candidate_projection");
            Assert.Contains(calls, call => call.ToolName == "wait_analysis");
            Assert.Contains(calls, call => call.ToolName == "image_load_timing");
            Assert.Contains(calls, call => call.ToolName == "cpu_top_functions");
            Assert.All(calls, call =>
            {
                Assert.Equal(candidate.ProcessStartUs, call.StartUs);
                Assert.Equal(candidate.StartupEndUs, call.EndUs);
            });
        }
    }

    [Fact]
    public void DiagnoseSlowStartup_WaitEvidenceReportsFullBlockedTime()
    {
        var resp = ComposeDeterministicSlowStartup();

        Assert.NotNull(resp.Evidence);
        foreach (var candidate in resp.Candidates)
        {
            var evidence = Assert.Single(resp.Evidence!, item =>
                item.EvidenceType == "process_wait_summary" &&
                item.Pid == candidate.Pid &&
                item.ProcessStartUs == candidate.ProcessStartUs);
            Assert.Equal(candidate.StartupBlockedUs, evidence.MetricValue);
            Assert.True(evidence.MetricValue >= evidence.TopWaitReasons.Sum(reason => reason.BlockedUs));
        }
    }

    [Fact]
    public void DiagnoseSlowStartup_AttachesWindowEvidenceForFirstImageLoadGaps()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var resp = tools.DiagnoseSlowStartup(
            FixturePath,
            nameSubstring: "taskhostw",
            minWaitRatio: 0.0,
            maxCandidates: 1,
            topImageLoads: 5,
            topCpu: 1,
            slowFirstImageLoadThresholdUs: 0,
            topWindowEvidence: 3);

        Assert.NotNull(resp.FirstImageLoadGapEvidence);
        if (resp.Candidates.Count == 0)
        {
            Assert.Empty(resp.FirstImageLoadGapEvidence!);
            Assert.Contains(resp.NotConcluded!, item => item.Code == "no_candidates");
            return;
        }
        Assert.NotEmpty(resp.FirstImageLoadGapEvidence!);
        var gap = resp.FirstImageLoadGapEvidence![0];
        Assert.True(gap.FirstImageLoadTimeUs >= gap.ProcessStartUs);
        Assert.Equal(gap.FirstImageLoadTimeUs - gap.ProcessStartUs, gap.FirstImageLoadOffsetUs);
        Assert.Equal(gap.Pid, gap.Window.Pid);
        Assert.Equal(gap.ProcessStartUs, gap.ChildStartUs);
        Assert.Equal(gap.FirstImageLoadTimeUs, gap.ChildEndUs);
        Assert.Equal(gap.ChildStartUs, gap.Window.WindowStartUs);
        Assert.Equal(gap.ChildEndUs, gap.Window.WindowEndUs);
        Assert.True(gap.ChildStartUs >= gap.ParentWindow.StartUs);
        Assert.True(gap.ChildEndUs <= gap.ParentWindow.EndUs);
        Assert.NotEmpty(gap.Window.ExecutedToolCalls);
        Assert.Contains(gap.Window.ExecutedToolCalls, call => call.ToolName == "hard_fault_by_file");
        var call = Assert.Single(resp.ExecutedToolCalls!, item => item.CallId == gap.CallId);
        Assert.Equal(gap.Pid, call.Pid);
        Assert.Equal(gap.ProcessStartUs, call.ProcessStartUs);
        Assert.Equal(gap.ChildStartUs, call.StartUs);
        Assert.Equal(gap.ChildEndUs, call.EndUs);
        Assert.Equal(gap.ParentWindow.StartUs, call.ParentStartUs);
        Assert.Equal(gap.ParentWindow.EndUs, call.ParentEndUs);
        Assert.False(call.Replayable);
        Assert.Contains("maxWindowDurationUs", call.InternalNote);
    }

    [Fact]
    public void DiagnoseSlowStartup_ExcludesTraceResidentProcessesFromCandidates()
    {
        var cache = new TraceCache(capacity: 2);
        var meta = new MetaTools(cache);
        var tools = new DiagnoseTools(cache);
        var processes = meta.ListProcesses(FixturePath, top: 1000).Rows;
        var target = processes
            .Where(row => row.TraceResident && row.WaitRatio.HasValue && !string.IsNullOrWhiteSpace(row.Name))
            .GroupBy(row => row.Name)
            .OrderBy(group => group.Count())
            .Select(group => group.First())
            .FirstOrDefault();
        Assert.NotNull(target);

        var resp = tools.DiagnoseSlowStartup(
            FixturePath,
            nameSubstring: target!.Name,
            minWaitRatio: 0.0,
            maxCandidates: 20,
            topImageLoads: 5,
            topCpu: 1,
            slowFirstImageLoadThresholdUs: 0,
            topWindowEvidence: 3);

        Assert.DoesNotContain(resp.Candidates, candidate => candidate.Pid == target.Pid);
        Assert.DoesNotContain(
            resp.FirstImageLoadGapEvidence ?? Array.Empty<StartupGapEvidenceRow>(),
            gap => gap.Pid == target.Pid);
        Assert.Contains(resp.NotConcluded!, item =>
            item.Code == "startup_start_not_observed" &&
            item.Pid == target.Pid &&
            item.ProcessStartUs.HasValue &&
            item.EvidenceId ==
                $"slow-startup.pid-{target.Pid}.start-{item.ProcessStartUs}.startup-start");
    }

    [Fact]
    public void DiagnoseSlowStartup_ProvenanceAndEvidenceIdsAreInstanceBound()
    {
        var response = ComposeDeterministicSlowStartup();

        foreach (var candidate in response.Candidates)
        {
            Assert.True(candidate.Window.ProcessStartObserved);
            Assert.Equal(candidate.Pid, candidate.Window.Pid);
            Assert.Equal(candidate.ProcessStartUs, candidate.Window.ProcessStartUs);
            Assert.Equal(candidate.ProcessStartUs, candidate.Window.StartUs);
            Assert.Equal(candidate.StartupEndUs, candidate.Window.EndUs);
        }

        foreach (var gap in response.FirstImageLoadGapEvidence ?? [])
        {
            Assert.True(gap.ChildStartUs >= gap.ParentWindow.StartUs);
            Assert.True(gap.ChildEndUs <= gap.ParentWindow.EndUs);
            Assert.Equal(gap.FirstImageLoadTimeUs, gap.ChildEndUs);
        }

        var evidenceIds = response.Candidates.Select(candidate => candidate.EvidenceId)
            .Concat((response.Evidence ?? []).Select(item => item.EvidenceId))
            .Concat((response.NotConcluded ?? [])
                .Where(item => item.EvidenceId is not null)
                .Select(item => item.EvidenceId!))
            .Concat((response.FirstImageLoadGapEvidence ?? [])
                .Select(item => item.EvidenceId))
            .Concat((response.Discovery?.ExcludedSamples ?? [])
                .Select(item => item.EvidenceId))
            .ToList();

        Assert.Equal(
            evidenceIds.Count,
            evidenceIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            response.ExecutedToolCalls?.Count ?? 0,
            response.ExecutedToolCalls?
                .Select(call => call.CallId)
                .Distinct(StringComparer.Ordinal)
                .Count() ?? 0);
    }

    [Fact]
    public void DiagnoseSlowStartup_ZeroCandidatesRetainsSchedulerWarningsOnce()
    {
        const string schedulerWarning = "scheduler stream incomplete";

        var response = ComposeDeterministicSlowStartup(
            startupCpuUs: 0,
            schedulerWarnings: [schedulerWarning]);

        Assert.Empty(response.Candidates);
        Assert.Equal(
            1,
            response.Warnings.Count(warning =>
                warning == $"slow-startup.discovery: {schedulerWarning}"));
    }

    [Fact]
    public void DiagnoseHighWait_RejectsBadArguments()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", maxCandidates: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", maxCandidates: 21));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", topStacks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", topReadyStacks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", timeBudgetMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", startUs: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", startUs: 2, endUs: 1));
    }

    [Fact]
    public void DiagnoseWindow_RejectsBadArguments()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseWindow("nonexistent.etl", startUs: 0, endUs: 1, top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseWindow("nonexistent.etl", startUs: 0, endUs: 1, top: 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseWindow("nonexistent.etl", startUs: -1, endUs: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseWindow("nonexistent.etl", startUs: 2, endUs: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseWindow("nonexistent.etl", startUs: 0, endUs: 1, maxWindowDurationUs: 0));
    }

    [Fact]
    public void DiagnoseWindow_GuardsWideWindowsBeforeRunningSubtools()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var resp = tools.DiagnoseWindow("nonexistent.etl", startUs: 0, endUs: 1_000_000, maxWindowDurationUs: 999_999);

        Assert.Empty(resp.ExecutedToolCalls);
        Assert.Null(resp.Pressure);
        Assert.Contains(resp.Warnings, warning => warning.Contains("window is"));
        Assert.Contains(resp.NotConcluded, item => item.Code == "window_too_wide");
    }

    [Fact]
    public void DiagnoseWindow_AggregatesHardFaultEvidenceAroundLatencyTimestamp()
    {
        var cache = new TraceCache(capacity: 2);
        var hardFaultTools = new HardFaultTools(cache);
        var diagnoseTools = new DiagnoseTools(cache);
        var slowest = hardFaultTools.HardFaultByFile(MmapFixturePath, top: 100, orderBy: "max_latency").Rows[0];

        var resp = diagnoseTools.DiagnoseWindow(
            MmapFixturePath,
            startUs: slowest.MaxLatencyTimeUs,
            endUs: slowest.MaxLatencyTimeUs + 1,
            top: 10);

        Assert.Equal(slowest.MaxLatencyTimeUs, resp.WindowStartUs);
        Assert.Equal(slowest.MaxLatencyTimeUs + 1, resp.WindowEndUs);
        Assert.Contains(resp.HardFaultsByMaxLatency, row =>
            row.File == slowest.File &&
            row.MaxLatencyUs == slowest.MaxLatencyUs &&
            row.MaxLatencyTimeUs == slowest.MaxLatencyTimeUs);
        Assert.Contains(resp.Evidence, item =>
            item.EvidenceType == "hard_fault_max_latency" &&
            item.MetricValue == slowest.MaxLatencyUs &&
            item.TimeUs == slowest.MaxLatencyTimeUs);
        Assert.Contains(resp.NextTools, tool =>
            tool.ToolName == "diagnose_window" &&
            tool.StartUs <= slowest.MaxLatencyTimeUs &&
            tool.EndUs > slowest.MaxLatencyTimeUs);
    }

    [Fact]
    public void DiagnoseWindow_PropagatesOneWindowToEveryExecutedCall()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var resp = tools.DiagnoseWindow(MmapFixturePath, startUs: 0, endUs: 100_000, top: 5);

        Assert.NotEmpty(resp.ExecutedToolCalls);
        Assert.All(resp.ExecutedToolCalls, call =>
        {
            Assert.Equal(0, call.StartUs);
            Assert.Equal(100_000, call.EndUs);
            Assert.Equal(5, call.Top);
        });
        Assert.Contains(resp.ExecutedToolCalls, call => call.ToolName == "hard_fault_by_file");
        Assert.Contains(resp.ExecutedToolCalls, call => call.ToolName == "hard_fault_by_file" && call.OrderBy == "bytes");
        Assert.Contains(resp.ExecutedToolCalls, call => call.ToolName == "hard_fault_by_file" && call.OrderBy == "max_latency");
        Assert.Contains(resp.ExecutedToolCalls, call => call.ToolName == "file_io_top_files");
        Assert.Contains(resp.ExecutedToolCalls, call => call.ToolName == "memory_resource_analysis");
        Assert.Contains(resp.ExecutedToolCalls, call => call.ToolName == "security_scan_analysis");
        Assert.Contains(resp.ExecutedToolCalls, call => call.ToolName == "wait_analysis");
    }

    [Fact]
    public void DiagnoseHighWait_HasNoConclusionFields()
    {
        var propertyNames = typeof(DiagnoseHighWaitResponse)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Summary", propertyNames);
        Assert.DoesNotContain("Conclusion", propertyNames);
        Assert.DoesNotContain("Diagnosis", propertyNames);
        Assert.DoesNotContain("RootCause", propertyNames);
        Assert.DoesNotContain("Root_Cause", propertyNames);
    }

    [Fact]
    public void CompositeEvidence_UsesFrameMetricsNotBareFunctionLists()
    {
        var evidenceProperties = typeof(CompositeEvidence)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var frameMetricProperties = typeof(FrameMetric)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Functions", evidenceProperties);
        Assert.Contains("Frames", evidenceProperties);
        Assert.Contains("Function", frameMetricProperties);
        Assert.Contains("ExclusiveMetric", frameMetricProperties);
        Assert.Contains("InclusiveMetric", frameMetricProperties);
        Assert.Contains("Unit", frameMetricProperties);
    }

    [Fact]
    public void CompositeNotConcluded_CarriesMetricAndThresholdContext()
    {
        var propertyNames = typeof(CompositeNotConcluded)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("MetricName", propertyNames);
        Assert.Contains("MetricValue", propertyNames);
        Assert.Contains("Unit", propertyNames);
        Assert.Contains("ObservedPct", propertyNames);
        Assert.Contains("ThresholdPct", propertyNames);
    }

    [Fact]
    public void CompositeSchemaDescriptions_GuideLlmInterpretation()
    {
        var evidenceTypeDescription = DescriptionOf<CompositeEvidence>("EvidenceType");
        var evidenceMetricDescription = DescriptionOf<CompositeEvidence>("MetricValue");
        var notConcludedMetricDescription = DescriptionOf<CompositeNotConcluded>("MetricValue");
        var notConcludedObservedDescription = DescriptionOf<CompositeNotConcluded>("ObservedPct");
        var candidatesDescription = DescriptionOf<DiagnoseHighWaitResponse>("Candidates");
        var nextToolHypothesisDescription = DescriptionOf<CompositeNextTool>("TestsHypothesis");
        var toolCallTopDescription = DescriptionOf<CompositeToolCall>("Top");
        var toolCallReplayableDescription = DescriptionOf<CompositeToolCall>("Replayable");
        var toolCallInternalTopDescription = DescriptionOf<CompositeToolCall>("InternalTop");
        var toolCallOrderByDescription = DescriptionOf<CompositeToolCall>("OrderBy");
        var toolDescription = DescriptionOf(typeof(DiagnoseTools).GetMethod(nameof(DiagnoseTools.DiagnoseHighWait))!);
        var fieldDescriptions = new[]
        {
            evidenceTypeDescription,
            evidenceMetricDescription,
            notConcludedMetricDescription,
            notConcludedObservedDescription,
            candidatesDescription,
            nextToolHypothesisDescription,
            toolCallTopDescription,
            toolCallReplayableDescription,
            toolCallInternalTopDescription,
            toolCallOrderByDescription,
        };

        Assert.Contains("process_wait_summary", evidenceTypeDescription);
        Assert.Contains("wait_reason", evidenceTypeDescription);
        Assert.Contains("wait_stack_summary", evidenceTypeDescription);
        Assert.Contains("ready_thread_stack_summary", evidenceTypeDescription);
        Assert.Contains("Raw amount", evidenceMetricDescription);
        Assert.Contains("same MetricName/Unit", evidenceMetricDescription);
        Assert.Contains("compare ObservedPct with ThresholdPct", notConcludedMetricDescription);
        Assert.Contains("Compare this with ThresholdPct", notConcludedObservedDescription);
        Assert.Contains("not impact", candidatesDescription);
        Assert.Contains("Hypothesis", nextToolHypothesisDescription);
        Assert.Contains("not an ordered checklist", nextToolHypothesisDescription);
        Assert.Contains("Replayable public MCP top", toolCallTopDescription);
        Assert.Contains("audit-only", toolCallReplayableDescription);
        Assert.Contains("do not replay public tool expecting identical output", toolCallReplayableDescription);
        Assert.Contains("Internal-only top", toolCallInternalTopDescription);
        Assert.Contains("do not pass to public tool", toolCallInternalTopDescription);
        Assert.Contains("orderBy", toolCallOrderByDescription);
        Assert.Contains("Candidates are ordered by total blocked microseconds", toolDescription);
        Assert.Contains("NextTools are optional hypothesis checks", toolDescription);
        Assert.All(fieldDescriptions, description => Assert.True(
            description.Length <= 140,
            $"Field description is too long for schema guidance: {description}"));
        Assert.True(toolDescription.Length <= 420, $"Tool description is too long: {toolDescription}");
    }

    [Fact]
    public void DiagnoseHighWait_OnCpuFixtureDegradesToWaitReasonsWithoutStackEvidence()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var resp = tools.DiagnoseHighWait(FixturePath, maxCandidates: 3);

        Assert.NotEmpty(resp.Candidates);
        Assert.Contains(resp.ExecutedToolCalls, call =>
            call.ToolName == "wait_analysis" &&
            !call.Replayable &&
            call.Top is null &&
            call.InternalTop == int.MaxValue &&
            call.InternalNote?.Contains("public wait_analysis caps top", StringComparison.OrdinalIgnoreCase) == true);
        Assert.All(resp.ExecutedToolCalls.Where(call => call.Top.HasValue), call =>
            Assert.InRange(call.Top!.Value, 1, 1000));
        Assert.DoesNotContain(resp.ExecutedToolCalls, call => call.ToolName == "wait_top_stacks");
        Assert.DoesNotContain(resp.ExecutedToolCalls, call => call.ToolName == "ready_thread_top_stacks");
        Assert.Contains(resp.NotConcluded, item => item.Code == "missing_stackwalks");
        Assert.Contains(resp.Evidence, item => item.EvidenceType == "process_wait_summary");
        Assert.DoesNotContain(resp.Evidence, item => item.EvidenceType.Contains("stack", StringComparison.OrdinalIgnoreCase));
        Assert.All(resp.Candidates, candidate => Assert.Null(candidate.WaitStacksCallId));

        var callIds = resp.ExecutedToolCalls.Select(call => call.CallId).ToHashSet(StringComparer.Ordinal);
        Assert.All(resp.Evidence, item => Assert.Contains(item.CallId, callIds));
        Assert.All(resp.Evidence, item =>
        {
            Assert.NotNull(item.Frames);
            Assert.DoesNotContain(item.Frames, frame => string.IsNullOrWhiteSpace(frame.Function));
        });
        Assert.NotEmpty(resp.NextTools);
        Assert.All(resp.NextTools, item => Assert.False(string.IsNullOrWhiteSpace(item.TestsHypothesis)));
    }

    [Fact]
    public void DiagnoseHighWait_EvidenceLabelsDoNotCarryConclusions()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));
        var resp = tools.DiagnoseHighWait(FixturePath, maxCandidates: 3);
        var bannedFragments = new[]
        {
            "root cause",
            "caused by",
            "diagnosis",
            "because",
            "is responsible",
        };

        foreach (var label in resp.Evidence.Select(item => item.Label))
        {
            foreach (var fragment in bannedFragments)
                Assert.DoesNotContain(fragment, label, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DiagnoseHighWait_CandidatesUseFullWaitAggregation()
    {
        var cache = new TraceCache(capacity: 2);
        var tools = new DiagnoseTools(cache);
        var resp = tools.DiagnoseHighWait(FixturePath, maxCandidates: 5);

        var trace = cache.Get(FixturePath);
        var expected = WaitAnalysis.Analyze(trace, top: int.MaxValue, pid: null, startUs: null, endUs: null)
            .Rows
            .Where(row => row.Pid > 0 && row.Pid != 4)
            .GroupBy(row => row.Pid)
            .Select(group => new
            {
                Pid = group.Key,
                BlockedUs = group.Sum(row => row.BlockedUs),
            })
            .Where(row => row.BlockedUs > 0)
            .OrderByDescending(row => row.BlockedUs)
            .Take(5)
            .ToList();

        Assert.Equal(expected.Select(row => row.Pid), resp.Candidates.Select(candidate => candidate.Pid));
        foreach (var candidate in resp.Candidates)
        {
            var expectedBlockedUs = expected.Single(row => row.Pid == candidate.Pid).BlockedUs;
            Assert.Equal(expectedBlockedUs, candidate.TotalBlockedUs);
        }
    }

    [Fact]
    public void DiagnoseHighWait_SchedulerGateUsesTotalBlockedTimeDenominator()
    {
        var method = typeof(DiagnoseTools).GetMethod(
            "SchedulerWaitPct",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var topReasons = new[]
        {
            new WaitReasonBucket("WrDispatchInt", BlockedUs: 60, Count: 1),
            new WaitReasonBucket("WrUserRequest", BlockedUs: 40, Count: 1),
        };

        var pct = Assert.IsType<double>(method.Invoke(null, new object[] { topReasons, 1_000L }));

        Assert.Equal(0.06, pct, precision: 6);
    }

    [Fact]
    public void DiagnoseHighWait_ReportsMissingStackwalkEvenWhenWindowHasNoCandidates()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var resp = tools.DiagnoseHighWait(
            FixturePath, pid: int.MaxValue, startUs: 0, endUs: 100_000);

        Assert.Empty(resp.Candidates);
        Assert.Contains(resp.NotConcluded, item => item.Code == "no_wait_candidates");
        Assert.Contains(resp.NotConcluded, item => item.Code == "missing_stackwalks");
    }

    [Fact]
    public void DiagnoseHighWait_PropagatesOneWindowToEveryExecutedCall()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var resp = tools.DiagnoseHighWait(FixturePath, startUs: 0, endUs: 100_000, maxCandidates: 2);

        Assert.NotEmpty(resp.ExecutedToolCalls);
        Assert.All(resp.ExecutedToolCalls, call =>
        {
            Assert.Equal(0, call.StartUs);
            Assert.Equal(100_000, call.EndUs);
        });
    }

    private static string DescriptionOf<T>(string propertyName)
    {
        var property = typeof(T).GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Property {typeof(T).Name}.{propertyName} was not found.");
        return DescriptionOf(property);
    }

    private static string DescriptionOf(System.Reflection.MemberInfo member)
    {
        var attribute = Assert.IsType<DescriptionAttribute>(
            Attribute.GetCustomAttribute(member, typeof(DescriptionAttribute)));
        return attribute.Description;
    }

    private static DiagnoseSlowStartupResponse ComposeDeterministicSlowStartup(
        long startupCpuUs = 100,
        IReadOnlyList<string>? schedulerWarnings = null,
        Action<ThreadAnalysisScope>? onCpu = null)
    {
        var key = new ProcessInstanceKey(42, 100);
        var lifetime = new ProcessLifetime(
            key,
            EndUs: 900,
            StartObserved: true,
            EndObserved: true);
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 1_000,
            processes: [lifetime],
            threads: Array.Empty<ThreadLifecycleEvent>());
        var catalog = StartupProcessCatalog.Build(
            [new StartupProcessMetadata(
                lifetime,
                ParentPid: 1,
                Name: "deterministic.exe",
                LifetimeCpuUs: 200,
                LifetimeImageLoadCount: 0)],
            startupWindowUs: 500,
            traceDurationUs: 1_000,
            nameSubstring: null,
            maxCollectionItems: 8);
        IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> scheduler =
            new Dictionary<ProcessInstanceKey, StartupSchedulerMetrics>
            {
                [key] = new(
                    StartupCpuUs: startupCpuUs,
                    StartupBlockedUs: 400,
                    BlockedUsByReason: new Dictionary<string, long>
                    {
                        ["WrUserRequest"] = 400,
                    },
                    RunningIntervalCount: 1,
                    BlockedIntervalCount: 1,
                    BlockedCountByReason: new Dictionary<string, long>
                    {
                        ["WrUserRequest"] = 1,
                    }),
            };
        var imageLoads = new StartupImageLoadResult(
            new Dictionary<ProcessInstanceKey, StartupImageLoadBucket>
            {
                [key] = new(
                    TotalAvailable: 0,
                    FirstLoads: Array.Empty<ImageLoadRow>(),
                    HasMore: false),
            },
            UnresolvedProcessInstanceCount: 0,
            AmbiguousProcessInstanceCount: 0);

        return DiagnoseTools.ComposeSlowStartup(
            identities,
            catalog,
            scheduler,
            schedulerWarnings ?? Array.Empty<string>(),
            imageLoads,
            nameSubstring: null,
            maxCandidates: 1,
            minWaitRatio: 0,
            topImageLoads: 5,
            topCpu: 3,
            slowFirstImageLoadThresholdUs: 0,
            topWindowEvidence: 3,
            analyzeCpu: scope =>
            {
                onCpu?.Invoke(scope);
                return new CpuTopFunctionsResponse(
                    Rows: Array.Empty<CpuFunctionRow>(),
                    Stats: new SymbolStats(
                        Resolved: 0,
                        Unresolved: 0,
                        ResolutionRate: 1,
                        TopUnresolvedModules: Array.Empty<UnresolvedModule>()),
                    Warnings: Array.Empty<string>(),
                    SelectedProcess: key);
            },
            diagnoseWindow: (_, _, _) =>
                throw new InvalidOperationException("No image-load gap is expected."));
    }
}
