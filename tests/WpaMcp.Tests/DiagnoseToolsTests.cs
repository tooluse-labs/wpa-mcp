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
    public void SlowStartupDiscovery_BoundsExclusionEvidenceAndKeepsExactTotals()
    {
        var exclusions = Enumerable.Range(0, 25)
            .Select(index => new StartupProcessExclusion(
                new ProcessInstanceKey(index + 1, index * 10L),
                $"resident-{index}",
                "startup_start_not_observed",
                "ProcessStart was not observed."))
            .ToArray();
        var catalog = new StartupProcessCatalogResult(
            Eligible: Array.Empty<StartupProcessObservation>(),
            TotalEligibleCount: 0,
            EligibleHasMore: false,
            Excluded: exclusions,
            TotalUnobservedStartCount: exclusions.Length,
            TotalOtherExcludedCount: 0,
            ExcludedHasMore: false,
            ExplicitNameTarget: false);
        var response = DiagnoseTools.ComposeSlowStartup(
            TraceIdentityIndex.BuildFromEvents(
                traceEndUs: 1_000,
                processes: Array.Empty<ProcessLifetime>(),
                threads: Array.Empty<ThreadLifecycleEvent>()),
            catalog,
            new Dictionary<ProcessInstanceKey, StartupSchedulerMetrics>(),
            Array.Empty<string>(),
            new StartupImageLoadResult(
                new Dictionary<ProcessInstanceKey, StartupImageLoadBucket>(),
                UnresolvedProcessInstanceCount: 0,
                AmbiguousProcessInstanceCount: 0),
            nameSubstring: null,
            maxCandidates: 1,
            minWaitRatio: 0,
            topImageLoads: 1,
            topCpu: 1,
            slowFirstImageLoadThresholdUs: 0,
            topWindowEvidence: 1,
            analyzeCpu: _ => throw new InvalidOperationException("No candidate expected."),
            diagnoseWindow: (_, _, _) => throw new InvalidOperationException("No candidate expected."));

        Assert.NotNull(response.Discovery);
        Assert.Equal(25, response.Discovery!.ExcludedStartupInstanceCount);
        Assert.Equal(25, response.Discovery.ExcludedUnobservedStartCount);
        Assert.Equal(0, response.Discovery.OtherExcludedStartupInstanceCount);
        Assert.Equal(
            StartupDiscoverySummary.ExcludedSampleLimit,
            response.Discovery.ExcludedSamples.Count);
        Assert.True(response.Discovery.ExcludedSamplesHasMore);
        CompositeResultContractValidator.Validate(response);
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
        AssertPlannerNotAdmitted(resp.PlannerExecution, "diagnose_slow_startup");
        Assert.NotNull(resp.Warnings);
        Assert.Equal(resp.Candidates.Count, resp.CandidateBoundary.Returned);
        Assert.False(resp.CandidateBoundary.ContinuationAvailable);
        Assert.Equal(ToolSectionTotalState.Exact, resp.CandidateBoundary.TotalState);
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
        AssertPlannerNotAdmitted(gap.Window.PlannerExecution, "diagnose_window");
        Assert.True(gap.FirstImageLoadTimeUs >= gap.ProcessStartUs);
        Assert.Equal(gap.FirstImageLoadTimeUs - gap.ProcessStartUs, gap.FirstImageLoadOffsetUs);
        Assert.Equal(gap.Pid, gap.Window.Pid);
        Assert.Equal(gap.ProcessStartUs, gap.ChildStartUs);
        Assert.Equal(gap.FirstImageLoadTimeUs, gap.ChildEndUs);
        Assert.Equal(gap.ChildStartUs, gap.Window.WindowStartUs);
        Assert.Equal(gap.ChildEndUs, gap.Window.WindowEndUs);
        Assert.Equal(
            new ProcessInstanceKey(gap.Pid, gap.ProcessStartUs),
            gap.Window.SelectedProcess);
        Assert.Equal("single_process", gap.Window.ScopeMode);
        Assert.True(gap.ChildStartUs >= gap.ParentWindow.StartUs);
        Assert.True(gap.ChildEndUs <= gap.ParentWindow.EndUs);
        Assert.NotEmpty(gap.Window.ExecutedToolCalls);
        Assert.All(gap.Window.ExecutedToolCalls, childCall =>
        {
            Assert.Equal(gap.Pid, childCall.Pid);
            if (childCall.ToolName == "security_scan_analysis")
                Assert.Equal(gap.ProcessStartUs, childCall.TargetProcessStartUs);
            else
                Assert.Equal(gap.ProcessStartUs, childCall.ProcessStartUs);
        });
        Assert.All(gap.Window.NextTools.Where(tool => tool.Pid == gap.Pid), tool =>
            Assert.Equal(gap.ProcessStartUs, tool.ProcessStartUs));
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
    public void DiagnoseSlowStartup_ExposesNestedDiagnosticsOnceWithoutDanglingReferences()
    {
        const string schedulerWarning = "scheduler warning";
        const string cpuWarning = "cpu warning";
        const string childWarning = "child window warning";
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
                LifetimeImageLoadCount: 1)],
            startupWindowUs: 500,
            traceDurationUs: 1_000,
            nameSubstring: null,
            maxCollectionItems: 8);
        IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> scheduler =
            new Dictionary<ProcessInstanceKey, StartupSchedulerMetrics>
            {
                [key] = new(
                    StartupCpuUs: 100,
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
        var firstLoad = new ImageLoadRow(
            TimeUs: 300,
            TimeFromProcessStartUs: 200,
            FileName: "fixture.dll",
            ImageSize: 1,
            GapFromPrevUs: null);
        var imageLoads = new StartupImageLoadResult(
            new Dictionary<ProcessInstanceKey, StartupImageLoadBucket>
            {
                [key] = new(
                    TotalAvailable: 1,
                    FirstLoads: [firstLoad],
                    HasMore: false),
            },
            UnresolvedProcessInstanceCount: 0,
            AmbiguousProcessInstanceCount: 0);
        var childCall = new CompositeToolCall(
            CallId: "child.file-io",
            ToolName: "file_io_top_files",
            Pid: key.Pid,
            AwakenedPid: null,
            StartUs: key.StartUs,
            EndUs: firstLoad.TimeUs,
            Top: 1,
            CompactStacks: null,
            SummaryOnly: null,
            WhenBuckets: null,
            Warnings: Array.Empty<string>(),
            ProcessStartUs: key.StartUs);
        var childNextTool = new CompositeNextTool(
            ToolName: "wait_top_stacks",
            Reason: "Inspect associated blocking stacks.",
            Pid: key.Pid,
            AwakenedPid: null,
            StartUs: key.StartUs,
            EndUs: firstLoad.TimeUs,
            CompactStacks: false,
            SummaryOnly: false,
            TestsHypothesis: "Check whether the wait maps to a captured code path.",
            ProcessStartUs: key.StartUs);
        var childWindow = new DiagnoseWindowResponse(
            WindowStartUs: key.StartUs,
            WindowEndUs: firstLoad.TimeUs,
            DurationUs: firstLoad.TimeUs - key.StartUs,
            Pid: key.Pid,
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
            Evidence:
            [
                new WindowEvidenceRow(
                    EvidenceType: "file_io_top_file",
                    Label: "fixture",
                    MetricName: "bytes",
                    MetricValue: 1,
                    Unit: "bytes",
                    Pid: key.Pid,
                    ProcessName: "deterministic.exe",
                    File: "fixture.dll",
                    TimeUs: null,
                    Details: Array.Empty<string>(),
                    Samples: Array.Empty<WindowEvidenceSample>(),
                    SamplesBoundary: null,
                    ProcessStartUs: key.StartUs,
                    ScopeMode: "single_process",
                    EvidenceId: "child.evidence.file-io",
                    CallId: childCall.CallId),
            ],
            NotConcluded: Array.Empty<CompositeNotConcluded>(),
            NextTools: [childNextTool],
            ExecutedToolCalls: [childCall],
            Warnings: [childWarning],
            SelectedProcess: key,
            ScopeMode: "single_process",
            IncludedProcesses: [key],
            CapabilityStatus: "observed",
            MatchedEventCount: 1);

        var response = DiagnoseTools.ComposeSlowStartup(
            identities,
            catalog,
            scheduler,
            schedulerWarnings: [schedulerWarning],
            imageLoads,
            nameSubstring: null,
            maxCandidates: 1,
            minWaitRatio: 0,
            topImageLoads: 1,
            topCpu: 1,
            slowFirstImageLoadThresholdUs: 100,
            topWindowEvidence: 1,
            analyzeCpu: _ => new CpuTopFunctionsResponse(
                Rows: Array.Empty<CpuFunctionRow>(),
                Stats: new SymbolStats(
                    Resolved: 0,
                    Unresolved: 0,
                    ResolutionRate: 1,
                    TopUnresolvedModules: Array.Empty<UnresolvedModule>()),
                Warnings: [cpuWarning],
                SelectedProcess: key),
            diagnoseWindow: (_, _, _) => childWindow);

        var gap = Assert.Single(response.FirstImageLoadGapEvidence!);
        var outerGapCall = Assert.Single(response.ExecutedToolCalls!, call =>
            call.CallId == gap.CallId);
        Assert.Empty(outerGapCall.Warnings);
        Assert.Contains("maxWindowDurationUs", outerGapCall.InternalNote);
        Assert.Contains("FirstImageLoadGapEvidence[].Window", outerGapCall.InternalNote);
        Assert.Empty(response.NextTools!);
        Assert.Same(childWindow, gap.Window);

        var reachableWarnings = response.Warnings
            .Concat(response.ExecutedToolCalls!.SelectMany(call => call.Warnings))
            .Concat(gap.Window.Warnings)
            .Concat(gap.Window.ExecutedToolCalls.SelectMany(call => call.Warnings))
            .ToArray();
        Assert.Equal(1, reachableWarnings.Count(warning => warning.Contains(
            schedulerWarning, StringComparison.Ordinal)));
        Assert.Equal(1, reachableWarnings.Count(warning => warning.Contains(
            cpuWarning, StringComparison.Ordinal)));
        Assert.Equal(1, reachableWarnings.Count(warning => warning.Contains(
            childWarning, StringComparison.Ordinal)));
        Assert.Single(gap.Window.NextTools, tool => tool == childNextTool);

        var executedToolCalls = Assert.IsAssignableFrom<IReadOnlyList<CompositeToolCall>>(
            response.ExecutedToolCalls);
        var cpuCall = Assert.Single(executedToolCalls, call =>
            call.ToolName == "cpu_top_functions");
        Assert.Empty(cpuCall.Warnings);
        Assert.Contains("outer Warnings", cpuCall.InternalNote);
        var waitCall = Assert.Single(executedToolCalls, call =>
            call.ToolName == "wait_analysis");
        Assert.Empty(waitCall.Warnings);
        Assert.Contains("outer Warnings", waitCall.InternalNote);

        CompositeResultContractValidator.Validate(response);
        var childCallIds = gap.Window.ExecutedToolCalls
            .Select(call => call.CallId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(gap.Window.Evidence, item =>
        {
            Assert.NotNull(item.CallId);
            Assert.Contains(item.CallId!, childCallIds);
        });
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
            item.BoundaryId ==
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
                .Where(item => item.BoundaryId is not null)
                .Select(item => item.BoundaryId!))
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

        var callIds = (response.ExecutedToolCalls ?? [])
            .Select(call => call.CallId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(response.Candidates, candidate =>
            Assert.Contains(candidate.CallId, callIds));
        Assert.All(response.Discovery?.ExcludedSamples ?? [], exclusion =>
            Assert.Contains(exclusion.CallId, callIds));
        Assert.NotNull(response.Discovery);
        Assert.Contains(response.Discovery!.CallId, callIds);
        Assert.Equal(
            response.Discovery.ConsideredStartupInstanceCount,
            response.Discovery.CandidateInputBoundary.Returned);
        Assert.Equal(
            response.Discovery.EligibleStartupInstanceCount,
            response.Discovery.CandidateInputBoundary.TotalAvailable);
        Assert.Equal(
            response.Discovery.CandidateInputHasMore,
            response.Discovery.CandidateInputBoundary.HasMore);
        Assert.All(response.Candidates, candidate =>
        {
            Assert.Equal(candidate.TopStartupWaitReasons.Count,
                candidate.TopStartupWaitReasonsBoundary.Returned);
            Assert.Equal(candidate.FirstStartupImageLoads.Count,
                candidate.FirstStartupImageLoadsBoundary.Returned);
            Assert.Equal(candidate.TopStartupCpuFunctions?.Count ?? 0,
                candidate.TopStartupCpuFunctionsBoundary.Returned);
        });
        Assert.All(response.FirstImageLoadGapEvidence ?? [], gap =>
        {
            Assert.Equal(8, gap.WindowSectionBoundaries.Count);
            Assert.Equal(8, gap.WindowSectionBoundaries
                .Select(boundary => boundary.SectionPointer)
                .Distinct(StringComparer.Ordinal).Count());
        });
        Assert.Single(response.ExecutedToolCalls ?? [], call =>
            call.ToolName == "startup_candidate_discovery" && !call.Replayable);
        CompositeResultContractValidator.Validate(response);

        if (response.Candidates.Count > 0)
        {
            var broken = response with
            {
                Candidates = response.Candidates
                    .Select((candidate, index) => index == 0
                        ? candidate with { CallId = "missing-projection-call" }
                        : candidate)
                    .ToArray(),
            };
            Assert.Throws<InvalidOperationException>(() =>
                CompositeResultContractValidator.Validate(broken));
        }

        if ((response.Discovery?.ExcludedSamples.Count ?? 0) > 0)
        {
            var discovery = response.Discovery! with
            {
                ExcludedSamples = response.Discovery!.ExcludedSamples
                    .Select((exclusion, index) => index == 0
                        ? exclusion with { CallId = "missing-discovery-call" }
                        : exclusion)
                    .ToArray(),
            };
            var broken = response with { Discovery = discovery };
            Assert.Throws<InvalidOperationException>(() =>
                CompositeResultContractValidator.Validate(broken));
        }
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
    public void DiagnoseSlowStartup_TruncatedInputNeverClaimsGlobalNoCandidates()
    {
        const int retainedCount = 128;
        var lifetimes = Enumerable.Range(1, retainedCount + 1)
            .Select(index => new ProcessLifetime(
                new ProcessInstanceKey(10_000 + index, index * 10L),
                EndUs: 5_000,
                StartObserved: true,
                EndObserved: true))
            .ToArray();
        var metadata = lifetimes
            .Select(lifetime => new StartupProcessMetadata(
                lifetime,
                ParentPid: 1,
                Name: "candidate.exe",
                LifetimeCpuUs: 100,
                LifetimeImageLoadCount: 0))
            .ToArray();
        var catalog = StartupProcessCatalog.Build(
            metadata,
            startupWindowUs: 500,
            traceDurationUs: 5_000,
            nameSubstring: "candidate",
            maxCollectionItems: retainedCount);
        var fullEligible = metadata
            .Select(process => new StartupProcessObservation(
                process,
                StartupWindow.Create(
                    process.Lifetime,
                    startupWindowUs: 500,
                    traceDurationUs: 5_000)))
            .ToArray();
        var omittedProcess = lifetimes[^1].Key;
        var scheduler = fullEligible.ToDictionary(
            process => process.Process,
            process => new StartupSchedulerMetrics(
                StartupCpuUs: process.Process == omittedProcess ? 100 : 0,
                StartupBlockedUs: 100,
                BlockedUsByReason: new Dictionary<string, long>
                {
                    ["WrUserRequest"] = 100,
                },
                RunningIntervalCount: 0,
                BlockedIntervalCount: 1,
                BlockedCountByReason: new Dictionary<string, long>
                {
                    ["WrUserRequest"] = 1,
                }));
        var imageLoads = new StartupImageLoadResult(
            fullEligible.ToDictionary(
                process => process.Process,
                _ => new StartupImageLoadBucket(
                    TotalAvailable: 0,
                    FirstLoads: Array.Empty<ImageLoadRow>(),
                    HasMore: false)),
            UnresolvedProcessInstanceCount: 0,
            AmbiguousProcessInstanceCount: 0);
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 5_000,
            processes: lifetimes,
            threads: Array.Empty<ThreadLifecycleEvent>());
        var qualifier = Assert.Single(SlowStartupProjection.Rank(
            fullEligible,
            scheduler,
            imageLoads.ByProcess,
            nameSubstring: "candidate",
            minWaitRatio: 0,
            maxCandidates: 1));
        Assert.Equal(omittedProcess, qualifier.Process);

        var response = DiagnoseTools.ComposeSlowStartup(
            identities,
            catalog,
            scheduler,
            schedulerWarnings: Array.Empty<string>(),
            imageLoads,
            nameSubstring: "candidate",
            maxCandidates: 1,
            minWaitRatio: 0,
            topImageLoads: 1,
            topCpu: 1,
            slowFirstImageLoadThresholdUs: 0,
            topWindowEvidence: 1,
            analyzeCpu: _ => throw new InvalidOperationException(
                "No candidate should reach CPU analysis."),
            diagnoseWindow: (_, _, _) => throw new InvalidOperationException(
                "No candidate should reach window analysis."));

        Assert.Empty(response.Candidates);
        Assert.NotNull(response.Discovery);
        Assert.True(response.Discovery!.CandidateInputHasMore);
        Assert.Equal(ToolSectionTotalState.Unknown, response.CandidateBoundary.TotalState);
        Assert.Equal(ToolSectionMoreState.Unknown, response.CandidateBoundary.MoreState);
        Assert.False(response.CandidateBoundary.HasMore);
        Assert.False(response.CandidateBoundary.ContinuationAvailable);
        Assert.Equal(retainedCount + 1, response.Discovery.EligibleStartupInstanceCount);
        Assert.Equal(retainedCount, response.Discovery.ConsideredStartupInstanceCount);
        Assert.DoesNotContain(response.NotConcluded!, item => item.Code == "no_candidates");
        var truncation = Assert.Single(response.NotConcluded!, item =>
            item.Code == "upstream_candidate_input_truncated");
        Assert.Equal("partial", truncation.CapabilityStatus);
        Assert.Equal("upstream_candidate_input_truncated", truncation.NoDataReason);
        Assert.Equal("qualifiedCandidateCountLowerBound", truncation.MetricName);
        Assert.Equal(0, truncation.MetricValue);
        Assert.Contains(response.NotConcluded!, item =>
            item.Code == "no_candidates_in_retained_input" &&
            item.CapabilityStatus == "partial");
        Assert.Contains(response.Warnings, warning =>
            warning.Contains("totalState=lower_bound", StringComparison.Ordinal));
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
        Assert.Throws<ArgumentException>(() => tools.DiagnoseHighWait(
            "nonexistent.etl", processStartUs: 1));
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
        Assert.Throws<ArgumentException>(() => tools.DiagnoseWindow(
            "nonexistent.etl", startUs: 0, endUs: 1, processStartUs: 1));
    }

    [Fact]
    public void DiagnoseWindow_WaitSummaryUsesExactTotalNotReturnedRows()
    {
        var waits = new WaitAnalysisResponse(
            Rows:
            [
                new WaitAnalysisRow(
                    Pid: 42,
                    ProcessName: "sample.exe",
                    Tid: 7,
                    CpuUs: 10,
                    BlockedUs: 100,
                    WaitRatio: 10,
                    ContextSwitches: 1,
                    TopWaitReasons:
                    [
                        new WaitReasonBucket("WrUserRequest", 100, 1),
                    ],
                    ProcessStartUs: 5),
            ],
            TotalCSwitches: 1,
            Warnings: Array.Empty<string>(),
            TotalBlockedUs: 350,
            SelectedProcess: new ProcessInstanceKey(42, 5),
            ScopeMode: "single_process");

        var evidence = DiagnoseTools.BuildWaitSummaryEvidence(
            waits,
            pid: 42);

        Assert.NotNull(evidence);
        Assert.Equal(350, evidence!.MetricValue);
        Assert.Equal("sample.exe", evidence.ProcessName);
        Assert.Equal(42, evidence.Pid);
        Assert.Equal(5, evidence.ProcessStartUs);
        Assert.NotEqual(waits.Rows.Sum(row => row.BlockedUs), evidence.MetricValue);
        Assert.NotNull(evidence.DetailsBoundary);
        Assert.Equal("/details", evidence.DetailsBoundary!.SectionPointer);
        Assert.Equal(ToolSectionTotalState.Unknown, evidence.DetailsBoundary.TotalState);
        Assert.Equal(ToolSectionMoreState.Unknown, evidence.DetailsBoundary.MoreState);
        Assert.Equal("returned_rows_sample", evidence.DetailsBoundary.TruncationReason);
    }

    [Fact]
    public void WindowEvidenceSchemaDescriptionsForbidAggregateSampleAttribution()
    {
        var properties = TypeDescriptor.GetProperties(typeof(WindowEvidenceRow));

        Assert.Contains(
            "pid_aggregate",
            properties[nameof(WindowEvidenceRow.Pid)]!.Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "Null for all_processes, pid_aggregate",
            properties[nameof(WindowEvidenceRow.ProcessName)]!.Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "do not own MetricValue",
            properties[nameof(WindowEvidenceRow.File)]!.Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "Null for aggregate metrics",
            properties[nameof(WindowEvidenceRow.TimeUs)]!.Description,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("all_processes")]
    [InlineData("pid_aggregate")]
    public void DiagnoseWindow_WaitSummaryDoesNotAttributeAggregateTotalToFirstReturnedProcess(
        string scopeMode)
    {
        var firstRow = new WaitAnalysisRow(
            Pid: 42,
            ProcessName: "first-returned.exe",
            Tid: 7,
            CpuUs: 10,
            BlockedUs: 100,
            WaitRatio: 10,
            ContextSwitches: 1,
            TopWaitReasons: [],
            ProcessStartUs: 5);
        var secondRow = firstRow with
        {
            Pid = scopeMode == "pid_aggregate" ? 42 : 43,
            ProcessName = "second-returned.exe",
            ProcessStartUs = 25,
        };
        var topOne = new WaitAnalysisResponse(
            Rows: [firstRow],
            TotalCSwitches: 1,
            Warnings: Array.Empty<string>(),
            TotalBlockedUs: 350,
            // Deliberately hostile legacy-looking value: aggregate scopes must
            // ignore it rather than turn the first top-N row into an owner.
            SelectedProcess: new ProcessInstanceKey(42, 5),
            ScopeMode: scopeMode);
        var topAll = topOne with { Rows = [firstRow, secondRow] };

        var evidenceAtTopOne = DiagnoseTools.BuildWaitSummaryEvidence(
            topOne,
            // Hostile caller input: response ScopeMode remains authoritative.
            pid: 42);
        var evidenceAtTopAll = DiagnoseTools.BuildWaitSummaryEvidence(
            topAll,
            pid: 42);

        Assert.NotNull(evidenceAtTopOne);
        Assert.NotNull(evidenceAtTopAll);
        Assert.Equal(350, evidenceAtTopOne!.MetricValue);
        Assert.Equal(evidenceAtTopOne.MetricValue, evidenceAtTopAll!.MetricValue);
        Assert.Null(evidenceAtTopOne.ProcessName);
        Assert.Null(evidenceAtTopAll.ProcessName);
        Assert.Null(evidenceAtTopOne.ProcessStartUs);
        Assert.Null(evidenceAtTopAll.ProcessStartUs);
        Assert.Equal(scopeMode == "pid_aggregate" ? 42 : null, evidenceAtTopOne.Pid);
        Assert.Equal(evidenceAtTopOne.Pid, evidenceAtTopAll.Pid);
    }

    [Fact]
    public void DiagnoseWindow_MissingExactProcessReturnsStructuredScopeFailure()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var response = tools.DiagnoseWindow(
            FixturePath,
            startUs: 0,
            endUs: 100_000,
            pid: int.MaxValue,
            top: 5,
            processStartUs: 123);

        Assert.Null(response.SelectedProcess);
        Assert.Equal("unresolved", response.ScopeMode);
        Assert.False(response.PidReuseObserved);
        Assert.NotNull(response.IncludedProcesses);
        Assert.Empty(response.IncludedProcesses!);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Empty(response.ExecutedToolCalls);
        Assert.Empty(response.Evidence);
        Assert.Null(response.Pressure);
        Assert.Contains(response.NotConcluded, item =>
            item.Code == "scope_not_found" &&
            item.Pid == int.MaxValue &&
            item.ProcessStartUs == 123);
    }

    [Fact]
    public void DiagnoseWindow_ExactProcessPropagatesOneInstanceAndEvidenceScope()
    {
        const int pid = 36772;
        const long processStartUs = 0;
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var response = tools.DiagnoseWindow(
            FixturePath,
            startUs: 0,
            endUs: 100_000,
            pid: pid,
            top: 5,
            processStartUs: processStartUs);

        Assert.Equal(new ProcessInstanceKey(pid, processStartUs), response.SelectedProcess);
        Assert.Equal("single_process", response.ScopeMode);
        Assert.NotNull(response.IncludedProcesses);
        Assert.Contains(
            new ProcessInstanceKey(pid, processStartUs),
            response.IncludedProcesses!);
        Assert.Equal("ok", response.ScopeStatus);
        Assert.NotEmpty(response.ExecutedToolCalls);
        Assert.All(response.ExecutedToolCalls, call =>
        {
            Assert.Equal(pid, call.Pid);
            Assert.Null(call.AwakenedPid);
            Assert.Null(call.AwakenedProcessStartUs);
            if (call.ToolName == "security_scan_analysis")
            {
                Assert.Null(call.ProcessStartUs);
                Assert.Equal(processStartUs, call.TargetProcessStartUs);
            }
            else
            {
                Assert.Equal(processStartUs, call.ProcessStartUs);
                Assert.Null(call.TargetProcessStartUs);
            }
        });
        Assert.All(response.NextTools.Where(tool => tool.Pid == pid), tool =>
            Assert.Equal(processStartUs, tool.ProcessStartUs));
        Assert.All(
            response.NotConcluded.Where(item => item.RelatedCallId is not null),
            item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.ScopeStatus));
                Assert.False(string.IsNullOrWhiteSpace(item.CapabilityStatus));
                if (item.NoDataReason is not null)
                    Assert.False(string.IsNullOrWhiteSpace(item.NoDataReason));
                Assert.Equal(processStartUs, item.ProcessStartUs);
            });
        var fileIoCall = Assert.Single(response.ExecutedToolCalls, call =>
            call.ToolName == "file_io_top_files");
        Assert.NotEmpty(fileIoCall.Warnings);
        Assert.Contains(response.Warnings, warning =>
            warning.StartsWith("file_io_top_files:", StringComparison.Ordinal));
        Assert.All(response.Evidence, item =>
        {
            if (item.EvidenceScope == "window_global")
            {
                Assert.Null(item.Pid);
                Assert.Null(item.ProcessStartUs);
                Assert.Equal("window_global", item.ScopeMode);
            }
            else
            {
                Assert.Equal(pid, item.Pid);
                Assert.Equal(processStartUs, item.ProcessStartUs);
                Assert.Equal("single_process", item.ScopeMode);
            }
        });
    }

    [Fact]
    public void DiagnoseWindow_GuardsWideWindowsBeforeRunningSubtools()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var resp = tools.DiagnoseWindow("nonexistent.etl", startUs: 0, endUs: 1_000_000, maxWindowDurationUs: 999_999);
        AssertPlannerNotAdmitted(resp.PlannerExecution, "diagnose_window");

        Assert.Empty(resp.ExecutedToolCalls);
        Assert.Null(resp.Pressure);
        Assert.Equal("not_evaluated", resp.ScopeMode);
        Assert.Equal("not_evaluated", resp.ScopeStatus);
        Assert.Equal("unknown", resp.CapabilityStatus);
        Assert.Equal("window_too_wide", resp.NoDataReason);
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
        var aggregateBytes = Assert.Single(resp.Evidence, item =>
            item.EvidenceType == "hard_fault_bytes");
        Assert.Null(aggregateBytes.TimeUs);
        Assert.Contains(aggregateBytes.Details, detail =>
            detail.StartsWith(
                "nonRepresentativePointOnly:",
                StringComparison.Ordinal));
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
    public void DiagnoseWindow_ScopedMissingWaitStacksDoesNotClaimTraceWideAbsence()
    {
        using var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var endUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var tools = new DiagnoseTools(cache);

        var response = tools.DiagnoseWindow(
            FixturePath,
            startUs: 0,
            endUs: endUs,
            top: 5,
            maxWindowDurationUs: endUs);

        var missingStacks = Assert.Single(response.NotConcluded, item =>
            item.Code == "scoped_wait_stacks_unavailable");
        Assert.Equal("unknown", missingStacks.CapabilityStatus);
        Assert.Equal("stacks_unavailable", missingStacks.NoDataReason);
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
        var targetProcessStartDescription = DescriptionOf<CompositeToolCall>("TargetProcessStartUs");
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
            targetProcessStartDescription,
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
        Assert.Contains("targetProcessStartUs", targetProcessStartDescription);
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
        AssertPlannerNotAdmitted(resp.PlannerExecution, "diagnose_high_wait");

        Assert.NotEmpty(resp.Candidates);
        Assert.Equal(resp.Candidates.Count, resp.CandidateBoundary.Returned);
        Assert.Equal(ToolSectionTotalState.Exact, resp.CandidateBoundary.TotalState);
        Assert.NotNull(resp.CandidateBoundary.TotalAvailable);
        Assert.True(resp.CandidateBoundary.TotalAvailable >= resp.Candidates.Count);
        Assert.False(resp.CandidateBoundary.ContinuationAvailable);
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

    private static void AssertPlannerNotAdmitted(
        PlannerExecutionTelemetry? telemetry,
        string toolName)
    {
        var planner = Assert.IsType<PlannerExecutionTelemetry>(telemetry);
        Assert.Equal(toolName, planner.ToolName);
        Assert.Equal("not_admitted_evidence_missing", planner.AdmissionStatus);
        Assert.Equal("direct_tool_execution_planner_not_admitted", planner.ExecutionStatus);
        Assert.NotEmpty(planner.MissingEvidence);
        Assert.Empty(planner.LogicalAnalyzersExecuted);
        Assert.Null(planner.PhysicalTracePassCount);
        Assert.Null(planner.ScannedEventCount);
        Assert.Null(planner.MatchedEventCount);
        Assert.Equal("unavailable_not_admitted", planner.PhysicalTracePassCountState);
        Assert.Equal("unavailable_not_admitted", planner.ScannedEventCountState);
        Assert.Equal("unavailable_not_admitted", planner.MatchedEventCountState);
        Assert.Contains("no_single_dispatch_claim", planner.EvidenceBoundaries);
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
    public void DiagnoseHighWait_PropagatesCandidateProcessStartToEvidenceAndFollowups()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var response = tools.DiagnoseHighWait(FixturePath, maxCandidates: 3);

        foreach (var candidate in response.Candidates)
        {
            Assert.Contains(response.Evidence, item =>
                item.Pid == candidate.Pid &&
                item.ProcessStartUs == candidate.ProcessStartUs);
            Assert.All(
                response.NextTools.Where(item =>
                    item.Pid == candidate.Pid || item.AwakenedPid == candidate.Pid),
                item =>
                {
                    if (item.AwakenedPid == candidate.Pid)
                    {
                        Assert.Equal(candidate.ProcessStartUs, item.AwakenedProcessStartUs);
                        Assert.Null(item.ProcessStartUs);
                    }
                    else
                    {
                        Assert.Equal(candidate.ProcessStartUs, item.ProcessStartUs);
                    }
                });
            Assert.All(
                response.ExecutedToolCalls.Where(item =>
                    item.CallId.Contains(
                        $"pid-{candidate.Pid}-start-{candidate.ProcessStartUs}",
                        StringComparison.Ordinal)),
                item =>
                {
                    if (item.AwakenedPid == candidate.Pid)
                    {
                        Assert.Equal(candidate.ProcessStartUs, item.AwakenedProcessStartUs);
                        Assert.Null(item.ProcessStartUs);
                    }
                    else
                    {
                        Assert.Equal(candidate.ProcessStartUs, item.ProcessStartUs);
                    }
                });
        }
    }

    [Fact]
    public void DiagnoseHighWait_CandidatesUseFullWaitAggregation()
    {
        var cache = new TraceCache(capacity: 2);
        var tools = new DiagnoseTools(cache);
        var resp = tools.DiagnoseHighWait(FixturePath, maxCandidates: 5);

        var trace = cache.Get(FixturePath);
        var allExpected = WaitAnalysis.Analyze(trace, top: int.MaxValue, pid: null, startUs: null, endUs: null)
            .Rows
            .Where(row => row.Pid > 0 && row.Pid != 4)
            .GroupBy(row => new ProcessInstanceKey(row.Pid, row.ProcessStartUs))
            .Select(group => new
            {
                Pid = group.Key.Pid,
                ProcessStartUs = group.Key.StartUs,
                BlockedUs = group.Sum(row => row.BlockedUs),
            })
            .Where(row => row.BlockedUs > 0)
            .OrderByDescending(row => row.BlockedUs)
            .ToList();
        var expected = allExpected.Take(5).ToList();

        Assert.Equal(
            expected.Select(row => (row.Pid, row.ProcessStartUs)),
            resp.Candidates.Select(candidate => (candidate.Pid, candidate.ProcessStartUs)));
        Assert.Equal(allExpected.Count, resp.CandidateBoundary.TotalAvailable);
        Assert.Equal(ToolSectionTotalState.Exact, resp.CandidateBoundary.TotalState);
        foreach (var candidate in resp.Candidates)
        {
            var expectedBlockedUs = expected.Single(row => row.Pid == candidate.Pid).BlockedUs;
            Assert.Equal(expectedBlockedUs, candidate.TotalBlockedUs);
        }
    }

    [Fact]
    public void DiagnoseHighWait_CandidateAggregationNeverMergesReusedPidLifetimes()
    {
        var rows = new[]
        {
            new WaitAnalysisRow(
                Pid: 42,
                ProcessName: "first",
                Tid: 101,
                CpuUs: 10,
                BlockedUs: 100,
                WaitRatio: 10,
                ContextSwitches: 2,
                TopWaitReasons: [new WaitReasonBucket("WrUserRequest", 100, 1)],
                ProcessStartUs: 1_000,
                ThreadGeneration: 1),
            new WaitAnalysisRow(
                Pid: 42,
                ProcessName: "second",
                Tid: 202,
                CpuUs: 20,
                BlockedUs: 300,
                WaitRatio: 15,
                ContextSwitches: 3,
                TopWaitReasons: [new WaitReasonBucket("WrLpcReceive", 300, 1)],
                ProcessStartUs: 2_000,
                ThreadGeneration: 1),
        };

        var candidates = DiagnoseTools.BuildHighWaitCandidateAggregates(
            rows,
            requestedPid: 42,
            maxCandidates: 5);

        Assert.Equal(2, candidates.Count);
        Assert.Equal([2_000L, 1_000L], candidates.Select(candidate => candidate.ProcessStartUs));
        Assert.Equal([300L, 100L], candidates.Select(candidate => candidate.TotalBlockedUs));
        Assert.All(candidates, candidate => Assert.Equal(42, candidate.Pid));
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
    public void DiagnoseHighWait_DoesNotBorrowTraceWideStackStateWhenScopeHasNoCandidates()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        var resp = tools.DiagnoseHighWait(
            FixturePath, pid: int.MaxValue, startUs: 0, endUs: 100_000);

        Assert.Empty(resp.Candidates);
        Assert.Contains(resp.NotConcluded, item => item.Code == "scope_not_found");
        Assert.DoesNotContain(resp.NotConcluded, item => item.Code == "missing_stackwalks");
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
