using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Aggregates FileIORead/FileIOWrite events into per-file byte/count totals.
//
// Two-pass design: FileObjectResolver.Build walks the trace once to populate the
// FileObject -> FileName map, then TopFiles walks it again to aggregate IO. This
// matters because a Read/Write may fire before the FileIOCreate that named its
// FileObject was processed in callback order; the resolver must be fully built
// first so Resolve never falls back to <unmapped> for a file that did get named.
public static class FileIoAnalysis
{
    public static FileIoResponse TopFiles(TraceLog trace, int top, int? pid, long? startUs = null, long? endUs = null)
    {
        var resolver = FileObjectResolver.Build(trace);
        var agg = new Dictionary<string, (long ReadBytes, long ReadCount, long WriteBytes, long WriteCount)>();

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.FileIORead += data =>
            {
                if (pid is { } p && data.ProcessID != p) return;
                if (!InWindow(data.TimeStampRelativeMSec, startUs, endUs)) return;
                var name = resolver.Resolve(data.FileObject);
                var cur = agg.GetValueOrDefault(name);
                agg[name] = (cur.ReadBytes + data.IoSize, cur.ReadCount + 1, cur.WriteBytes, cur.WriteCount);
            };
            kernel.FileIOWrite += data =>
            {
                if (pid is { } p && data.ProcessID != p) return;
                if (!InWindow(data.TimeStampRelativeMSec, startUs, endUs)) return;
                var name = resolver.Resolve(data.FileObject);
                var cur = agg.GetValueOrDefault(name);
                agg[name] = (cur.ReadBytes, cur.ReadCount, cur.WriteBytes + data.IoSize, cur.WriteCount + 1);
            };
        });

        var rows = agg
            .Select(kv => new FileIoRow(kv.Key, kv.Value.ReadBytes, kv.Value.ReadCount, kv.Value.WriteBytes, kv.Value.WriteCount))
            .OrderByDescending(r => r.ReadBytes + r.WriteBytes)
            .Take(top)
            .ToList();

        return new FileIoResponse(rows);
    }

    private static bool InWindow(double timeStampRelativeMSec, long? startUs, long? endUs)
    {
        var nowUs = (long)(timeStampRelativeMSec * 1000);
        return (!startUs.HasValue || nowUs >= startUs.Value) &&
               (!endUs.HasValue || nowUs < endUs.Value);
    }
}
