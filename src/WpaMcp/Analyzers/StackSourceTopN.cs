using System.Diagnostics;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Core;
using WpaMcp.Output;
// Disambiguate: TraceEvent's stacks namespace also exports a `CallerCalleeNode` type, but
// our DTO is named the same. Use the WpaMcp DTO unconditionally inside this file.
using CallerCalleeNode = WpaMcp.Output.CallerCalleeNode;

namespace WpaMcp.Analyzers;

internal static class StackMetricAccounting
{
    public const string ExactIntegerCount = "exact_integer_count";
    public const string Float32PerSampleApproximate = "float32_per_sample_approximate";
    public const string ExactLong = "exact_long";

    // TraceEvent's StackSourceSample.Metric is a float. Unit-weight samples remain exact
    // integer counts; byte and duration weights can round before per-frame aggregation.
    public static string ForMetric(string metricName) => metricName.ToLowerInvariant() switch
    {
        "count" or "samples" or "loads" or "alpcevents" or "exceptions" or
        "providerevents" or "readyevents" or "regops" => ExactIntegerCount,
        _ => Float32PerSampleApproximate,
    };
}

internal sealed class DomainStackCoverageAccumulator
{
    private readonly string _domain;
    private readonly string _metricName;
    private readonly string _stackSemantics;
    private long _totalEventCount;
    private long _stackedEventCount;
    private long _totalMetric;
    private long _stackedMetric;

    public DomainStackCoverageAccumulator(
        string domain,
        string metricName = "count",
        string stackSemantics = "event_call_stack")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackSemantics);
        _domain = domain;
        _metricName = metricName;
        _stackSemantics = stackSemantics;
    }

    public string MetricName => _metricName;

    public void Observe(bool hasStack, long metric)
    {
        _totalEventCount = checked(_totalEventCount + 1);
        _totalMetric = checked(_totalMetric + metric);
        if (!hasStack)
            return;

        _stackedEventCount = checked(_stackedEventCount + 1);
        _stackedMetric = checked(_stackedMetric + metric);
    }

    public DomainStackCoverage Snapshot()
    {
        var state = _totalEventCount == 0
            ? "no_events"
            : _stackedEventCount == 0
                ? "no_stacks"
                : _stackedEventCount == _totalEventCount
                    ? "full"
                    : "partial";
        var containsSyntheticUnknown = _stackedEventCount < _totalEventCount;
        return new DomainStackCoverage(
            Domain: _domain,
            TotalEventCount: _totalEventCount,
            StackedEventCount: _stackedEventCount,
            StackCoveragePct: PercentOrNull(_stackedEventCount, _totalEventCount),
            CoverageState: state,
            TotalMetric: _totalMetric,
            StackedMetric: _stackedMetric,
            MetricStackCoveragePct: PercentOrNull(_stackedMetric, _totalMetric),
            MetricName: _metricName,
            ContainsSyntheticUnknown: containsSyntheticUnknown,
            SyntheticUnknownFrame: containsSyntheticUnknown ? "?!?" : null,
            MetricAccounting: StackMetricAccounting.ExactLong,
            StackSemantics: _stackSemantics);
    }

    private static double? PercentOrNull(long value, long total) =>
        total == 0 ? null : 100.0 * value / total;
}

internal sealed record SymbolLookupAttempt(string State, string? Failure)
{
    public static SymbolLookupAttempt Skipped() => new("skipped", null);
    public static SymbolLookupAttempt Executed() => new("executed", null);
    public static SymbolLookupAttempt Failed(string failure) => new("failed", failure);
    public static SymbolLookupAttempt Unknown() => new("unknown", null);
}

internal sealed class SymbolFrameMetricAccumulator
{
    private readonly string _metricName;
    private readonly HashSet<int> _uniqueCodeFrames = new();
    private readonly HashSet<int> _uniqueResolvedCodeFrames = new();
    private readonly HashSet<int> _uniqueUnresolvedCodeFrames = new();
    private readonly HashSet<int> _uniqueExcludedFrames = new();
    private readonly Dictionary<string, long> _unresolvedByModule = new(StringComparer.OrdinalIgnoreCase);
    private long _resolvedMetric;
    private long _unresolvedMetric;
    private long _excludedMetric;

    public SymbolFrameMetricAccumulator(string metricName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        _metricName = metricName;
    }

    public void ObserveCodeFrame(int frameIdentity, string module, bool resolved, long metric)
    {
        var firstObservation = _uniqueCodeFrames.Add(frameIdentity);
        if (resolved)
        {
            if (firstObservation)
                _uniqueResolvedCodeFrames.Add(frameIdentity);
            _resolvedMetric = checked(_resolvedMetric + metric);
        }
        else
        {
            if (firstObservation)
            {
                _uniqueUnresolvedCodeFrames.Add(frameIdentity);
                _unresolvedByModule[module] = checked(
                    _unresolvedByModule.GetValueOrDefault(module) + 1);
            }
            _unresolvedMetric = checked(_unresolvedMetric + metric);
        }
    }

    public void ObserveExcludedFrame(int frameIdentity, long metric)
    {
        _uniqueExcludedFrames.Add(frameIdentity);
        _excludedMetric = checked(_excludedMetric + metric);
    }

    public SymbolStats Snapshot(
        SymbolLookupAttempt lookupAttempt,
        string metricAccounting = "exact_long")
    {
        var uniqueTotal = _uniqueCodeFrames.Count;
        var totalMetric = checked(_resolvedMetric + _unresolvedMetric);
        double? uniqueRate = uniqueTotal == 0
            ? null
            : _uniqueResolvedCodeFrames.Count / (double)uniqueTotal;
        double? metricRate = totalMetric == 0
            ? null
            : _resolvedMetric / (double)totalMetric;
        var topUnresolved = StackSourceTopN.TopByValue(
            _unresolvedByModule, 10, (module, count) => new UnresolvedModule(module, count));

        return new SymbolStats(
            Resolved: _uniqueResolvedCodeFrames.Count,
            Unresolved: _uniqueUnresolvedCodeFrames.Count,
            ResolutionRate: uniqueRate,
            TopUnresolvedModules: topUnresolved,
            UniqueCodeFrameCount: uniqueTotal,
            UniqueResolvedCodeFrameCount: _uniqueResolvedCodeFrames.Count,
            UniqueUnresolvedCodeFrameCount: _uniqueUnresolvedCodeFrames.Count,
            ObservedUniqueCodeFrameNameResolutionRate: uniqueRate,
            TotalCodeFrameMetric: totalMetric,
            ResolvedCodeFrameMetric: _resolvedMetric,
            UnresolvedCodeFrameMetric: _unresolvedMetric,
            ObservedMetricWeightedCodeFrameNameResolutionRate: metricRate,
            ExcludedSyntheticOrPseudoUniqueFrames: _uniqueExcludedFrames.Count,
            ExcludedSyntheticOrPseudoFrameMetric: _excludedMetric,
            MetricName: _metricName,
            LookupState: lookupAttempt.State,
            WarmSymbolThreshold: StackSourceTopN.WarmSymbolThreshold,
            ResolutionEvidence: "post_lookup_frame_name_heuristic",
            LookupFailure: lookupAttempt.Failure,
            MetricAccounting: metricAccounting);
    }
}

// Shared "stack source → CallTree → top-N" pipeline used by both CpuAnalysis (metric=1
// per CPU sample) and BlockedTimeStackAnalysis (metric=blocked μs per CSwitch resume).
//
// Two analyzers ago I would have left this duplicated; the second use-site (BlockedTime)
// was the rule-of-two trigger. Encapsulates the PerfView-parity invariants that are easy
// to violate by accident when rolling a new analyzer:
//
//   1. Symbol resolution stats are computed on RAW frames (BEFORE module!? folding) so
//      the resolved/unresolved tally reflects physical resolution quality rather than the
//      synthetic "module!?" buckets we make up for display.
//   2. Unresolved per-address frames ("module!hex", "module!?+0x10") are collapsed into
//      a per-module "module!?" bucket via a second MutableTraceEventStackSource. Without
//      this, any non-symbolicated DLL fills the top-N with hex offsets and the response
//      is unusable for an LLM.
//   3. If a caller chooses native symbol resolution, it MUST call LookupWarmSymbols on the
//      raw source BEFORE BuildNormalized — once the source is normalized, real symbol names
//      are no longer recoverable. Broad MCP calls default to skipping symbol lookup to avoid
//      remote PDB latency; callers can opt in after narrowing pid/window.
//
// What this helper does NOT do: build the raw source, decide the metric, run the CallTree.
// Those are analyzer-specific. Callers fill rawSource themselves (one sample per CPU sample,
// or one sample per CSwitch resume), then hand it here for stats + normalization.
/// <summary>
/// Common per-call inputs to a stack analyzer's BuildNormalized stage.  The 5-tuple
/// (pid, startUs, endUs, symbolLog, when) was repeated in every analyzer's signature
/// before this record absorbed it.
///
/// `Pid` semantics differ slightly per analyzer: some omit it entirely (InterruptStackAnalysis —
/// kernel context, no per-process attribution), some rename it for the public API
/// (ReadyThreadStackAnalysis exposes it as `awakenedPid` because the metric is "who
/// readied threads in process X").  The record stores the raw nullable int; each analyzer
/// applies the appropriate semantic.
///
/// `When` is a class reference and the bucket array it owns is mutable shared state.
/// Although the record itself is `readonly record struct`, callers WRITE through `When.Add(...)`
/// during the trace walk; the struct's value-equality semantics don't extend to it.
/// </summary>
internal readonly record struct StackAnalysisRequest(
    int? Pid,
    long? StartUs,
    long? EndUs,
    TextWriter SymbolLog,
    StackSourceTopN.WhenHistogram When)
{
    public bool ResolveSymbols { get; init; } = StackResponseOptions.CurrentResolveSymbols;

    public ThreadAnalysisScope? ThreadScope { get; init; }

    public TraceIdentityIndex? IdentityIndex { get; init; }

    public ProcessAnalysisScope? ProcessScope { get; init; }

    public bool? FilterSpecified { get; init; }

    /// <summary>
    /// True iff the caller restricted the analysis with at least one of pid / startUs / endUs.
    /// Gates the PctOfTrace denominator — it only carries meaning when there's a "trace total"
    /// baseline distinct from the filtered subset.
    /// </summary>
    public bool HasFilter => FilterSpecified ??
        (Pid.HasValue || ProcessScope?.ProcessStartUs.HasValue == true ||
         StartUs.HasValue || EndUs.HasValue);

    public static StackAnalysisRequest ForProcess(
        TraceLog trace,
        int? pid,
        long? processStartUs,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        StackSourceTopN.WhenHistogram when,
        bool? filterSpecified = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = new TimeWindow(startUs ?? 0, endUs ?? traceEndUs);
        var identities = TraceIdentityIndex.For(trace);
        var processScope = ProcessAnalysisScope.Resolve(window, pid, processStartUs, identities);
        return new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when)
        {
            IdentityIndex = identities,
            ProcessScope = processScope,
            FilterSpecified = filterSpecified ??
                (pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue),
        };
    }

    /// <summary>
    /// True iff the event with the given process and timestamp passes pid + half-open
    /// window filters: StartUs <= nowUs < EndUs.
    /// Replaces the 3-line `if (req.Pid is …) … if (req.StartUs is …) … if (req.EndUs is …) …`
    /// block that recurs in every typed-event handler.
    /// </summary>
    public bool PassesFilter(int processId, long nowUs) =>
        ProcessScope is not null
            ? ProcessScope.MatchesEvent(
                IdentityIndex ?? throw new InvalidOperationException(
                    "A process scope requires a trace identity index."),
                processId,
                nowUs)
            : (!Pid.HasValue || processId == Pid.Value) &&
              (!StartUs.HasValue || nowUs >= StartUs.Value) &&
              (!EndUs.HasValue || nowUs < EndUs.Value);

    public bool PassesFilter(int processId, int threadId, long nowUs) =>
        ThreadScope.HasValue
            ? ThreadScope.Value.MatchesPoint(processId, threadId, nowUs)
            : PassesFilter(processId, nowUs);

    /// <summary>
    /// Time-only filter — for kernel-context analyzers (DPC/ISR) where per-process attribution
    /// is meaningless and Pid is always null at the call site.
    /// </summary>
    public bool PassesFilter(long nowUs) =>
        (!StartUs.HasValue || nowUs >= StartUs.Value) &&
        (!EndUs.HasValue || nowUs < EndUs.Value);
}

internal readonly record struct StackResultContract(
    ProcessInstanceKey? SelectedProcess,
    string ScopeMode,
    bool PidReuseObserved,
    IReadOnlyList<ProcessInstanceKey> IncludedProcesses,
    string ScopeStatus,
    string CapabilityStatus,
    long MatchedEventCount,
    string? NoDataReason,
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null,
    string? ScopeWarning = null)
{
    public static StackResultContract From(
        ProcessAnalysisScope? processScope,
        bool filterSpecified,
        DomainStackCoverage coverage,
        bool focusRequested = false,
        bool focusFound = true,
        long? traceEventCount = null)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var scopeStatus = processScope?.ScopeStatus ?? ProcessAnalysisScope.ResolvedStatus;
        var scopeMode = processScope?.ScopeMode ?? "all_processes";
        // All-process queries already declare ScopeMode=all_processes. Serializing every
        // lifetime in a large system trace adds thousands of tokens without narrowing the
        // scope; IncludedProcesses is useful only for PID-selected aggregate/exact scopes.
        var included = processScope?.Pid.HasValue == true
            ? processScope.IncludedProcesses
            : [];
        var capabilityStatus = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? "unknown"
            : coverage.TotalEventCount > 0
                ? "observed"
                : traceEventCount switch
                {
                    0 => "not_observed",
                    > 0 => "unknown",
                    _ => filterSpecified ? "unknown" : "not_observed",
                };
        string? noDataReason = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? scopeStatus
            : coverage.TotalEventCount == 0
                ? traceEventCount switch
                {
                    0 => "event_class_not_observed",
                    > 0 => "no_events_in_scope",
                    _ => filterSpecified ? "no_events_in_scope" : "event_class_not_observed",
                }
                : coverage.StackedEventCount == 0
                    ? "stacks_unavailable"
                    : focusRequested && !focusFound
                        ? "focus_not_found"
                        : null;

        return new StackResultContract(
            processScope?.SelectedProcess,
            scopeMode,
            processScope?.PidReuseObserved ?? false,
            included,
            scopeStatus,
            capabilityStatus,
            coverage.TotalEventCount,
            noDataReason);
    }

    public static StackResultContract FromThreadScope(
        ThreadAnalysisScope? threadScope,
        bool filterSpecified,
        DomainStackCoverage coverage,
        long? traceEventCount = null)
    {
        var contract = From(
            processScope: null,
            filterSpecified,
            coverage,
            traceEventCount: traceEventCount);
        if (!threadScope.HasValue)
            return contract;

        var scope = threadScope.Value;
        var selected = scope.Process?.Key ?? scope.Thread?.Key.Process;
        var includedProcesses = scope.IncludedProcesses ??
            (selected.HasValue ? [selected.Value] : []);
        var includedThreads = scope.IncludedThreads ??
            (scope.Thread is null
                ? []
                : [new ThreadScopeCandidate(
                    scope.Thread.Key,
                    scope.Thread.StartUs,
                    scope.Thread.EndUs)]);
        return contract with
        {
            SelectedProcess = selected,
            ScopeMode = scope.ScopeMode,
            PidReuseObserved = scope.PidReuseObserved,
            IncludedProcesses = includedProcesses,
            IncludedThreads = includedThreads,
            ScopeStatus = scope.ScopeStatus,
            CapabilityStatus = scope.IsResolved ? contract.CapabilityStatus : "unknown",
            MatchedEventCount = scope.IsResolved ? contract.MatchedEventCount : 0,
            NoDataReason = scope.NoDataReason ?? contract.NoDataReason,
            ScopeWarning = scope.ScopeWarning,
        };
    }

    public static StackResultContract FromIntervalEndpoints(
        ProcessAnalysisScope? processScope,
        ThreadAnalysisScope? threadScope,
        bool filterSpecified,
        DomainStackCoverage coverage,
        long traceSourceEndpointCount,
        long scopedSourceEndpointCount,
        long scopedIdentityUnresolvedEndpointCount)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (traceSourceEndpointCount < 0)
            throw new ArgumentOutOfRangeException(nameof(traceSourceEndpointCount));
        if (scopedSourceEndpointCount < 0)
            throw new ArgumentOutOfRangeException(nameof(scopedSourceEndpointCount));
        if (scopedIdentityUnresolvedEndpointCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scopedIdentityUnresolvedEndpointCount));
        }

        var contract = threadScope.HasValue
            ? FromThreadScope(
                threadScope,
                filterSpecified,
                coverage,
                traceEventCount: traceSourceEndpointCount)
            : From(
                processScope,
                filterSpecified,
                coverage,
                traceEventCount: traceSourceEndpointCount);
        if (contract.ScopeStatus != ProcessAnalysisScope.ResolvedStatus)
            return contract with { MatchedEventCount = 0 };

        var hasCompletedInterval = coverage.TotalEventCount > 0;
        var capabilityStatus = hasCompletedInterval || scopedSourceEndpointCount > 0
            ? "observed"
            : traceSourceEndpointCount == 0
                ? "not_observed"
                : "unknown";
        string? noDataReason = hasCompletedInterval
            ? coverage.StackedEventCount == 0
                ? "stacks_unavailable"
                : null
            : scopedSourceEndpointCount > 0
                ? "no_completed_intervals_in_scope"
                : traceSourceEndpointCount == 0
                    ? "event_class_not_observed"
                    : scopedIdentityUnresolvedEndpointCount > 0
                        ? "source_events_unattributed"
                        : "no_events_in_scope";

        return contract with
        {
            CapabilityStatus = capabilityStatus,
            MatchedEventCount = scopedSourceEndpointCount,
            NoDataReason = noDataReason,
        };
    }

    public void AddWarning(ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (!string.IsNullOrWhiteSpace(ScopeWarning) && !warnings.Contains(ScopeWarning))
            warnings.Add(ScopeWarning);
        if (NoDataReason is null)
            return;
        var prefix = NoDataReason + ":";
        if (warnings.Any(warning => warning.StartsWith(prefix, StringComparison.Ordinal)))
            return;
        warnings.Add(NoDataReason switch
        {
            "scope_not_found" =>
                "scope_not_found: the requested process lifetime does not intersect the analysis window.",
            "process_instance_not_found" =>
                "process_instance_not_found: the requested process lifetime does not intersect the analysis window.",
            "thread_instance_not_found" =>
                "thread_instance_not_found: the requested thread lifetime does not intersect the analysis window.",
            "ambiguous_process_instance" =>
                ProcessAnalysisScope.ResolutionFailureWarning(
                    ProcessAnalysisScope.AmbiguousStatus),
            "ambiguous_thread_instance" =>
                "ambiguous_thread_instance: multiple thread lifetimes matched; supply processStartUs and threadStartUs from IncludedProcesses and IncludedThreads.",
            "event_class_not_observed" =>
                "event_class_not_observed: no matching event was observed in this unfiltered trace; this does not prove a capture keyword was disabled.",
            "no_events_in_scope" =>
                "no_events_in_scope: the selected process/window matched no events; capture capability remains unknown for this scope.",
            "source_events_unattributed" =>
                "source_events_unattributed: source events with matching raw PID/TID/time were observed, but required process, thread, or CLR instance identity could not be resolved; no scoped attribution was guessed.",
            "no_completed_intervals_in_scope" =>
                "no_completed_intervals_in_scope: one or more scoped interval endpoints were observed, but no valid completed interval was projected into the requested scope.",
            "stacks_unavailable" =>
                "stacks_unavailable: matching events were observed, but none carried an attached stack.",
            "focus_not_found" =>
                "focus_not_found: matching stacked events were observed, but the requested focus frame was absent.",
            _ => $"{NoDataReason}: no data was produced.",
        });
    }
}

internal static class StackSourceTopN
{
    // PerfView's threshold for "warm" symbol resolution: modules with ≥50 inclusive samples
    // get their PDBs fetched. Below that, a symbol-server round trip per cold module would
    // dominate analysis time on traces with hundreds of seldom-touched DLLs.
    public const int WarmSymbolThreshold = 50;

    // ETW self-overhead frame patterns. Borrowed from PerfView's default GroupPats: any
    // frame whose symbol matches these is the kernel synthesizing the very stack we're
    // analyzing — counting it inflates "ntoskrnl" / "ntdll" inclusive % by 5-30% depending
    // on stackwalk frequency. PerfView's "Just My App" preset folds them all into one
    // bucket; we mirror that.
    private static readonly string[] EtwOverheadSymbolFragments =
    {
        "EtwpLogKernelEvent",
        "EtwpTraceStackWalk",
        "EtwTraceStackWalk",
        "RtlpWalkFrameChain",
    };

    /// <summary>
    /// Bundle of "raw stack source + synthetic ?!? root + reusable sample container" that
    /// every stack-source analyzer needs in identical form. Returning all three together
    /// keeps the PerfView-parity invariant (no-stack samples → ?!? root) in one place
    /// instead of being re-implemented in every new analyzer.
    /// </summary>
    public readonly record struct RawStackSource(
        MutableTraceEventStackSource Source,
        StackSourceCallStackIndex NoStackCallStack,
        StackSourceSample Sample,
        DomainStackCoverageAccumulator Coverage,
        List<long> ExactSampleMetrics)
    {
        /// <summary>
        /// Push one sample at the resolved call stack (falling back to the synthetic ?!? root
        /// when the event has no stack walk attached). Centralised so PerfView-parity invariant
        /// #1 (no-stack samples → ?!? root) lives in one place across all stack analyzers.
        /// </summary>
        public void AddSample(CallStackIndex csIdx, TraceEvent ev, long metric)
        {
            var hasStack = csIdx != CallStackIndex.Invalid;
            var stackIndex = !hasStack
                ? NoStackCallStack
                : Source.GetCallStack(csIdx, ev);
            AddSample(stackIndex, hasStack, ev.TimeStampRelativeMSec, metric);
        }

        /// <summary>
        /// Push a sample whose TraceEvent stack was resolved earlier. Interval analyzers use
        /// this after pairing start/stop events, while retaining the same exact-long coverage
        /// accounting and synthetic-unknown contract as direct event analyzers.
        /// </summary>
        public void AddSample(
            StackSourceCallStackIndex stackIndex,
            bool hasStack,
            double timeRelativeMSec,
            long metric)
        {
            Coverage.Observe(hasStack, metric);
            Sample.StackIndex = hasStack ? stackIndex : NoStackCallStack;
            Sample.TimeRelativeMSec = timeRelativeMSec;
            Sample.Metric = (float)metric;
            Source.AddSample(Sample);
            ExactSampleMetrics.Add(metric);
        }
    }

    public static RawStackSource CreateRawSource(
        TraceLog trace,
        string domain = "unknown",
        string metricName = "count",
        string stackSemantics = "event_call_stack")
    {
        var src = new MutableTraceEventStackSource(trace) { ShowUnknownAddresses = true };
        var noStackFrame = src.Interner.FrameIntern("?!?");
        var noStack = src.Interner.CallStackIntern(noStackFrame, StackSourceCallStackIndex.Invalid);
        return new RawStackSource(
            src,
            noStack,
            new StackSourceSample(src),
            new DomainStackCoverageAccumulator(domain, metricName, stackSemantics),
            new List<long>());
    }

    public static void AddCoverageWarning(
        ICollection<string> warnings,
        DomainStackCoverage coverage)
    {
        if (coverage.CoverageState == "no_stacks")
        {
            warnings.Add(
                $"stack_coverage_state=no_stacks: none of the {coverage.TotalEventCount:N0} " +
                $"{coverage.Domain} event(s) had an attached stack; ?!? is synthetic unknown evidence, not a captured call chain.");
        }
        else if (coverage.CoverageState == "partial")
        {
            warnings.Add(
                $"stack_coverage_state=partial: {coverage.StackedEventCount:N0} of " +
                $"{coverage.TotalEventCount:N0} {coverage.Domain} event(s) had an attached stack " +
                $"({coverage.StackCoveragePct:F2}%); ?!? represents the unstacked remainder.");
        }
    }

    /// <summary>
    /// PerfView-parity ratio formula for "exclusive/inclusive % of trace": null when no
    /// filter is in effect (caller's "% of filtered" already covers that case) or when the
    /// total is zero (avoids division-by-zero on traces without the relevant event keyword).
    /// Same formula appears 4× per analyzer × 5 analyzers; centralising stops it from
    /// drifting between sites.
    /// </summary>
    public static double? PctOfTrace(bool hasFilter, double traceTotal, double n)
        => hasFilter && traceTotal > 0 ? Pct(traceTotal, n) : (double?)null;

    public static double Pct(double total, double n)
    {
        if (total <= 0 || n <= 0)
            return 0;

        var pct = 100.0 * n / total;
        if (double.IsNaN(pct) || double.IsInfinity(pct))
            return 0;
        Debug.Assert(pct <= 100.5, $"Unexpected percentage overshoot: total={total}, n={n}, pct={pct}");
        return Math.Clamp(pct, 0, 100);
    }

    /// <summary>
    /// Caller/callee drill-down: scan every sample in <paramref name="normalized"/>, locate
    /// stacks containing <paramref name="focusFunction"/>, and aggregate the immediate caller
    /// (frame one step toward root) and callee (frame one step toward leaf) by name.
    ///
    /// Stack-source-agnostic — works on any MutableTraceEventStackSource regardless of the
    /// metric kind (CPU samples, blocked μs, hard-fault bytes, file-IO bytes, image-load
    /// counts). Callers pass their metric name for the response field so consumers know the
    /// units.
    ///
    /// Recursion handling: if focusFunction appears multiple times in a single stack (e.g.,
    /// recursive function), only the LEAF-MOST occurrence is counted. This matches PerfView's
    /// default caller/callee semantics and avoids double-counting.
    /// </summary>
    public static CallerCalleeResponse ComputeCallerCallee(
        MutableTraceEventStackSource normalized,
        string focusFunction,
        int top,
        string metricName,
        SymbolStats stats,
        IList<string> baseWarnings,
        long? sourceTotalMetric = null,
        int unmatchedIntervalCount = 0,
        ProcessInstanceKey? selectedProcess = null,
        ThreadInstanceKey? selectedThread = null,
        bool hasContextSwitches = false,
        bool hasContextSwitchBlockingStacks = false,
        bool hasSampledProfileStacks = false,
        string symbolResolutionState = "not_applicable",
        DomainStackCoverage? stackCoverage = null,
        StackResultContract? resultContract = null)
    {
        long focusExclusive = 0;
        long focusInclusive = 0;
        long totalMetric = 0;
        var focusFound = false;
        var callers = new Dictionary<string, (long excl, long incl)>();
        var callees = new Dictionary<string, (long excl, long incl)>();

        for (var s = 0; s < normalized.SampleIndexLimit; s++)
        {
            var sample = normalized.GetSampleByIndex((StackSourceSampleIndex)s);
            var metric = (long)sample.Metric;
            totalMetric += metric;

            var walk = sample.StackIndex;
            string? childOfFocus = null;
            while (walk != StackSourceCallStackIndex.Invalid)
            {
                var frameIdx = normalized.GetFrameIndex(walk);
                var name = normalized.GetFrameName(frameIdx, fullModulePath: false);

                if (name == focusFunction)
                {
                    focusFound = true;
                    focusInclusive += metric;

                    // Caller = frame one step toward root. "<root>" when focus has no caller
                    // (e.g., focus IS the entry point or stack walk truncated).
                    var callerWalk = normalized.GetCallerIndex(walk);
                    var callerName = callerWalk == StackSourceCallStackIndex.Invalid
                        ? "<root>"
                        : normalized.GetFrameName(normalized.GetFrameIndex(callerWalk), fullModulePath: false);
                    AccumulateNeighbor(callers, callerName, metric);

                    // Callee = frame one step toward leaf — the previous iteration's frame.
                    // null when focus is the leaf itself (sample ENDED at focus); record as
                    // "<self>" so the row appears in callees with metric attributed there.
                    var calleeName = childOfFocus ?? "<self>";
                    AccumulateNeighbor(callees, calleeName, metric);

                    // Exclusive: focus is the leaf. childOfFocus is null only on the first
                    // iteration before we've passed through any frame.
                    if (childOfFocus is null) focusExclusive += metric;

                    // Stop after leaf-most match (recursion-safe).
                    break;
                }

                childOfFocus = name;
                walk = normalized.GetCallerIndex(walk);
            }
        }

        var totalDouble = totalMetric;
        CallerCalleeNode Project(KeyValuePair<string, (long excl, long incl)> kv)
            => new(kv.Key, kv.Value.excl, kv.Value.incl,
                   Pct(totalDouble, kv.Value.excl),
                   Pct(totalDouble, kv.Value.incl));

        var topCallers = callers.OrderByDescending(kv => kv.Value.incl).Take(top).Select(Project).ToList();
        var topCallees = callees.OrderByDescending(kv => kv.Value.incl).Take(top).Select(Project).ToList();

        var warnings = new List<string>(baseWarnings);
        var contract = resultContract ?? (stackCoverage is null
            ? new StackResultContract(
                selectedProcess,
                selectedProcess.HasValue ? "single_process" : "all_processes",
                PidReuseObserved: false,
                IncludedProcesses: selectedProcess.HasValue ? [selectedProcess.Value] : [],
                ScopeStatus: "ok",
                CapabilityStatus: totalMetric > 0 ? "observed" : "unknown",
                MatchedEventCount: 0,
                NoDataReason: null)
            : StackResultContract.From(
                processScope: null,
                filterSpecified: false,
                stackCoverage));
        if (!focusFound && contract.NoDataReason is null)
            contract = contract with { NoDataReason = "focus_not_found" };
        if (contract.NoDataReason == "focus_not_found")
        {
            warnings.Add(
                $"Focus function '{focusFunction}' not found in the analyzed stack samples. " +
                "Frame names are case-sensitive — copy verbatim from cpu_top_functions / " +
                "wait_top_stacks / etc. output. Unresolved frames are stored as 'module!?'.");
        }
        contract.AddWarning(warnings);

        var metricAccounting = StackMetricAccounting.ForMetric(
            stackCoverage?.MetricName ?? metricName);
        return new CallerCalleeResponse(
            FocusFunction: focusFunction,
            FocusExclusiveMetric: focusExclusive,
            FocusInclusiveMetric: focusInclusive,
            FocusExclusivePct: Pct(totalDouble, focusExclusive),
            FocusInclusivePct: Pct(totalDouble, focusInclusive),
            MetricName: metricName,
            Callers: topCallers,
            Callees: topCallees,
            Stats: stats,
            Warnings: warnings,
            SourceTotalMetric: sourceTotalMetric ?? totalMetric,
            UnmatchedIntervalCount: unmatchedIntervalCount,
            SelectedProcess: selectedProcess ?? contract.SelectedProcess,
            SelectedThread: selectedThread,
            HasContextSwitches: hasContextSwitches,
            HasContextSwitchBlockingStacks: hasContextSwitchBlockingStacks,
            HasSampledProfileStacks: hasSampledProfileStacks,
            SymbolResolutionState: symbolResolutionState,
            StackCoverage: stackCoverage,
            MetricPrecision: metricAccounting,
            RowMetricAccounting: metricAccounting,
            ExactTotalAccounting: StackMetricAccounting.ExactLong,
            ScopeMode: contract.ScopeMode,
            PidReuseObserved: contract.PidReuseObserved,
            IncludedProcesses: contract.IncludedProcesses,
            ScopeStatus: contract.ScopeStatus,
            CapabilityStatus: contract.CapabilityStatus,
            MatchedEventCount: contract.MatchedEventCount,
            NoDataReason: contract.NoDataReason,
            IncludedThreads: contract.IncludedThreads);
    }

    public static string GetSymbolResolutionState(
        bool resolveSymbols,
        SymbolStats stats,
        bool hasSourceStacks)
    {
        if (!hasSourceStacks)
            return "no_stacks";
        if (stats.LookupState == "failed")
            return "failed";
        if (!resolveSymbols)
            return "skipped";
        if (stats.UniqueCodeFrameCount == 0)
            return "no_code_frames";
        if (stats.Unresolved == 0)
            return "resolved";
        return stats.Resolved == 0 ? "unresolved" : "partial";
    }

    private static void AccumulateNeighbor(
        Dictionary<string, (long excl, long incl)> map, string name, long metric)
    {
        // For caller/callee neighbors of a focus frame, "exclusive" and "inclusive" carry the
        // same value: it's the metric flowing through this single edge to/from focus. PerfView
        // shows both columns for symmetry with the main top-N view; we keep the convention.
        var prev = map.GetValueOrDefault(name);
        map[name] = (prev.excl + metric, prev.incl + metric);
    }

    /// <summary>
    /// Constructs a SymbolReader from a configured-path snapshot. The TraceLog overload also
    /// adds the original ETL directory to that reader only, without mutating _NT_SYMBOL_PATH.
    /// </summary>
    public static SymbolReader OpenSymbolReader(TextWriter symbolLog)
        => new(symbolLog, SymbolPathState.CurrentPath);

    public static SymbolReader OpenSymbolReader(TraceLog trace, TextWriter symbolLog)
        => new(symbolLog, TraceSymbolContext.GetEffectivePath(trace));

    public static SymbolLookupAttempt TryLookupWarmSymbols(
        MutableTraceEventStackSource source,
        bool resolveSymbols,
        SymbolReader symbolReader,
        Action<MutableTraceEventStackSource, int, SymbolReader>? lookup = null)
    {
        if (!resolveSymbols)
            return SymbolLookupAttempt.Skipped();

        try
        {
            if (lookup is null)
                source.LookupWarmSymbols(WarmSymbolThreshold, symbolReader);
            else
                lookup(source, WarmSymbolThreshold, symbolReader);
            return SymbolLookupAttempt.Executed();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return SymbolLookupAttempt.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void AddSymbolLookupWarning(
        ICollection<string> warnings,
        SymbolStats stats)
    {
        if (stats.LookupState == "failed")
        {
            warnings.Add(
                $"symbol_lookup_state=failed: warm-symbol lookup failed; observed frame-name rates may reflect a partial attempt. {stats.LookupFailure}");
        }
    }

    /// <summary>
    /// Fixed-bucket time histogram builder. Caller passes a window (filter window or full
    /// trace duration if no filter) and bucket count; analyzer calls <see cref="Add"/> on
    /// every recorded sample, then <see cref="Build"/> to produce the wire-format response
    /// or null when the caller requested zero buckets.
    /// </summary>
    public sealed class WhenHistogram
    {
        private readonly long _startUs;
        private readonly long _endUs;
        private readonly long _bucketWidthUs;
        private readonly long[]? _buckets;

        private WhenHistogram(long startUs, long endUs, long bucketWidthUs, long[]? buckets)
        {
            _startUs = startUs;
            _endUs = endUs;
            _bucketWidthUs = bucketWidthUs;
            _buckets = buckets;
        }

        public static WhenHistogram ForWindow(long? startUs, long? endUs, TraceLog trace, int bucketCount)
        {
            var window = Validation.RequireWindowInput(startUs, endUs).Resolve(
                TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds),
                maxDurationUs: null);
            return ForWindow(window, bucketCount);
        }

        public static WhenHistogram ForWindow(TimeWindow window, int bucketCount)
        {
            if (bucketCount <= 0)
                return new WhenHistogram(window.StartUs, window.EndUs, 0, null);

            var width = checked((window.DurationUs + bucketCount - 1) / bucketCount);
            return new WhenHistogram(
                window.StartUs, window.EndUs, width, new long[bucketCount]);
        }

        public void AddPoint(long nowUs, long metric)
        {
            if (_buckets is null) return;
            if (nowUs < _startUs || nowUs >= _endUs) return;
            var bucket = (int)((nowUs - _startUs) / _bucketWidthUs);
            if ((uint)bucket < (uint)_buckets.Length)
                _buckets[bucket] = checked(_buckets[bucket] + metric);
        }

        public void Add(long nowUs, long metric) => AddPoint(nowUs, metric);

        public void AddDurationInterval(long intervalStartUs, long intervalEndUs)
        {
            if (_buckets is null || intervalEndUs <= intervalStartUs)
                return;

            var clippedStartUs = TimeWindow.ClipStart(intervalStartUs, _startUs);
            var clippedEndUs = TimeWindow.ClipEnd(intervalEndUs, _endUs);
            if (clippedEndUs <= clippedStartUs)
                return;

            var firstBucket = (int)((clippedStartUs - _startUs) / _bucketWidthUs);
            var lastBucket = (int)((clippedEndUs - _startUs - 1) / _bucketWidthUs);
            if (lastBucket >= _buckets.Length)
                lastBucket = _buckets.Length - 1;

            for (var bucket = firstBucket; bucket <= lastBucket; bucket++)
            {
                var bucketStartUs = checked(_startUs + bucket * _bucketWidthUs);
                var bucketEndUs = checked(bucketStartUs + _bucketWidthUs);
                bucketEndUs = TimeWindow.ClipEnd(bucketEndUs, _endUs);

                var overlapUs = new TimeWindow(bucketStartUs, bucketEndUs)
                    .IntersectDurationUs(clippedStartUs, clippedEndUs);
                _buckets[bucket] = checked(_buckets[bucket] + overlapUs);
            }
        }

        public TimeHistogram? Build()
            => _buckets is null
                ? null
                : new TimeHistogram(_startUs, _endUs, _bucketWidthUs, _buckets);
    }

    /// <summary>
    /// Walks every frame in <paramref name="rawSource"/> classifying each as resolved or
    /// unresolved (per PerfView conventions: contains '?', empty symbol, or starts with
    /// "0x"). Returns the count totals + per-module unresolved breakdown for callers to
    /// surface in their response. When symbol lookup is enabled, call this AFTER
    /// <c>LookupWarmSymbols</c> so resolution had its chance — and BEFORE the source is
    /// normalized.
    /// </summary>
    public static SymbolStats ComputeSymbolStats(
        RawStackSource raw,
        SymbolLookupAttempt lookupAttempt)
        => ComputeSymbolStats(
            raw.Source,
            raw.ExactSampleMetrics,
            raw.Coverage.MetricName,
            lookupAttempt);

    public static SymbolStats ComputeSymbolStats(MutableTraceEventStackSource rawSource)
        => ComputeSymbolStats(
            rawSource,
            exactSampleMetrics: null,
            metricName: "count",
            SymbolLookupAttempt.Unknown());

    private static SymbolStats ComputeSymbolStats(
        MutableTraceEventStackSource rawSource,
        IReadOnlyList<long>? exactSampleMetrics,
        string metricName,
        SymbolLookupAttempt lookupAttempt)
    {
        var hasExactMetrics = exactSampleMetrics?.Count == (int)rawSource.SampleIndexLimit;
        var accumulator = new SymbolFrameMetricAccumulator(metricName);

        for (var sampleIndex = 0; sampleIndex < (int)rawSource.SampleIndexLimit; sampleIndex++)
        {
            var sample = rawSource.GetSampleByIndex((StackSourceSampleIndex)sampleIndex);
            var metric = hasExactMetrics
                ? exactSampleMetrics![sampleIndex]
                : checked((long)sample.Metric);
            var stackIndex = sample.StackIndex;
            while (stackIndex != StackSourceCallStackIndex.Invalid)
            {
                var frameIndex = rawSource.GetFrameIndex(stackIndex);
                var frameIdentity = (int)frameIndex;
                if (rawSource.GetFrameCodeAddress(frameIndex) == CodeAddressIndex.Invalid)
                {
                    accumulator.ObserveExcludedFrame(frameIdentity, metric);
                }
                else
                {
                    var frameName = rawSource.GetFrameName(frameIndex, fullModulePath: false);
                    var (resolved, module) = ClassifyFrameName(frameName);
                    accumulator.ObserveCodeFrame(frameIdentity, module, resolved, metric);
                }
                stackIndex = rawSource.GetCallerIndex(stackIndex);
            }
        }

        return accumulator.Snapshot(
            lookupAttempt,
            hasExactMetrics ? "exact_long" : "float_fallback");
    }

    private static (bool Resolved, string Module) ClassifyFrameName(string frameName)
    {
        var bang = frameName.IndexOf('!');
        var symbolPart = bang >= 0 ? frameName[(bang + 1)..] : frameName;
        var module = bang > 0 ? frameName[..bang] : "<unknown>";
        var unresolved =
            symbolPart.Length == 0 ||
            symbolPart.Contains('?') ||
            symbolPart.StartsWith("0x", StringComparison.Ordinal);
        return (!unresolved, module);
    }

    /// <summary>
    /// Take the N highest-value entries from a counter dictionary and project each into a row
    /// type.  Replaces the four-call-site `OrderByDescending(kv => kv.Value).Take(n).Select(kv =>
    /// new TRow(kv.Key, kv.Value)).ToList()` shape — used for things like top exception types,
    /// top allocated types, top unresolved modules, top marker-event names.
    /// </summary>
    public static List<TRow> TopByValue<TKey, TRow>(
        IDictionary<TKey, long> source, int n, Func<TKey, long, TRow> project) where TKey : notnull =>
        source.OrderByDescending(kv => kv.Value).Take(n).Select(kv => project(kv.Key, kv.Value)).ToList();

    /// <summary>
    /// Builds a second <see cref="MutableTraceEventStackSource"/> that mirrors
    /// <paramref name="rawSource"/> sample-for-sample but with stack frames normalized:
    /// unresolved per-address frames are collapsed into per-module "module!?" buckets,
    /// and (when <paramref name="excludeEtwSelfOverhead"/> is true) ETW-overhead frames
    /// fold into a single "[ETW Overhead]!?" bucket. Sample metrics and timestamps are
    /// preserved verbatim.
    /// </summary>
    public static MutableTraceEventStackSource BuildNormalized(
        MutableTraceEventStackSource rawSource,
        TraceLog trace,
        bool excludeEtwSelfOverhead)
    {
        var normalized = new MutableTraceEventStackSource(trace) { ShowUnknownAddresses = true };
        var sample = new StackSourceSample(normalized);
        // Per-stack and per-frame caches keep cost O(unique frames) rather than O(all frames
        // across all samples). On large traces this is the difference between seconds and
        // minutes.
        var stackCache = new Dictionary<StackSourceCallStackIndex, StackSourceCallStackIndex>();
        var frameCache = new Dictionary<StackSourceFrameIndex, StackSourceFrameIndex>();

        for (var s = 0; s < rawSource.SampleIndexLimit; s++)
        {
            var src = rawSource.GetSampleByIndex((StackSourceSampleIndex)s);
            sample.StackIndex = NormalizeStack(rawSource, normalized, src.StackIndex,
                stackCache, frameCache, excludeEtwSelfOverhead);
            sample.TimeRelativeMSec = src.TimeRelativeMSec;
            sample.Metric = src.Metric;
            normalized.AddSample(sample);
        }
        normalized.DoneAddingSamples();
        return normalized;
    }

    private static StackSourceCallStackIndex NormalizeStack(
        MutableTraceEventStackSource src,
        MutableTraceEventStackSource dst,
        StackSourceCallStackIndex orig,
        Dictionary<StackSourceCallStackIndex, StackSourceCallStackIndex> stackCache,
        Dictionary<StackSourceFrameIndex, StackSourceFrameIndex> frameCache,
        bool excludeEtwSelfOverhead)
    {
        if (orig == StackSourceCallStackIndex.Invalid) return StackSourceCallStackIndex.Invalid;
        if (stackCache.TryGetValue(orig, out var cached)) return cached;

        var callerIdx = NormalizeStack(src, dst, src.GetCallerIndex(orig), stackCache, frameCache,
            excludeEtwSelfOverhead);
        var srcFrameIdx = src.GetFrameIndex(orig);
        if (!frameCache.TryGetValue(srcFrameIdx, out var dstFrameIdx))
        {
            var name = src.GetFrameName(srcFrameIdx, fullModulePath: false);
            var normalizedName = NormalizeName(name, excludeEtwSelfOverhead);
            dstFrameIdx = dst.Interner.FrameIntern(normalizedName);
            frameCache[srcFrameIdx] = dstFrameIdx;
        }
        var result = dst.Interner.CallStackIntern(dstFrameIdx, callerIdx);
        stackCache[orig] = result;
        return result;
    }

    /// <summary>
    /// Convert "module!hex" or "module!?something" into "module!?". Resolved symbol names
    /// (e.g. "module!MyClass::Method+0x10") pass through unchanged. The synthetic "?!?" root
    /// (its symbol part is the literal "?") also passes through unchanged.
    /// </summary>
    private static string NormalizeName(string name, bool excludeEtwSelfOverhead)
    {
        if (excludeEtwSelfOverhead)
        {
            // Substring rather than equality: PerfView's resolver sometimes appends "+0x10"
            // offsets, and TraceEvent on certain Windows builds spells them as
            // "EtwpLogKernelEvent_0".
            foreach (var frag in EtwOverheadSymbolFragments)
            {
                if (name.Contains(frag, StringComparison.Ordinal))
                    return "[ETW Overhead]!?";
            }
        }

        var bang = name.IndexOf('!');
        if (bang < 0) return name;
        var symPart = name.AsSpan(bang + 1);
        if (symPart.Length == 0) return name;
        if (symPart.Length == 1 && symPart[0] == '?') return name;
        if (symPart.StartsWith("0x", StringComparison.Ordinal))
            return string.Concat(name.AsSpan(0, bang), "!?");
        if (symPart.IndexOf('?') >= 0)
            return string.Concat(name.AsSpan(0, bang), "!?");
        return name;
    }
}
