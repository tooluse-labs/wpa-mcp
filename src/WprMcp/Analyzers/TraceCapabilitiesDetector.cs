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

        // Single source pass with both kernel and CLR parsers attached — they share the
        // same TraceEventDispatcher so we don't pay for two full trace walks just to set
        // boolean flags. (KernelEventWalker / ClrEventWalker each create their own source +
        // call Process(); using them sequentially here would double the cold-load cost.)
        var source = trace.Events.GetSource();
        var kernel = new KernelTraceEventParser(source);
        var clr = new ClrTraceEventParser(source);

        kernel.PerfInfoSample += _ => hasCpuSamples = true;
        kernel.ThreadCSwitch += _ => hasCSwitch = true;
        kernel.FileIORead += _ => hasFileIo = true;
        kernel.FileIOWrite += _ => hasFileIo = true;
        kernel.DiskIORead += _ => hasDiskIo = true;
        kernel.DiskIOWrite += _ => hasDiskIo = true;
        kernel.ImageLoad += _ => hasImageLoad = true;
        kernel.MemoryHardFault += _ => hasHardFaults = true;
        kernel.StackWalkStack += _ => hasStackWalks = true;
        kernel.VirtualMemAlloc += _ => hasVirtualAlloc = true;
        kernel.VirtualMemFree += _ => hasVirtualAlloc = true;
        kernel.TcpIpSend += _ => hasNetIo = true;
        kernel.TcpIpRecv += _ => hasNetIo = true;
        kernel.UdpIpSend += _ => hasNetIo = true;
        kernel.UdpIpRecv += _ => hasNetIo = true;
        kernel.RegistryQueryValue += _ => hasRegistry = true;
        kernel.RegistryOpen += _ => hasRegistry = true;
        kernel.RegistrySetValue += _ => hasRegistry = true;
        kernel.DispatcherReadyThread += _ => hasReadyThread = true;
        kernel.PerfInfoDPC += _ => hasInterrupt = true;
        kernel.PerfInfoISR += _ => hasInterrupt = true;
        kernel.ALPCSendMessage += _ => hasAlpc = true;
        kernel.ALPCReceiveMessage += _ => hasAlpc = true;
        kernel.ThreadStart += _ => hasThreadEvents = true;
        kernel.ThreadStop += _ => hasThreadEvents = true;

        clr.GCStart += _ => hasClrGc = true;
        clr.GCSuspendEEStart += _ => hasClrGc = true;
        clr.MethodJittingStarted += _ => hasClrJit = true;

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
            HasClrJit: hasClrJit);
    }
}
