using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Detects which kernel-keyword event classes are present in the trace, so callers know
// upfront whether dependent tools will return data. Implementation: subscribe one no-op
// flag-setting handler per event class, run a single source pass.
//
// We don't use trace.Stats / per-event-count enumeration because (a) the event-name format
// varies between OS builds and providers — substring matching against "FileIO/Read" vs
// "FileIo/Read" is fragile — and (b) the kernel parser's typed handlers are the same APIs
// the rest of the codebase uses, so detection accuracy matches actual analyzer behavior.
//
// TraceCache memoizes the result per (path, mtime) so subsequent LoadTrace calls don't
// re-walk — see Core/TraceCache.cs.
internal static class TraceCapabilitiesDetector
{
    public static TraceCapabilities Detect(TraceLog trace)
    {
        bool hasCpuSamples = false, hasCSwitch = false, hasFileIo = false;
        bool hasDiskIo = false, hasImageLoad = false, hasHardFaults = false;
        bool hasStackWalks = false;

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.PerfInfoSample += _ => hasCpuSamples = true;
            kernel.ThreadCSwitch += _ => hasCSwitch = true;
            kernel.FileIORead += _ => hasFileIo = true;
            kernel.FileIOWrite += _ => hasFileIo = true;
            kernel.DiskIORead += _ => hasDiskIo = true;
            kernel.DiskIOWrite += _ => hasDiskIo = true;
            kernel.ImageLoad += _ => hasImageLoad = true;
            kernel.MemoryHardFault += _ => hasHardFaults = true;
            kernel.StackWalkStack += _ => hasStackWalks = true;
        });

        return new TraceCapabilities(
            HasCpuSamples: hasCpuSamples,
            HasCSwitch: hasCSwitch,
            HasFileIo: hasFileIo,
            HasDiskIo: hasDiskIo,
            HasImageLoad: hasImageLoad,
            HasHardFaults: hasHardFaults,
            HasStackWalks: hasStackWalks);
    }
}
