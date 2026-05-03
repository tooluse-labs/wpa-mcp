using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
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
        bool hasVirtualAlloc = false, hasNetIo = false, hasRegistry = false;
        bool hasReadyThread = false, hasInterrupt = false, hasAlpc = false, hasThreadEvents = false;
        bool hasClrGc = false, hasClrJit = false;
        bool hasClrAlloc = false, hasClrException = false, hasClrContention = false;

        // Single source pass with both kernel and CLR parsers attached — they share the
        // same TraceEventDispatcher so we don't pay for two full trace walks just to set
        // boolean flags. (KernelEventWalker / ClrEventWalker each create their own source +
        // call Process(); using them sequentially here would double the cold-load cost.)
        var source = trace.Events.GetSource();
        var kernel = new KernelTraceEventParser(source);
        var clr = new ClrTraceEventParser(source);

        // For every event group, multiple events fire iff the same kernel keyword is enabled
        // (e.g. Registry: Query/Open/SetValue all gated by the Registry keyword).  One
        // representative subscription per group is enough — they always co-occur, and per-event
        // dispatch isn't free on a multi-GB trace.  Pick a high-volume representative so the
        // detector flips early in the walk.
        kernel.PerfInfoSample += _ => hasCpuSamples = true;
        kernel.ThreadCSwitch += _ => hasCSwitch = true;
        kernel.FileIORead += _ => hasFileIo = true;
        kernel.DiskIORead += _ => hasDiskIo = true;
        kernel.ImageLoad += _ => hasImageLoad = true;
        kernel.MemoryHardFault += _ => hasHardFaults = true;
        kernel.StackWalkStack += _ => hasStackWalks = true;
        kernel.VirtualMemAlloc += _ => hasVirtualAlloc = true;
        kernel.TcpIpSend += _ => hasNetIo = true;
        kernel.RegistryQueryValue += _ => hasRegistry = true;
        kernel.DispatcherReadyThread += _ => hasReadyThread = true;
        kernel.PerfInfoDPC += _ => hasInterrupt = true;
        kernel.ALPCSendMessage += _ => hasAlpc = true;
        kernel.ThreadStart += _ => hasThreadEvents = true;

        clr.GCStart += _ => hasClrGc = true;
        clr.MethodJittingStarted += _ => hasClrJit = true;
        clr.GCAllocationTick += _ => hasClrAlloc = true;
        clr.ExceptionStart += _ => hasClrException = true;
        clr.ContentionStart += _ => hasClrContention = true;

        source.Process();

        return new TraceCapabilities(
            HasCpuSamples: hasCpuSamples,
            HasCSwitch: hasCSwitch,
            HasFileIo: hasFileIo,
            HasDiskIo: hasDiskIo,
            HasImageLoad: hasImageLoad,
            HasHardFaults: hasHardFaults,
            HasStackWalks: hasStackWalks,
            HasVirtualAlloc: hasVirtualAlloc,
            HasNetIo: hasNetIo,
            HasRegistry: hasRegistry,
            HasReadyThread: hasReadyThread,
            HasInterrupt: hasInterrupt,
            HasAlpc: hasAlpc,
            HasThreadEvents: hasThreadEvents,
            HasClrGc: hasClrGc,
            HasClrJit: hasClrJit,
            HasClrAlloc: hasClrAlloc,
            HasClrException: hasClrException,
            HasClrContention: hasClrContention);
    }
}
