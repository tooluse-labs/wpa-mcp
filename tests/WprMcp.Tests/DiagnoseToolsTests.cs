using System.ComponentModel;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class DiagnoseToolsTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

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
            Assert.All(resp.Candidates, c => Assert.True(c.WaitRatio is null || c.WaitRatio >= 1.0));
    }

    [Fact]
    public void DiagnoseSlowStartup_SummaryIsObsoleteAndProvenanceIsPopulated()
    {
        var summary = typeof(DiagnoseSlowStartupResponse).GetProperty("Summary");
        Assert.NotNull(summary);
        Assert.NotNull(summary.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false).SingleOrDefault());

        var tools = new DiagnoseTools(new TraceCache(capacity: 2));
        var resp = tools.DiagnoseSlowStartup(FixturePath, minWaitRatio: 0.0, maxCandidates: 1, startupWindowUs: 123_456);

        Assert.NotNull(resp.ExecutedToolCalls);
        Assert.Contains(resp.ExecutedToolCalls!, call => call.ToolName == "list_processes");
        if (resp.Candidates.Count == 0) return;

        var pid = resp.Candidates[0].Pid;
        Assert.Contains(resp.ExecutedToolCalls!, call =>
            call.ToolName == "wait_analysis" &&
            call.StartUs is null &&
            call.EndUs is null &&
            !call.Replayable &&
            call.Top is null &&
            call.InternalTop == int.MaxValue);
        Assert.Contains(resp.ExecutedToolCalls!, call =>
            call.ToolName == "cpu_top_functions" &&
            call.Pid == pid &&
            call.StartUs.HasValue &&
            call.EndUs - call.StartUs == 123_456);
    }

    [Fact]
    public void DiagnoseSlowStartup_WaitEvidenceReportsFullBlockedTime()
    {
        var cache = new TraceCache(capacity: 2);
        var tools = new DiagnoseTools(cache);
        var resp = tools.DiagnoseSlowStartup(FixturePath, minWaitRatio: 0.0, maxCandidates: 5);
        if (resp.Candidates.Count == 0) return;

        var trace = cache.Get(FixturePath);
        var waitRows = WaitAnalysis.Analyze(trace, top: int.MaxValue, pid: null, startUs: null, endUs: null)
            .Rows
            .GroupBy(row => row.Pid)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.BlockedUs));

        Assert.NotNull(resp.Evidence);
        foreach (var candidate in resp.Candidates)
        {
            var expected = waitRows.GetValueOrDefault(candidate.Pid);
            var evidence = Assert.Single(resp.Evidence!, item =>
                item.EvidenceType == "process_wait_summary" && item.Pid == candidate.Pid);
            Assert.Equal(expected, evidence.MetricValue);
            Assert.True(evidence.MetricValue >= evidence.TopWaitReasons.Sum(reason => reason.BlockedUs));
        }
    }

    [Fact]
    public void DiagnoseHighWait_RejectsBadArguments()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));

        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", maxCandidates: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", maxCandidates: 21));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", topStacks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", topReadyStacks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiagnoseHighWait("nonexistent.etl", startUs: -1));
        Assert.Throws<ArgumentException>(() => tools.DiagnoseHighWait("nonexistent.etl", startUs: 2, endUs: 1));
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

        var resp = tools.DiagnoseHighWait(FixturePath, startUs: 10_000_000_000, endUs: 10_000_000_001);

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
}
