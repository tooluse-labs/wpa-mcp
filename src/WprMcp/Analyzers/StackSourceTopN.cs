using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;
// Disambiguate: TraceEvent's stacks namespace also exports a `CallerCalleeNode` type, but
// our DTO is named the same. Use the WprMcp DTO unconditionally inside this file.
using CallerCalleeNode = WprMcp.Output.CallerCalleeNode;

namespace WprMcp.Analyzers;

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
//   3. Caller MUST call LookupWarmSymbols on the raw source BEFORE BuildNormalized — once
//      the source is normalized, real symbol names are no longer recoverable.
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
    /// <summary>
    /// True iff the caller restricted the analysis with at least one of pid / startUs / endUs.
    /// Gates the PctOfTrace denominator — it only carries meaning when there's a "trace total"
    /// baseline distinct from the filtered subset.
    /// </summary>
    public bool HasFilter => Pid.HasValue || StartUs.HasValue || EndUs.HasValue;

    /// <summary>
    /// True iff the event with the given process and timestamp passes pid + window filters.
    /// Replaces the 3-line `if (req.Pid is …) … if (req.StartUs is …) … if (req.EndUs is …) …`
    /// block that recurs in every typed-event handler.
    /// </summary>
    public bool PassesFilter(int processId, long nowUs) =>
        (!Pid.HasValue || processId == Pid.Value) &&
        (!StartUs.HasValue || nowUs >= StartUs.Value) &&
        (!EndUs.HasValue || nowUs <= EndUs.Value);

    /// <summary>
    /// Time-only filter — for kernel-context analyzers (DPC/ISR) where per-process attribution
    /// is meaningless and Pid is always null at the call site.
    /// </summary>
    public bool PassesFilter(long nowUs) =>
        (!StartUs.HasValue || nowUs >= StartUs.Value) &&
        (!EndUs.HasValue || nowUs <= EndUs.Value);
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
        StackSourceSample Sample)
    {
        /// <summary>
        /// Push one sample at the resolved call stack (falling back to the synthetic ?!? root
        /// when the event has no stack walk attached). Centralised so PerfView-parity invariant
        /// #1 (no-stack samples → ?!? root) lives in one place across all stack analyzers.
        /// </summary>
        public void AddSample(CallStackIndex csIdx, TraceEvent ev, double metric)
        {
            Sample.StackIndex = csIdx == CallStackIndex.Invalid
                ? NoStackCallStack
                : Source.GetCallStack(csIdx, ev);
            Sample.TimeRelativeMSec = ev.TimeStampRelativeMSec;
            Sample.Metric = (float)metric;
            Source.AddSample(Sample);
        }
    }

    public static RawStackSource CreateRawSource(TraceLog trace)
    {
        var src = new MutableTraceEventStackSource(trace) { ShowUnknownAddresses = true };
        var noStackFrame = src.Interner.FrameIntern("?!?");
        var noStack = src.Interner.CallStackIntern(noStackFrame, StackSourceCallStackIndex.Invalid);
        return new RawStackSource(src, noStack, new StackSourceSample(src));
    }

    /// <summary>
    /// PerfView-parity ratio formula for "exclusive/inclusive % of trace": null when no
    /// filter is in effect (caller's "% of filtered" already covers that case) or when the
    /// total is zero (avoids division-by-zero on traces without the relevant event keyword).
    /// Same formula appears 4× per analyzer × 5 analyzers; centralising stops it from
    /// drifting between sites.
    /// </summary>
    public static double? PctOfTrace(bool hasFilter, double traceTotal, double n)
        => hasFilter && traceTotal > 0 ? 100.0 * n / traceTotal : (double?)null;

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
        IList<string> baseWarnings)
    {
        long focusExclusive = 0;
        long focusInclusive = 0;
        long totalMetric = 0;
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

        var totalDouble = Math.Max(1.0, totalMetric);
        CallerCalleeNode Project(KeyValuePair<string, (long excl, long incl)> kv)
            => new(kv.Key, kv.Value.excl, kv.Value.incl,
                   100.0 * kv.Value.excl / totalDouble,
                   100.0 * kv.Value.incl / totalDouble);

        var topCallers = callers.OrderByDescending(kv => kv.Value.incl).Take(top).Select(Project).ToList();
        var topCallees = callees.OrderByDescending(kv => kv.Value.incl).Take(top).Select(Project).ToList();

        var warnings = new List<string>(baseWarnings);
        if (focusInclusive == 0)
        {
            warnings.Add(
                $"Focus function '{focusFunction}' not found in the analyzed stack samples. " +
                "Frame names are case-sensitive — copy verbatim from cpu_top_functions / " +
                "wait_top_stacks / etc. output. Unresolved frames are stored as 'module!?'.");
        }

        return new CallerCalleeResponse(
            FocusFunction: focusFunction,
            FocusExclusiveMetric: focusExclusive,
            FocusInclusiveMetric: focusInclusive,
            FocusExclusivePct: 100.0 * focusExclusive / totalDouble,
            FocusInclusivePct: 100.0 * focusInclusive / totalDouble,
            MetricName: metricName,
            Callers: topCallers,
            Callees: topCallees,
            Stats: stats,
            Warnings: warnings);
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
    /// Constructs a SymbolReader pointed at the process-wide _NT_SYMBOL_PATH. Centralised
    /// here so analyzers don't re-read the env var by hand and so a future SymbolService
    /// migration only changes one site.
    /// </summary>
    public static SymbolReader OpenSymbolReader(TextWriter symbolLog)
        => new(symbolLog, Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));

    /// <summary>
    /// Fixed-bucket time histogram builder. Caller passes a window (filter window or full
    /// trace duration if no filter) and bucket count; analyzer calls <see cref="Add"/> on
    /// every recorded sample, then <see cref="Build"/> to produce the wire-format response
    /// or null when the caller requested zero buckets.
    /// </summary>
    public sealed class WhenHistogram
    {
        private readonly long _startUs;
        private readonly long _bucketWidthUs;
        private readonly long[]? _buckets;

        private WhenHistogram(long startUs, long bucketWidthUs, long[]? buckets)
        {
            _startUs = startUs;
            _bucketWidthUs = bucketWidthUs;
            _buckets = buckets;
        }

        public static WhenHistogram ForWindow(long? startUs, long? endUs, TraceLog trace, int bucketCount)
        {
            var winStart = startUs ?? 0;
            var winEnd = endUs ?? (long)trace.SessionDuration.TotalMicroseconds;
            if (bucketCount <= 0 || winEnd <= winStart)
                return new WhenHistogram(winStart, 0, null);
            var width = Math.Max(1, (winEnd - winStart) / bucketCount);
            return new WhenHistogram(winStart, width, new long[bucketCount]);
        }

        public void Add(long nowUs, long metric)
        {
            if (_buckets is null) return;
            var bucket = (int)((nowUs - _startUs) / _bucketWidthUs);
            if ((uint)bucket < (uint)_buckets.Length) _buckets[bucket] += metric;
        }

        public TimeHistogram? Build()
            => _buckets is null ? null : new TimeHistogram(_startUs, _bucketWidthUs, _buckets);
    }

    /// <summary>
    /// Walks every frame in <paramref name="rawSource"/> classifying each as resolved or
    /// unresolved (per PerfView conventions: contains '?', empty symbol, or starts with
    /// "0x"). Returns the count totals + per-module unresolved breakdown for callers to
    /// surface in their response. MUST be called AFTER <c>LookupWarmSymbols</c> so symbol
    /// resolution had its chance — and BEFORE the source is normalized.
    /// </summary>
    public static SymbolStats ComputeSymbolStats(MutableTraceEventStackSource rawSource)
    {
        long resolvedFrames = 0, unresolvedFrames = 0;
        var unresolvedByModule = new Dictionary<string, long>();
        for (var i = 0; i < (int)rawSource.CallFrameIndexLimit; i++)
        {
            var frameName = rawSource.GetFrameName((StackSourceFrameIndex)i, fullModulePath: false);
            // Resolved frames look like "module!Symbol" or "module!Symbol+0x..".
            // Unresolved frames contain '?' or start with raw '0x' addresses.
            var bang = frameName.IndexOf('!');
            var symbolPart = bang >= 0 ? frameName[(bang + 1)..] : frameName;
            var module = bang > 0 ? frameName[..bang] : "<unknown>";
            var unresolved =
                symbolPart.Length == 0 ||
                symbolPart.Contains('?') ||
                symbolPart.StartsWith("0x", StringComparison.Ordinal);
            if (unresolved)
            {
                unresolvedFrames++;
                unresolvedByModule[module] = unresolvedByModule.GetValueOrDefault(module) + 1;
            }
            else
            {
                resolvedFrames++;
            }
        }
        var totalFrames = resolvedFrames + unresolvedFrames;
        var resolutionRate = totalFrames == 0 ? 1.0 : (double)resolvedFrames / totalFrames;

        var topUnresolved = TopByValue(unresolvedByModule, 10, (k, v) => new UnresolvedModule(k, v));
        return new SymbolStats(resolvedFrames, unresolvedFrames, resolutionRate, topUnresolved);
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
