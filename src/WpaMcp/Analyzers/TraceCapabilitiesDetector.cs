using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Detects which kernel-keyword event classes are present in the trace, so callers know
// upfront whether dependent tools will return data. Implementation: subscribe one no-op
// flag-setting handler per typed event class, then do a bounded raw-name scan for event
// classes TraceEvent can preserve without dispatching through typed callbacks.
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
        bool hasVirtualAlloc = false, hasNetIo = false, hasNetConnections = false, hasRegistry = false;
        bool hasReadyThread = false, hasInterrupt = false, hasAlpc = false, hasThreadEvents = false;
        bool hasClrGc = false, hasClrJit = false;
        bool hasClrAlloc = false, hasClrException = false, hasClrContention = false;
        bool hasNtHeap = false, hasMemoryProcessInfo = false, hasHandleEvents = false, hasPoolEvents = false;
        bool hasCSwitchStacks = false, hasReadyThreadStacks = false, hasInterruptStacks = false;

        // Single source pass with both kernel and CLR parsers attached — they share the
        // same TraceEventDispatcher so we don't pay for two full trace walks just to set
        // boolean flags. (KernelEventWalker / ClrEventWalker each create their own source +
        // call Process(); using them sequentially here would double the cold-load cost.)
        var source = trace.Events.GetSource();
        var kernel = new KernelTraceEventParser(source);
        var clr = new ClrTraceEventParser(source);
        var heap = new Microsoft.Diagnostics.Tracing.Parsers.Kernel.HeapTraceProviderTraceEventParser(source);

        void MarkStackIfPresent(TraceEvent data)
        {
            if (data.CallStackIndex() != CallStackIndex.Invalid)
                hasStackWalks = true;
        }

        // Subscribe to the same event family each downstream analyzer consumes. Some captures
        // can be write-only, receive-only, or free-only, so a single representative event would
        // incorrectly report "capability absent" while the analyzer can still return rows.
        kernel.PerfInfoSample += data => { hasCpuSamples = true; MarkStackIfPresent(data); };
        kernel.ThreadCSwitch += data =>
        {
            hasCSwitch = true;
            if (data.CallStackIndex() != CallStackIndex.Invalid)
            {
                hasCSwitchStacks = true;
                hasStackWalks = true;
            }
        };
        kernel.FileIORead += data => { hasFileIo = true; MarkStackIfPresent(data); };
        kernel.FileIOWrite += data => { hasFileIo = true; MarkStackIfPresent(data); };
        kernel.DiskIORead += data => { hasDiskIo = true; MarkStackIfPresent(data); };
        kernel.DiskIOWrite += data => { hasDiskIo = true; MarkStackIfPresent(data); };
        kernel.ImageLoad += data => { hasImageLoad = true; MarkStackIfPresent(data); };
        kernel.MemoryHardFault += data => { hasHardFaults = true; MarkStackIfPresent(data); };
        kernel.StackWalkStack += _ => hasStackWalks = true;
        kernel.VirtualMemAlloc += data => { hasVirtualAlloc = true; MarkStackIfPresent(data); };
        kernel.VirtualMemFree += data => { hasVirtualAlloc = true; MarkStackIfPresent(data); };
        kernel.TcpIpSend += data => { hasNetIo = true; MarkStackIfPresent(data); };
        kernel.TcpIpRecv += data => { hasNetIo = true; MarkStackIfPresent(data); };
        kernel.TcpIpSendIPV6 += data => { hasNetIo = true; MarkStackIfPresent(data); };
        kernel.TcpIpRecvIPV6 += data => { hasNetIo = true; MarkStackIfPresent(data); };
        kernel.UdpIpSend += data => { hasNetIo = true; MarkStackIfPresent(data); };
        kernel.UdpIpRecv += data => { hasNetIo = true; MarkStackIfPresent(data); };
        kernel.UdpIpSendIPV6 += data => { hasNetIo = true; MarkStackIfPresent(data); };
        kernel.UdpIpRecvIPV6 += data => { hasNetIo = true; MarkStackIfPresent(data); };
        kernel.TcpIpConnect += _ => hasNetConnections = true;
        kernel.TcpIpConnectIPV6 += _ => hasNetConnections = true;
        kernel.TcpIpAccept += _ => hasNetConnections = true;
        kernel.TcpIpAcceptIPV6 += _ => hasNetConnections = true;
        kernel.TcpIpDisconnect += _ => hasNetConnections = true;
        kernel.TcpIpDisconnectIPV6 += _ => hasNetConnections = true;
        kernel.TcpIpReconnect += _ => hasNetConnections = true;
        kernel.TcpIpReconnectIPV6 += _ => hasNetConnections = true;
        kernel.RegistryQueryValue += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistryQuery += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistryQueryMultipleValue += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistryOpen += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistryCreate += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistrySetValue += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistrySetInformation += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistryDeleteValue += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistryDelete += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistryEnumerateKey += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistryEnumerateValueKey += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.RegistryVirtualize += data => { hasRegistry = true; MarkStackIfPresent(data); };
        kernel.DispatcherReadyThread += data =>
        {
            hasReadyThread = true;
            if (data.CallStackIndex() != CallStackIndex.Invalid)
            {
                hasReadyThreadStacks = true;
                hasStackWalks = true;
            }
        };
        kernel.PerfInfoDPC += data =>
        {
            hasInterrupt = true;
            if (data.CallStackIndex() != CallStackIndex.Invalid)
            {
                hasInterruptStacks = true;
                hasStackWalks = true;
            }
        };
        kernel.PerfInfoISR += data =>
        {
            hasInterrupt = true;
            if (data.CallStackIndex() != CallStackIndex.Invalid)
            {
                hasInterruptStacks = true;
                hasStackWalks = true;
            }
        };
        kernel.ALPCSendMessage += data => { hasAlpc = true; MarkStackIfPresent(data); };
        kernel.ALPCReceiveMessage += data => { hasAlpc = true; MarkStackIfPresent(data); };
        kernel.ThreadStart += _ => hasThreadEvents = true;
        kernel.ThreadStop += _ => hasThreadEvents = true;
        kernel.MemoryProcessMemInfo += _ => hasMemoryProcessInfo = true;
        kernel.ObjectCreateHandle += _ => hasHandleEvents = true;
        kernel.ObjectCloseHandle += _ => hasHandleEvents = true;
        kernel.ObjectDuplicateHandle += _ => hasHandleEvents = true;

        clr.GCStart += _ => hasClrGc = true;
        clr.MethodJittingStarted += _ => hasClrJit = true;
        clr.GCAllocationTick += data => { hasClrAlloc = true; MarkStackIfPresent(data); };
        clr.ExceptionStart += data => { hasClrException = true; MarkStackIfPresent(data); };
        clr.ContentionStart += data => { hasClrContention = true; MarkStackIfPresent(data); };

        heap.HeapTraceAlloc += data => { hasNtHeap = true; MarkStackIfPresent(data); };

        source.Process();

        if (!hasPoolEvents)
        {
            foreach (var ev in trace.Events)
            {
                if (!hasPoolEvents && MemoryResourceAnalysis.IsPoolEventName(ev.EventName))
                    hasPoolEvents = true;

                if (hasPoolEvents)
                    break;
            }
        }

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
            HasNetConnections: hasNetConnections,
            HasRegistry: hasRegistry,
            HasReadyThread: hasReadyThread,
            HasInterrupt: hasInterrupt,
            HasAlpc: hasAlpc,
            HasThreadEvents: hasThreadEvents,
            HasClrGc: hasClrGc,
            HasClrJit: hasClrJit,
            HasClrAlloc: hasClrAlloc,
            HasClrException: hasClrException,
            HasClrContention: hasClrContention,
            HasNtHeap: hasNtHeap,
            HasMemoryProcessInfo: hasMemoryProcessInfo,
            HasHandleEvents: hasHandleEvents,
            HasPoolEvents: hasPoolEvents,
            HasCSwitchStacks: hasCSwitchStacks,
            HasReadyThreadStacks: hasReadyThreadStacks,
            HasInterruptStacks: hasInterruptStacks);
    }
}
