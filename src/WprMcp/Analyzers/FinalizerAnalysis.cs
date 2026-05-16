using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// .NET CLR finalizer analysis — what types are finalized + how long the finalizer thread
// spends running them.  PerfView surfaces parts of this in 'GCStats'; we package the two
// related event streams into one tool because the typical question — "is the finalizer thread
// a problem and what's keeping it busy" — needs both pieces.
//
// Two events:
//
//   GCFinalizeObject (per-object): fires for each object whose finalizer ran.  Carries the
//     resolved TypeName, so we aggregate to a top-types-by-count table.
//   GCFinalizersStart / GCFinalizersStop (per-batch): bracket each run of the finalizer
//     thread.  Stop carries Count = number of finalizers run in this batch.  The interval
//     is the time the finalizer thread blocked the rest of the runtime in this run.
//
// Useful for:
//   - "Why are GCs slow?"  Finalizers that take a long time per object hold up the next GC
//     because GC waits for the finalizer queue to drain on certain transitions.
//   - "What's allocating finalizable objects?"  Top types by count points at the offenders;
//     pair with clr_alloc_top_stacks to find their allocators.
//
// Requires the Microsoft-Windows-DotNETRuntime ETW provider with the GC keyword in the
// capture profile.
public static class FinalizerAnalysis
{
    public static FinalizerAnalysisResponse Analyze(TraceLog trace, int? pid, long? startUs, long? endUs)
    {
        var pendingStartUs = new Dictionary<int, long>();
        var batches = new List<FinalizerBatchRow>();
        var countByType = new Dictionary<string, long>(StringComparer.Ordinal);
        long totalObjects = 0;
        long totalBatchUs = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.GCFinalizeObject += data =>
            {
                if (pid is { } p && data.ProcessID != p) return;
                var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (startUs is { } s && nowUs < s) return;
                if (endUs is { } e && nowUs >= e) return;

                totalObjects++;
                var typeName = string.IsNullOrEmpty(data.TypeName) ? "(unknown)" : data.TypeName;
                countByType[typeName] = countByType.GetValueOrDefault(typeName) + 1;
            };

            clr.GCFinalizersStart += data =>
            {
                pendingStartUs[data.ProcessID] = (long)(data.TimeStampRelativeMSec * 1000);
            };

            clr.GCFinalizersStop += data =>
            {
                if (!pendingStartUs.Remove(data.ProcessID, out var startBatchUs)) return;
                if (pid is { } p && data.ProcessID != p) return;
                var endBatchUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (startUs is { } s && startBatchUs < s) return;
                if (endUs is { } e && endBatchUs >= e) return;

                var dur = endBatchUs - startBatchUs;
                totalBatchUs += dur;
                batches.Add(new FinalizerBatchRow(
                    Pid: data.ProcessID,
                    StartUs: startBatchUs,
                    DurationUs: dur,
                    FinalizersRun: data.Count));
            };
        });

        var topTypes = StackSourceTopN.TopByValue(countByType, 20, (k, v) => new FinalizedTypeRow(k, v));

        var warnings = new List<string>();
        if (totalObjects == 0 && batches.Count == 0)
            warnings.Add(WarningBuilder.MissingClrKeyword("finalizer", "GC",
                "or no finalizers ran in the filter window for the given PID"));

        return new FinalizerAnalysisResponse(
            Pid: pid,
            TotalObjectsFinalized: totalObjects,
            TotalBatchUs: totalBatchUs,
            Batches: batches,
            TopTypes: topTypes,
            Warnings: warnings);
    }
}
