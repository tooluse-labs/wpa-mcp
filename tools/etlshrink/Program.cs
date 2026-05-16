using System;
using System.IO;
using Microsoft.Diagnostics.Tracing;

// etlshrink — relog an .etl through ETWReloggerTraceEventSource, optionally truncating
// past a relative-time cutoff in milliseconds. Reduces fixture size for git tracking
// without LFS: typical ratio is ~20% from relogger compression alone, plus whatever the
// caller drops via the time filter.
//
// Usage: etlshrink <input.etl> <output.etl> [maxRelativeMs]
//   maxRelativeMs — keep only events whose TimeStampRelativeMSec <= this value.
//                   Omit (or pass a huge number) to relog without truncation.
//
// Example: dotnet run --project tools/etlshrink -- in.etl out.etl 500
//
// Note: this passively echoes events through the relogger. Aggressive time cuts can
// leave classic kernel events named only by raw task GUID/opcode after a clean ETLX
// conversion. MemoryResourceAnalysis intentionally handles the raw Pool task shape
// used by small_memory.etl; do not add tail-preservation filters here without proving
// the resulting fixture with deleted .etlx caches.
class P {
    static int Main(string[] args) {
        if (args.Length < 2 || args.Length > 3) {
            Console.Error.WriteLine("usage: etlshrink <in.etl> <out.etl> [maxMs]");
            return 2;
        }

        var input = args[0];
        var output = args[1];
        var maxMs = args.Length >= 3 ? double.Parse(args[2]) : double.MaxValue;

        long inSize = new FileInfo(input).Length;
        long kept = 0, dropped = 0;

        using var relog = new ETWReloggerTraceEventSource(input, output);
        relog.AllEvents += data => {
            if (data.TimeStampRelativeMSec <= maxMs) { relog.WriteEvent(data); kept++; }
            else dropped++;
        };
        relog.Process();

        long outSize = new FileInfo(output).Length;
        Console.WriteLine($"in={inSize/1024/1024}MB events_kept={kept} events_dropped={dropped} out={outSize/1024/1024}MB ratio={(double)outSize/inSize:F2}");
        return 0;
    }
}
