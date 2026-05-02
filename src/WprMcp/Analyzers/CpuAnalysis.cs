using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Output;

namespace WprMcp.Analyzers;

public static class CpuAnalysis
{
    public static CpuTopFunctionsResponse TopFunctions(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog)
    {
        // 1. Filter to CPU sample events (SampledProfileTraceData) with optional pid/time filters.
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
        using var symbolReader = new SymbolReader(
            symbolLog,
            Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
        var stackSource = new MutableTraceEventStackSource(trace) { ShowUnknownAddresses = true };
        var sample = new StackSourceSample(stackSource);
        foreach (var ev in sampleEvents)
        {
            var csIdx = ev.CallStackIndex();
            if (csIdx == CallStackIndex.Invalid) continue;
            sample.StackIndex = stackSource.GetCallStack(csIdx, ev);
            sample.TimeRelativeMSec = ev.TimeStampRelativeMSec;
            sample.Metric = 1;
            stackSource.AddSample(sample);
        }
        stackSource.DoneAddingSamples();

        // 3. Resolve symbols for hot modules (>=50 inclusive samples). Mirrors PerfView default.
        stackSource.LookupWarmSymbols(50, symbolReader);

        // 4. Walk frames to compute symbol resolution stats.
        long resolvedFrames = 0, unresolvedFrames = 0;
        var unresolvedByModule = new Dictionary<string, long>();
        for (var i = 0; i < (int)stackSource.CallFrameIndexLimit; i++)
        {
            var frameName = stackSource.GetFrameName((StackSourceFrameIndex)i, fullModulePath: false);
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

        // 5. Build the call tree and rank functions by exclusive sample count.
        var callTree = new CallTree(ScalingPolicyKind.ScaleToData) { StackSource = stackSource };
        var totalSamples = (double)Math.Max(1, callTree.Root.InclusiveCount);

        var rows = callTree.ByID
            .OrderByDescending(n => n.ExclusiveCount)
            .Take(top)
            .Select(n => new CpuFunctionRow(
                Function: n.Name,
                ExclusiveSamples: (long)n.ExclusiveCount,
                InclusiveSamples: (long)n.InclusiveCount,
                ExclusivePct: 100.0 * n.ExclusiveCount / totalSamples,
                InclusivePct: 100.0 * n.InclusiveCount / totalSamples))
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
}
