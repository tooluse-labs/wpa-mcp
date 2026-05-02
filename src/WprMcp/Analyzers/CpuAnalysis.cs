using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

public static class CpuAnalysis
{
    // ETW self-overhead frame patterns. Borrowed from PerfView's default GroupPats:
    // any frame whose symbol matches these is the kernel synthesizing the very stack
    // we're analyzing — counting it inflates "ntoskrnl" / "ntdll" inclusive % by 5-30%
    // depending on stackwalk frequency. PerfView's "Just My App" preset folds all of
    // them into one bucket; we mirror that.
    private static readonly string[] EtwOverheadSymbolFragments = new[]
    {
        "EtwpLogKernelEvent",
        "EtwpTraceStackWalk",
        "EtwTraceStackWalk",
        "RtlpWalkFrameChain",
    };

    public static CpuTopFunctionsResponse TopFunctions(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        bool excludeEtwSelfOverhead = false)
    {
        // 1. Filter to CPU sample events (SampledProfileTraceData) with optional pid/time filters.
        // Also count the unfiltered total so we can report ExclusivePctOfTrace alongside the
        // ExclusivePct (which is normalized over the filtered subset). When no pid filter is
        // applied, the two would be identical — leave OfTrace null in that case.
        var hasFilter = pid.HasValue || startUs.HasValue || endUs.HasValue;
        long traceTotalSamples = 0;
        if (hasFilter)
        {
            foreach (var e in trace.Events)
                if (e is SampledProfileTraceData) traceTotalSamples++;
        }

        var sampleEvents = trace.Events.Filter(e =>
        {
            if (e is not SampledProfileTraceData) return false;
            if (pid is { } p && e.ProcessID != p) return false;
            var usSinceStart = (long)(e.TimeStampRelativeMSec * 1000);
            if (startUs is { } s && usSinceStart < s) return false;
            if (endUs is { } eUs && usSinceStart > eUs) return false;
            return true;
        });

        // 2. Build the mutable stack source by adding one sample per CPU sample event.
        //    PerfView attributes samples with no callstack to a synthetic "?!?" root so the grand
        //    total matches raw event count. Mirror that behaviour here so per-row sample counts
        //    align with PerfView's SaveCPUStacksAsCsv output.
        using var symbolReader = new SymbolReader(
            symbolLog,
            Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
        var rawSource = new MutableTraceEventStackSource(trace) { ShowUnknownAddresses = true };
        var noStackFrame = rawSource.Interner.FrameIntern("?!?");
        var noStackCallStack = rawSource.Interner.CallStackIntern(noStackFrame, StackSourceCallStackIndex.Invalid);
        var sample = new StackSourceSample(rawSource);
        foreach (var ev in sampleEvents)
        {
            var csIdx = ev.CallStackIndex();
            sample.StackIndex = csIdx == CallStackIndex.Invalid
                ? noStackCallStack
                : rawSource.GetCallStack(csIdx, ev);
            sample.TimeRelativeMSec = ev.TimeStampRelativeMSec;
            sample.Metric = 1;
            rawSource.AddSample(sample);
        }
        rawSource.DoneAddingSamples();

        // 3. Resolve symbols for hot modules (>=50 inclusive samples). Mirrors PerfView default.
        //    Symbol resolution must happen BEFORE normalization so LookupWarmSymbols has a chance
        //    to convert raw addresses to real symbols; only then do we collapse the still-unresolved
        //    "module!hex" frames into per-module "module!?" buckets.
        rawSource.LookupWarmSymbols(50, symbolReader);

        // 4. Walk frames to compute symbol resolution stats. This is the PHYSICAL frame resolution
        //    rate (resolved actual addresses / total frame addresses) — independent from the
        //    per-module roll-up done below for top-N reporting. Counted on the raw source so the
        //    "?!?" synthetic frame and per-module "?" buckets do not skew the signal.
        long resolvedFrames = 0, unresolvedFrames = 0;
        var unresolvedByModule = new Dictionary<string, long>();
        for (var i = 0; i < (int)rawSource.CallFrameIndexLimit; i++)
        {
            var frameName = rawSource.GetFrameName((StackSourceFrameIndex)i, fullModulePath: false);
            // PerfView convention: unresolved frames contain '?' or start with raw '0x' addresses.
            // Resolved frames look like "module!Symbol" or "module!Symbol+0x..".
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

        // 5. Build a normalized stack source that collapses unresolved per-address frames to
        //    per-module "module!?" buckets — matches PerfView's display where every unresolved
        //    frame in a module aggregates into a single row. Without this, top-N is dominated by
        //    individual hex offsets in big system DLLs and is useless for LLM consumption.
        var normalizedSource = new MutableTraceEventStackSource(trace) { ShowUnknownAddresses = true };
        var normalizedSample = new StackSourceSample(normalizedSource);
        var stackCache = new Dictionary<StackSourceCallStackIndex, StackSourceCallStackIndex>();
        var frameNameCache = new Dictionary<StackSourceFrameIndex, StackSourceFrameIndex>();
        for (var s = 0; s < rawSource.SampleIndexLimit; s++)
        {
            var src = rawSource.GetSampleByIndex((StackSourceSampleIndex)s);
            normalizedSample.StackIndex = NormalizeStack(rawSource, normalizedSource, src.StackIndex,
                stackCache, frameNameCache, excludeEtwSelfOverhead);
            normalizedSample.TimeRelativeMSec = src.TimeRelativeMSec;
            normalizedSample.Metric = src.Metric;
            normalizedSource.AddSample(normalizedSample);
        }
        normalizedSource.DoneAddingSamples();

        // 6. Build the call tree on the normalized source and rank by exclusive sample count.
        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = normalizedSource };
        var totalSamples = (double)Math.Max(1, callTree.Root.InclusiveCount);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveCount)
            .Take(top)
            .Select(n => new CpuFunctionRow(
                Function: n.Name,
                ExclusiveSamples: (long)n.ExclusiveCount,
                InclusiveSamples: (long)n.InclusiveCount,
                ExclusivePct: 100.0 * n.ExclusiveCount / totalSamples,
                InclusivePct: 100.0 * n.InclusiveCount / totalSamples,
                ExclusivePctOfTrace: hasFilter && traceTotalSamples > 0
                    ? 100.0 * n.ExclusiveCount / traceTotalSamples
                    : (double?)null,
                InclusivePctOfTrace: hasFilter && traceTotalSamples > 0
                    ? 100.0 * n.InclusiveCount / traceTotalSamples
                    : (double?)null))
            .ToList();

        var topUnresolved = unresolvedByModule
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv => new UnresolvedModule(kv.Key, kv.Value))
            .ToList();
        var stats = new SymbolStats(resolvedFrames, unresolvedFrames, resolutionRate, topUnresolved);
        var warnings = resolutionRate < 0.8
            ? new List<string> { WarningBuilder.SymbolResolution(resolutionRate) }
            : new List<string>();

        return new CpuTopFunctionsResponse(rows, stats, warnings);
    }

    /// <summary>
    /// Recursively rebuild a callstack into the normalized stack source, collapsing unresolved
    /// "module!hex" frames into per-module "module!?" buckets. Caches per-stack and per-frame
    /// to keep the cost O(unique frames) rather than O(all frames across all samples).
    /// </summary>
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
    /// from Fix #1 also passes through unchanged (its symbol part is "?", its module is "?").
    /// </summary>
    private static string NormalizeName(string name, bool excludeEtwSelfOverhead)
    {
        if (excludeEtwSelfOverhead)
        {
            // Match the symbol part against known ETW-overhead fragments. Substring rather
            // than equality because PerfView's resolver sometimes appends "+0x10" offsets,
            // and TraceEvent on certain Windows builds spells them as "EtwpLogKernelEvent_0".
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
        // Synthetic "?!?" root: keep as-is (already aggregated form).
        if (symPart.Length == 1 && symPart[0] == '?') return name;
        // Unresolved hex addresses: "module!0xfffff8003862a25c" -> "module!?".
        if (symPart.StartsWith("0x", StringComparison.Ordinal))
            return string.Concat(name.AsSpan(0, bang), "!?");
        // Other unresolved markers ("module!?+0x10", etc.).
        if (symPart.IndexOf('?') >= 0)
            return string.Concat(name.AsSpan(0, bang), "!?");
        return name;
    }
}

