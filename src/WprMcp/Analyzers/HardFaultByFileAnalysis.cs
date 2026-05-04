using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Aggregates MemoryHardFault events into per-file page-in totals.  PerfView equivalent:
// "Memory Hard Fault → ByFile" view.  Most hard faults are mmap'd files being touched for
// the first time; the rest come from paged-out heap/stack pages and the page file.
//
// Probe findings (TraceEvent 3.2.2, KernelTraceEventParser):
//   * Event: MemoryHardFault (NOT PageFaultHardFault as the plan template guessed).
//   * Data type: MemoryHardFaultTraceData with these properties:
//       Double ElapsedTimeMSec
//       Int64 ReadOffset
//       UInt64 VirtualAddress
//       UInt64 FileKey         -- Section-object key, NOT a user-mode FileObject handle.
//       String FileName        -- Sometimes populated directly; can be empty for files
//                                 mapped before the trace started.
//       Int32 ByteCount        -- Bytes paged in for this fault.
//       Int32 ProcessID
//
// Because hard faults reference FileKey (not FileObject), FileObjectResolver — which
// keys on FileObject — does not apply here. Per task instructions we keep the change
// contained: this file builds its own FileKey -> FileName map by subscribing to the
// kernel events whose data type is FileIONameTraceData (FileIOName, FileIOFileCreate,
// FileIOFileDelete, FileIOFileRundown — confirmed in Task 11). We also fold in any
// FileName the hard-fault event itself supplies, since it can be present.
//
// Two-pass design (mirrors FileIoAnalysis.TopFiles): one trace pass to populate the
// FileKey -> FileName map, then a second pass to aggregate hard faults. This avoids
// the ordering hazard where a fault arrives before the Rundown that names its key.
public static class HardFaultByFileAnalysis
{
    public static HardFaultByFileResponse Analyze(TraceLog trace, int top, int? pid)
    {
        // Pass 1: build FileKey -> FileName map from FileIONameTraceData events.
        var fileNames = BuildFileKeyMap(trace);

        // Pass 2: aggregate MemoryHardFault events.
        var agg = new Dictionary<string, (long bytes, long count, long maxLatencyUs)>();
        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.MemoryHardFault += data =>
            {
                if (pid is { } p && data.ProcessID != p) return;

                // Prefer the FileName the event carries; otherwise fall back to the FileKey map.
                var name = !string.IsNullOrEmpty(data.FileName)
                    ? data.FileName
                    : fileNames.TryGetValue(data.FileKey, out var mapped) ? mapped : $"<unmapped:0x{data.FileKey:X}>";

                var cur = agg.GetValueOrDefault(name);
                var latencyUs = (long)(data.ElapsedTimeMSec * 1000);
                agg[name] = (cur.bytes + data.ByteCount,
                             cur.count + 1,
                             Math.Max(cur.maxLatencyUs, latencyUs));
            };
        });

        var rows = agg
            .Select(kv => new HardFaultFileRow(kv.Key, kv.Value.bytes, kv.Value.count, kv.Value.maxLatencyUs))
            .OrderByDescending(r => r.PageInBytes)
            .Take(top)
            .ToList();

        var warnings = new List<string> { WarningBuilder.HardFaultKeywordHint };
        return new HardFaultByFileResponse(rows, warnings);
    }

    private static Dictionary<ulong, string> BuildFileKeyMap(TraceLog trace)
    {
        var map = new Dictionary<ulong, string>();
        // FileIONameTraceData events expose FileKey + FileName together. Subscribing to all
        // four catches files named at any point in the trace lifecycle (open, rundown at
        // start, delete, etc.).
        void Capture(Microsoft.Diagnostics.Tracing.Parsers.Kernel.FileIONameTraceData d)
        {
            if (!string.IsNullOrEmpty(d.FileName)) map[d.FileKey] = d.FileName;
        }
        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.FileIOName += Capture;
            kernel.FileIOFileCreate += Capture;
            kernel.FileIOFileDelete += Capture;
            kernel.FileIOFileRundown += Capture;
        });
        return map;
    }
}
