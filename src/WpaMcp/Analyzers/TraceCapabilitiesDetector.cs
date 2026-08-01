using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using WpaMcp.Core;
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
        bool hasVirtualAlloc = false, hasNetIo = false, hasNetConnections = false, hasRegistry = false;
        bool hasReadyThread = false, hasInterrupt = false, hasAlpc = false, hasThreadEvents = false;
        bool hasClrGc = false, hasClrJit = false;
        bool hasClrAlloc = false, hasClrException = false, hasClrContention = false;
        bool hasNtHeap = false, hasMemoryProcessInfo = false, hasHandleEvents = false, hasPoolEvents = false;
        bool hasReadyThreadStacks = false, hasInterruptStacks = false;
        bool hasAttachedEventStacks = false;
        long explicitStackWalkEventCount = 0;
        var cpuCoverage = new DomainStackCoverageAccumulator("cpu");
        var cswitchCoverage = new DomainStackCoverageAccumulator(
            "cswitch", stackSemantics: "switch_out_blocking_stack");
        var fileIoCoverage = new DomainStackCoverageAccumulator("file_io", "bytes");
        var diskIoCoverage = new DomainStackCoverageAccumulator("disk_io", "bytes");
        var imageLoadCoverage = new DomainStackCoverageAccumulator("image_load");
        var hardFaultCoverage = new DomainStackCoverageAccumulator("hard_fault", "bytes");
        var virtualAllocCoverage = new DomainStackCoverageAccumulator(
            "virtual_alloc", "virtualMemoryOperationBytes");
        var netIoCoverage = new DomainStackCoverageAccumulator("net_io", "bytes");
        var registryCoverage = new DomainStackCoverageAccumulator("registry");
        var readyThreadCoverage = new DomainStackCoverageAccumulator("ready_thread");
        var interruptCoverage = new DomainStackCoverageAccumulator("interrupt", "us");
        var alpcCoverage = new DomainStackCoverageAccumulator("alpc");
        var clrAllocCoverage = new DomainStackCoverageAccumulator("clr_alloc", "bytes");
        var clrExceptionCoverage = new DomainStackCoverageAccumulator("clr_exception");
        var heapAllocCoverage = new DomainStackCoverageAccumulator("heap_alloc", "bytes");
        var genericEventCoverage = new DomainStackCoverageAccumulator("generic_event");
        // Identity indexing is only needed when managed contention is actually present.
        // Most traces have none, so avoid a second trace walk on the common path.
        var identities = new Lazy<TraceIdentityIndex>(() => TraceIdentityIndex.For(trace));
        var contentionPairer = new IntervalPairAccumulator<
            ThreadInstanceKey,
            ContentionStartData,
            ContentionStopData>();

        // Single source pass with both kernel and CLR parsers attached — they share the
        // same TraceEventDispatcher so we don't pay for two full trace walks just to set
        // boolean flags. (KernelEventWalker / ClrEventWalker each create their own source +
        // call Process(); using them sequentially here would double the cold-load cost.)
        var source = trace.Events.GetSource();
        var kernel = new KernelTraceEventParser(source);
        var clr = new ClrTraceEventParser(source);
        var heap = new Microsoft.Diagnostics.Tracing.Parsers.Kernel.HeapTraceProviderTraceEventParser(source);

        source.AllEvents += data =>
        {
            var hasStack = data.CallStackIndex() != CallStackIndex.Invalid;
            genericEventCoverage.Observe(hasStack, metric: 1);
            if (hasStack)
                hasAttachedEventStacks = true;
        };

        void Observe(
            DomainStackCoverageAccumulator coverage,
            TraceEvent data,
            long metric,
            CallStackIndex? stack = null)
        {
            var stackIndex = stack ?? data.CallStackIndex();
            var hasStack = stackIndex != CallStackIndex.Invalid;
            coverage.Observe(hasStack, metric);
            if (hasStack)
                hasAttachedEventStacks = true;
        }

        // Subscribe to the same event family each downstream analyzer consumes. Some captures
        // can be write-only, receive-only, or free-only, so a single representative event would
        // incorrectly report "capability absent" while the analyzer can still return rows.
        kernel.PerfInfoSample += data =>
        {
            hasCpuSamples = true;
            Observe(cpuCoverage, data, metric: 1);
        };
        kernel.ThreadCSwitch += data =>
        {
            hasCSwitch = true;
            Observe(cswitchCoverage, data, metric: 1, stack: data.BlockingStack());
        };
        kernel.FileIORead += data =>
        {
            hasFileIo = true;
            Observe(fileIoCoverage, data, data.IoSize);
        };
        kernel.FileIOWrite += data =>
        {
            hasFileIo = true;
            Observe(fileIoCoverage, data, data.IoSize);
        };
        kernel.DiskIORead += data =>
        {
            hasDiskIo = true;
            Observe(diskIoCoverage, data, data.TransferSize);
        };
        kernel.DiskIOWrite += data =>
        {
            hasDiskIo = true;
            Observe(diskIoCoverage, data, data.TransferSize);
        };
        kernel.ImageLoad += data =>
        {
            hasImageLoad = true;
            Observe(imageLoadCoverage, data, metric: 1);
        };
        kernel.MemoryHardFault += data =>
        {
            hasHardFaults = true;
            Observe(hardFaultCoverage, data, data.ByteCount);
        };
        kernel.StackWalkStack += _ =>
            explicitStackWalkEventCount = checked(explicitStackWalkEventCount + 1);
        void ObserveVirtualAlloc(Microsoft.Diagnostics.Tracing.Parsers.Kernel.VirtualAllocTraceData data)
        {
            hasVirtualAlloc = true;
            var bytes = (long)data.Length;
            if (bytes != 0)
                Observe(virtualAllocCoverage, data, bytes);
        }
        kernel.VirtualMemAlloc += ObserveVirtualAlloc;
        kernel.VirtualMemFree += ObserveVirtualAlloc;

        void ObserveNetIo(TraceEvent data, int size)
        {
            hasNetIo = true;
            Observe(netIoCoverage, data, size);
        }
        kernel.TcpIpSend += data => ObserveNetIo(data, data.size);
        kernel.TcpIpRecv += data => ObserveNetIo(data, data.size);
        kernel.TcpIpSendIPV6 += data => ObserveNetIo(data, data.size);
        kernel.TcpIpRecvIPV6 += data => ObserveNetIo(data, data.size);
        kernel.UdpIpSend += data => ObserveNetIo(data, data.size);
        kernel.UdpIpRecv += data => ObserveNetIo(data, data.size);
        kernel.UdpIpSendIPV6 += data => ObserveNetIo(data, data.size);
        kernel.UdpIpRecvIPV6 += data => ObserveNetIo(data, data.size);
        kernel.TcpIpConnect += _ => hasNetConnections = true;
        kernel.TcpIpConnectIPV6 += _ => hasNetConnections = true;
        kernel.TcpIpAccept += _ => hasNetConnections = true;
        kernel.TcpIpAcceptIPV6 += _ => hasNetConnections = true;
        kernel.TcpIpDisconnect += _ => hasNetConnections = true;
        kernel.TcpIpDisconnectIPV6 += _ => hasNetConnections = true;
        kernel.TcpIpReconnect += _ => hasNetConnections = true;
        kernel.TcpIpReconnectIPV6 += _ => hasNetConnections = true;
        void ObserveRegistry(Microsoft.Diagnostics.Tracing.Parsers.Kernel.RegistryTraceData data)
        {
            hasRegistry = true;
            Observe(registryCoverage, data, metric: 1);
        }
        kernel.RegistryQueryValue += ObserveRegistry;
        kernel.RegistryQuery += ObserveRegistry;
        kernel.RegistryQueryMultipleValue += ObserveRegistry;
        kernel.RegistryOpen += ObserveRegistry;
        kernel.RegistryCreate += ObserveRegistry;
        kernel.RegistrySetValue += ObserveRegistry;
        kernel.RegistrySetInformation += ObserveRegistry;
        kernel.RegistryDeleteValue += ObserveRegistry;
        kernel.RegistryDelete += ObserveRegistry;
        kernel.RegistryEnumerateKey += ObserveRegistry;
        kernel.RegistryEnumerateValueKey += ObserveRegistry;
        kernel.RegistryVirtualize += ObserveRegistry;
        kernel.DispatcherReadyThread += data =>
        {
            hasReadyThread = true;
            Observe(readyThreadCoverage, data, metric: 1);
        };
        kernel.PerfInfoDPC += data =>
        {
            hasInterrupt = true;
            Observe(interruptCoverage, data, TraceTime.FromMilliseconds(data.ElapsedTimeMSec));
        };
        kernel.PerfInfoISR += data =>
        {
            hasInterrupt = true;
            Observe(interruptCoverage, data, TraceTime.FromMilliseconds(data.ElapsedTimeMSec));
        };
        kernel.ALPCSendMessage += data => { hasAlpc = true; Observe(alpcCoverage, data, metric: 1); };
        kernel.ALPCReceiveMessage += data => { hasAlpc = true; Observe(alpcCoverage, data, metric: 1); };
        kernel.ThreadStart += _ => hasThreadEvents = true;
        kernel.ThreadStop += _ => hasThreadEvents = true;
        kernel.MemoryProcessMemInfo += _ => hasMemoryProcessInfo = true;
        kernel.ObjectCreateHandle += _ => hasHandleEvents = true;
        kernel.ObjectCloseHandle += _ => hasHandleEvents = true;
        kernel.ObjectDuplicateHandle += _ => hasHandleEvents = true;

        clr.GCStart += _ => hasClrGc = true;
        clr.MethodJittingStarted += _ => hasClrJit = true;
        clr.GCAllocationTick += data =>
        {
            hasClrAlloc = true;
            var bytes = data.AllocationAmount64 > 0 ? data.AllocationAmount64 : data.AllocationAmount;
            if (bytes > 0)
                Observe(clrAllocCoverage, data, bytes);
        };
        clr.ExceptionStart += data =>
        {
            hasClrException = true;
            Observe(clrExceptionCoverage, data, metric: 1);
        };
        clr.ContentionStart += data =>
        {
            if (data.ContentionFlags != ContentionFlags.Managed)
                return;
            hasClrContention = true;
            var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
            var resolution = identities.Value.Threads.ResolveAt(
                data.ProcessID,
                data.ThreadID,
                timestampUs);
            if (resolution.Status == InstanceResolutionStatus.Resolved && resolution.Value.HasValue)
            {
                contentionPairer.AddStart(
                    resolution.Value.Value,
                    timestampUs,
                    new ContentionStartData(data.CallStackIndex()));
            }
        };
        clr.ContentionStop += data =>
        {
            if (data.ContentionFlags != ContentionFlags.Managed)
                return;
            var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
            var resolution = identities.Value.Threads.ResolveAtEndpoint(
                data.ProcessID,
                data.ThreadID,
                timestampUs);
            if (resolution.Status == InstanceResolutionStatus.Resolved && resolution.Value.HasValue)
            {
                contentionPairer.AddStop(
                    resolution.Value.Value,
                    timestampUs,
                    new ContentionStopData());
            }
        };

        void ObserveHeap(TraceEvent data, long bytes)
        {
            hasNtHeap = true;
            if (bytes > 0)
                Observe(heapAllocCoverage, data, bytes);
        }
        heap.HeapTraceAlloc += data => ObserveHeap(data, data.AllocSize);
        heap.HeapTraceReAlloc += data => ObserveHeap(data, data.NewAllocSize);

        source.Process();

        var clrContentionCoverage = ProjectClrContentionCoverage(contentionPairer.Complete());

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

        var coverageByDomain = new Dictionary<string, DomainStackCoverage>(StringComparer.Ordinal)
        {
            ["cpu"] = cpuCoverage.Snapshot(),
            ["cswitch"] = cswitchCoverage.Snapshot(),
            ["file_io"] = fileIoCoverage.Snapshot(),
            ["disk_io"] = diskIoCoverage.Snapshot(),
            ["image_load"] = imageLoadCoverage.Snapshot(),
            ["hard_fault"] = hardFaultCoverage.Snapshot(),
            ["virtual_alloc"] = virtualAllocCoverage.Snapshot(),
            ["net_io"] = netIoCoverage.Snapshot(),
            ["registry"] = registryCoverage.Snapshot(),
            ["ready_thread"] = readyThreadCoverage.Snapshot(),
            ["interrupt"] = interruptCoverage.Snapshot(),
            ["alpc"] = alpcCoverage.Snapshot(),
            ["clr_alloc"] = clrAllocCoverage.Snapshot(),
            ["clr_exception"] = clrExceptionCoverage.Snapshot(),
            ["clr_contention"] = clrContentionCoverage,
            ["heap_alloc"] = heapAllocCoverage.Snapshot(),
            // This is a trace-wide aggregate only. GenericEventStackAnalysis returns the
            // authoritative provider/filter-specific coverage on every query response.
            ["generic_event"] = genericEventCoverage.Snapshot(),
        };
        hasReadyThreadStacks = coverageByDomain["ready_thread"].StackedEventCount > 0;
        hasInterruptStacks = coverageByDomain["interrupt"].StackedEventCount > 0;
        var hasExplicitStackWalkEvents = explicitStackWalkEventCount > 0;
        var hasStackWalks = hasExplicitStackWalkEvents || hasAttachedEventStacks;

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
            HasCSwitchStacks: coverageByDomain["cswitch"].StackedEventCount > 0,
            HasReadyThreadStacks: hasReadyThreadStacks,
            HasInterruptStacks: hasInterruptStacks,
            HasExplicitStackWalkEvents: hasExplicitStackWalkEvents,
            ExplicitStackWalkEventCount: explicitStackWalkEventCount,
            HasAttachedEventStacks: hasAttachedEventStacks,
            StackCoverageByDomain: coverageByDomain);
    }

    internal static DomainStackCoverage ProjectClrContentionCoverage(
        IntervalPairResult<ThreadInstanceKey, ContentionStartData, ContentionStopData> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var coverage = new DomainStackCoverageAccumulator("clr_contention", "us");
        foreach (var pair in intervals.Pairs)
        {
            if (pair.FullDurationUs > 0)
            {
                coverage.Observe(
                    pair.StartData.Stack != CallStackIndex.Invalid,
                    pair.FullDurationUs);
            }
        }
        return coverage.Snapshot();
    }
}
