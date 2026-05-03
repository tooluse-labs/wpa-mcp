using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// .NET CLR heap-size timeline — chronological list of GCHeapStats events.  PerfView surfaces
// this in 'GCStats' as the per-GC heap snapshot table; we expose it as a time series so an
// LLM can answer "is the heap leaking over time" / "is the pinned-object count climbing"
// without orchestrating multiple calls.
//
// GCHeapStats fires once per GC at the end (just before GCStop), carrying the per-generation
// sizes after the collection.  Pairs naturally with clr_gc_analysis (which lists the GC
// events) — same trace, same events, different aggregation.  Generations 0/1/2 are the
// classical heap; generation 3 is LOH; generation 4 is POH (pinned object heap, .NET 5+).
//
// Requires the Microsoft-Windows-DotNETRuntime ETW provider with the GC keyword in the
// capture profile.
public static class GcHeapStatsAnalysis
{
    public static GcHeapStatsResponse Analyze(TraceLog trace, int? pid, long? startUs, long? endUs)
    {
        var rows = new List<GcHeapStatsRow>();

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.GCHeapStats += data =>
            {
                if (pid is { } p && data.ProcessID != p) return;
                var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (startUs is { } s && nowUs < s) return;
                if (endUs is { } e && nowUs > e) return;

                rows.Add(new GcHeapStatsRow(
                    TimeUs: nowUs,
                    Pid: data.ProcessID,
                    TotalHeapBytes: data.TotalHeapSize,
                    Gen0Bytes: data.GenerationSize0,
                    Gen1Bytes: data.GenerationSize1,
                    Gen2Bytes: data.GenerationSize2,
                    LohBytes: data.GenerationSize3,
                    PohBytes: data.GenerationSize4,
                    PinnedObjectCount: data.PinnedObjectCount,
                    GcHandleCount: data.GCHandleCount,
                    FinalizationPromotedBytes: data.FinalizationPromotedSize,
                    FinalizationPromotedCount: data.FinalizationPromotedCount));
            };
        });

        var warnings = new List<string>();
        if (rows.Count == 0)
            warnings.Add(WarningBuilder.MissingClrKeyword("GCHeapStats", "GC",
                "or no GC fired in the filter window for the given PID"));

        return new GcHeapStatsResponse(Pid: pid, Rows: rows, Warnings: warnings);
    }
}
