using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class ResultContractSerializationTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TraceAndScopedDiagnostics_SerializeAsDistinctFields()
    {
        var wait = new WaitAnalysisResponse(
            Rows: [],
            TotalCSwitches: 99,
            Warnings: [],
            UnmatchedBlockedIntervalCount: 7,
            TraceUnmatchedBlockedIntervalCount: 7,
            ScopedUnmatchedBlockedIntervalCount: 2,
            TraceHasContextSwitches: true,
            TraceCSwitches: 101,
            MatchedIntervalCount: 4,
            TraceIdentityUnresolvedCSwitchSideCount: 3,
            ScopedIdentityUnresolvedCSwitchSideCount: 1);
        var waitJson = JsonSerializer.SerializeToElement(wait, WebJson);

        Assert.Equal(7, waitJson.GetProperty("unmatchedBlockedIntervalCount").GetInt32());
        Assert.Equal(7, waitJson.GetProperty("traceUnmatchedBlockedIntervalCount").GetInt32());
        Assert.Equal(2, waitJson.GetProperty("scopedUnmatchedBlockedIntervalCount").GetInt32());
        Assert.True(waitJson.GetProperty("traceHasContextSwitches").GetBoolean());
        Assert.Equal(101, waitJson.GetProperty("traceCSwitches").GetInt64());
        Assert.Equal(4, waitJson.GetProperty("matchedIntervalCount").GetInt64());
        Assert.Equal(3, waitJson.GetProperty("traceIdentityUnresolvedCSwitchSideCount").GetInt64());
        Assert.Equal(1, waitJson.GetProperty("scopedIdentityUnresolvedCSwitchSideCount").GetInt64());

        var gc = new GcAnalysisResponse(
            Pid: null,
            TotalGcCount: 0,
            Gen0Count: 0,
            Gen1Count: 0,
            Gen2Count: 0,
            TotalGcUs: 0,
            TotalPauseUs: 0,
            Events: [],
            Warnings: [],
            TotalFullGcUs: 0,
            TotalAccountedGcUs: 0,
            TotalFullPauseUs: 0,
            TotalAccountedPauseUs: 0,
            AccountingMode: "window_clipped",
            IncompleteClrIdentityCount: 0,
            UnmatchedGcIntervalCount: 5,
            UnmatchedPauseIntervalCount: 4,
            InvalidIntervalCount: 3,
            TraceUnmatchedGcIntervalCount: 5,
            ScopedUnmatchedGcIntervalCount: 1,
            TraceUnmatchedPauseIntervalCount: 4,
            ScopedUnmatchedPauseIntervalCount: 2,
            TraceInvalidIntervalCount: 3,
            ScopedInvalidIntervalCount: 1,
            TraceIdentityUnresolvedEndpointCount: 8,
            ScopedIdentityUnresolvedEndpointCount: 2,
            TraceUnmatchedGcStartCount: 3,
            TraceUnmatchedGcStopCount: 2,
            TraceUnmatchedPauseStartCount: 1,
            TraceUnmatchedPauseStopCount: 3);
        var gcJson = JsonSerializer.SerializeToElement(gc, WebJson);

        Assert.Equal(5, gcJson.GetProperty("traceUnmatchedGcIntervalCount").GetInt32());
        Assert.Equal(1, gcJson.GetProperty("scopedUnmatchedGcIntervalCount").GetInt32());
        Assert.Equal(4, gcJson.GetProperty("traceUnmatchedPauseIntervalCount").GetInt32());
        Assert.Equal(2, gcJson.GetProperty("scopedUnmatchedPauseIntervalCount").GetInt32());
        Assert.Equal(8, gcJson.GetProperty("traceIdentityUnresolvedEndpointCount").GetInt64());
        Assert.Equal(2, gcJson.GetProperty("scopedIdentityUnresolvedEndpointCount").GetInt64());
        Assert.Equal(3, gcJson.GetProperty("traceUnmatchedGcStartCount").GetInt32());
        Assert.Equal(2, gcJson.GetProperty("traceUnmatchedGcStopCount").GetInt32());

        var jit = new JitAnalysisResponse(
            Pid: null,
            TotalMethodsJitted: 0,
            TotalJitUs: 0,
            TopMethods: [],
            Warnings: [],
            TotalFullJitUs: 0,
            TotalAccountedJitUs: 0,
            HasMore: false,
            UnmatchedIntervalCount: 6,
            InvalidIntervalCount: 4,
            AccountingMode: "window_clipped",
            TraceUnmatchedIntervalCount: 6,
            ScopedUnmatchedIntervalCount: 2,
            TraceInvalidIntervalCount: 4,
            ScopedInvalidIntervalCount: 1,
            TraceIdentityUnresolvedEndpointCount: 5,
            ScopedIdentityUnresolvedEndpointCount: 1,
            TraceUnmatchedStartCount: 4,
            TraceUnmatchedStopCount: 2,
            ScopedUnmatchedStartCount: 1,
            ScopedUnmatchedStopCount: 1);
        var jitJson = JsonSerializer.SerializeToElement(jit, WebJson);

        Assert.Equal(6, jitJson.GetProperty("traceUnmatchedIntervalCount").GetInt32());
        Assert.Equal(2, jitJson.GetProperty("scopedUnmatchedIntervalCount").GetInt32());
        Assert.Equal(4, jitJson.GetProperty("traceInvalidIntervalCount").GetInt32());
        Assert.Equal(1, jitJson.GetProperty("scopedInvalidIntervalCount").GetInt32());
        Assert.Equal(5, jitJson.GetProperty("traceIdentityUnresolvedEndpointCount").GetInt64());
        Assert.Equal(1, jitJson.GetProperty("scopedIdentityUnresolvedEndpointCount").GetInt64());
        Assert.Equal(4, jitJson.GetProperty("traceUnmatchedStartCount").GetInt32());
        Assert.Equal(2, jitJson.GetProperty("traceUnmatchedStopCount").GetInt32());
    }

    [Fact]
    public void ReplayAndUnpairedFields_AreSerializedWithoutDroppingZeroOrOptionalSelectors()
    {
        var thread = new WaitAnalysisRow(
            Pid: 42,
            ProcessName: "worker",
            Tid: 9,
            CpuUs: 1,
            BlockedUs: 2,
            WaitRatio: 2,
            ContextSwitches: 3,
            TopWaitReasons: [],
            ProcessStartUs: 100,
            ThreadGeneration: 2,
            ThreadStartUs: 125);
        var threadJson = JsonSerializer.SerializeToElement(thread, WebJson);
        Assert.Equal(125, threadJson.GetProperty("threadStartUs").GetInt64());
        Assert.Equal(2, threadJson.GetProperty("threadGeneration").GetInt64());

        var connections = new NetConnectionsResponse(
            Pid: 42,
            TotalConnections: 0,
            Connections: [],
            Warnings: [],
            MatchedEventCount: 1,
            NoDataReason: "unpaired_endpoints_in_scope",
            UnpairedCloseCount: 1);
        var connectionJson = JsonSerializer.SerializeToElement(connections, WebJson);
        Assert.Equal(1, connectionJson.GetProperty("unpairedCloseCount").GetInt64());
        Assert.Equal("unpaired_endpoints_in_scope", connectionJson.GetProperty("noDataReason").GetString());

        var unload = new UnloadTraceResponse(
            Path: @"C:\trace.etl",
            CacheEntryRetired: false,
            NextLoadForcesEtlxRefresh: true,
            Warnings: [],
            RefreshRequestedForCurrentServerProcess: true);
        var unloadJson = JsonSerializer.SerializeToElement(unload, WebJson);
        Assert.False(unloadJson.GetProperty("cacheEntryRetired").GetBoolean());
        Assert.True(unloadJson.GetProperty("nextLoadForcesEtlxRefresh").GetBoolean());
        Assert.True(unloadJson.GetProperty("refreshRequestedForCurrentServerProcess").GetBoolean());
        Assert.Equal(
            "current_server_process_only",
            unloadJson.GetProperty("refreshRequestLifetime").GetString());
    }

    [Fact]
    public void NewDiagnosticFields_HaveDescriptionsForStructuredSchemas()
    {
        AssertDescriptions<CallerCalleeResponse>(
            "TraceUnmatchedIntervalCount", "ScopedUnmatchedIntervalCount",
            "TraceHasContextSwitches", "ScopedCSwitches", "ScopedStackedSwitches",
            "ScopedStackCoveragePct");
        AssertDescriptions<WaitAnalysisResponse>(
            "TraceUnmatchedBlockedIntervalCount", "ScopedUnmatchedBlockedIntervalCount",
            "TraceHasContextSwitches", "TraceCSwitches", "MatchedIntervalCount",
            "TraceIdentityUnresolvedCSwitchSideCount",
            "ScopedIdentityUnresolvedCSwitchSideCount");
        AssertDescriptions<WaitTopStacksResponse>(
            "TraceUnmatchedBlockedIntervalCount", "ScopedUnmatchedBlockedIntervalCount",
            "TraceHasContextSwitches", "ScopedCSwitches", "ScopedStackedSwitches",
            "ScopedStackCoveragePct", "TraceCSwitches", "MatchedIntervalCount",
            "TraceIdentityUnresolvedCSwitchSideCount",
            "ScopedIdentityUnresolvedCSwitchSideCount");
        AssertDescriptions<CpuPreciseResponse>(
            "NoDataReason", "TraceIdentityUnresolvedCSwitchSideCount",
            "ScopedIdentityUnresolvedCSwitchSideCount");
        AssertDescriptions<GcAnalysisResponse>(
            "TraceUnmatchedGcIntervalCount", "ScopedUnmatchedGcIntervalCount",
            "TraceUnmatchedPauseIntervalCount", "ScopedUnmatchedPauseIntervalCount",
            "TraceInvalidIntervalCount", "ScopedInvalidIntervalCount",
            "TraceIdentityUnresolvedEndpointCount", "ScopedIdentityUnresolvedEndpointCount",
            "TraceUnmatchedGcStartCount", "TraceUnmatchedGcStopCount",
            "TraceUnmatchedPauseStartCount", "TraceUnmatchedPauseStopCount");
        AssertDescriptions<JitAnalysisResponse>(
            "TraceUnmatchedIntervalCount", "ScopedUnmatchedIntervalCount",
            "TraceInvalidIntervalCount", "ScopedInvalidIntervalCount",
            "TraceIdentityUnresolvedEndpointCount", "ScopedIdentityUnresolvedEndpointCount",
            "TraceUnmatchedStartCount", "TraceUnmatchedStopCount",
            "ScopedUnmatchedStartCount", "ScopedUnmatchedStopCount");
        AssertDescriptions<CpuPreciseThreadRow>("ThreadGeneration", "ThreadStartUs");
        AssertDescriptions<WaitAnalysisRow>("ThreadGeneration", "ThreadStartUs");
        AssertDescriptions<NetConnectionsResponse>(
            "MatchedEventCount", "NoDataReason", "UnpairedCloseCount",
            "TraceIdentityUnresolvedEndpointCount",
            "ScopedIdentityUnresolvedEndpointCount");
        AssertDescriptions<MemoryResourceResponse>(
            "TraceIdentityUnresolvedEventCount",
            "ScopedIdentityUnresolvedEventCount");
        AssertDescriptions<GcHeapStatsResponse>(
            "ScopeStatus", "NoDataReason", "TraceIdentityUnresolvedEventCount",
            "ScopedIdentityUnresolvedEventCount");
        AssertDescriptions<FinalizerAnalysisResponse>(
            "NoDataReason", "TraceIdentityUnresolvedEventCount",
            "ScopedIdentityUnresolvedEventCount");
        AssertDescriptions<SecurityScanAnalysisResponse>(
            "ScopedUnattributedEventCount");
        AssertDescriptions<UnloadTraceResponse>(
            "Path", "CacheEntryRetired", "NextLoadForcesEtlxRefresh", "Warnings",
            "RefreshRequestedForCurrentServerProcess", "RefreshRequestLifetime");
        AssertDescriptions<InspectTraceResponse>("AnalysisContract");
        AssertDescriptions<SymbolStatus>("CacheDir");
        AssertDescriptions<InspectSymbolQuality>("CacheDir");
        AssertDescriptions<AnalysisContractGuidance>(
            "ScopeRule", "TraceScopedRule", "CountRule", "CapabilityRule",
            "StackRule", "SymbolRule", "ThreadReplayRule", "CausalityRule",
            "NoDataReasons");
        AssertDescriptions<NoDataReasonGuidance>(
            "ScopeNotFound", "AmbiguousProcessInstance", "ProcessStartRequired",
            "AmbiguousThreadInstance",
            "EventClassNotObserved", "NoEventsInScope", "SourceEventsUnattributed",
            "NoCompletedIntervalsInScope", "StacksUnavailable", "FocusNotFound");

        AssertDescriptionContains<CallerCalleeResponse>(
            "NoDataReason", "ambiguous_process_instance", "ambiguous_thread_instance");
        AssertDescriptionContains<WaitTopStacksResponse>(
            "NoDataReason", "ambiguous_process_instance", "ambiguous_thread_instance");
        AssertDescriptionContains<NetConnectionsResponse>(
            "NoDataReason", "scope_not_found", "ambiguous_process_instance");
        AssertDescriptionContains<ClrContentionStacksResponse>(
            "NoDataReason", "scope_not_found", "ambiguous_process_instance",
            "ambiguous_thread_instance");
    }

    private static void AssertDescriptions<T>(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = Assert.IsAssignableFrom<PropertyInfo>(
                typeof(T).GetProperty(propertyName));
            var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrWhiteSpace(description), $"{typeof(T).Name}.{propertyName}");
        }
    }

    private static void AssertDescriptionContains<T>(
        string propertyName,
        params string[] expectedTokens)
    {
        var property = Assert.IsAssignableFrom<PropertyInfo>(
            typeof(T).GetProperty(propertyName));
        var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.False(string.IsNullOrWhiteSpace(description), $"{typeof(T).Name}.{propertyName}");
        foreach (var token in expectedTokens)
            Assert.Contains(token, description, StringComparison.Ordinal);
    }
}
