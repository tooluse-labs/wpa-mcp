using System.Diagnostics;
using System.Collections.Frozen;
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
    private const int MaxProviderRows = 50;

    public static TraceCapabilities Detect(TraceLog trace) =>
        Scan(trace, CancellationToken.None, TraceFactsBuildBudget.Default).Capabilities;

    internal static TraceCapabilityScanResult Scan(
        TraceLog trace,
        CancellationToken cancellationToken,
        TraceFactsBuildBudget budget)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(budget);
        var elapsed = Stopwatch.StartNew();
        budget.ThrowIfExceeded(0, elapsed, cancellationToken);
        bool hasCpuSamples = false, hasCSwitch = false, hasFileIo = false;
        bool hasDiskIo = false, hasImageLoad = false, hasHardFaults = false;
        bool hasVirtualAlloc = false, hasNetIo = false, hasNetConnections = false, hasRegistry = false;
        bool hasReadyThread = false, hasInterrupt = false, hasAlpc = false;
        bool hasClrAlloc = false, hasClrException = false;
        bool hasNtHeap = false, hasMemoryProcessInfo = false, hasMemorySystemInfo = false;
        bool hasHandleEvents = false, hasPoolEvents = false;
        bool hasReadyThreadStacks = false, hasInterruptStacks = false;
        bool hasAttachedEventStacks = false;
        long observedProcessStartEventCount = 0;
        long clrGcHeapStatsEventCount = 0;
        long clrFinalizerObjectEventCount = 0;
        long clrFinalizerBatchStartEndpointEventCount = 0;
        long clrFinalizerBatchStopEndpointEventCount = 0;
        long networkConnectionLifecycleEndpointEventCount = 0;
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
        var processEvents = new List<ProcessLifecycleEvent>();
        var threadEvents = new List<ThreadLifecycleEvent>();
        var contentionEndpoints = new List<ClrContentionEndpoint>();
        var finalizerBatchEndpoints = new List<FinalizerEvent>();
        var networkConnectionEndpoints = new List<NetConnectionEvent>();
        var gcIntervalEndpoints = new List<ClrIntervalCapabilityEndpoint>();
        var jitIntervalEndpoints = new List<JitIntervalCapabilityEndpoint>();
        var providers = new Dictionary<string, ProviderAccumulator>(
            StringComparer.OrdinalIgnoreCase);
        long totalEvents = 0;
        long eventsWithCallStacks = 0;
        var contentionPairer = new IntervalPairAccumulator<
            ThreadInstanceKey,
            ContentionStartData,
            ContentionStopData>();
        var clrEndpointCapabilities = new ClrEndpointCapabilityAccumulator();
        var threadEndpointCapabilities = new ThreadEndpointCapabilityAccumulator();

        // Single source pass with both kernel and CLR parsers attached — they share the
        // same TraceEventDispatcher so we don't pay for two full trace walks just to set
        // boolean flags. (KernelEventWalker / ClrEventWalker each create their own source +
        // call Process(); using them sequentially here would double the cold-load cost.)
        var source = AnalysisEvents.CreateDispatcher(trace, cancellationToken);
        var kernel = new KernelTraceEventParser(source);
        var clr = new ClrTraceEventParser(source);
        var heap = new Microsoft.Diagnostics.Tracing.Parsers.Kernel.HeapTraceProviderTraceEventParser(source);

        source.AllEvents += data =>
        {
            totalEvents = checked(totalEvents + 1);
            if ((totalEvents & 0xFFF) == 0)
                budget.ThrowIfExceeded(totalEvents, elapsed, cancellationToken);
            var hasStack = data.CallStackIndex() != CallStackIndex.Invalid;
            genericEventCoverage.Observe(hasStack, metric: 1);
            if (hasStack)
            {
                hasAttachedEventStacks = true;
                eventsWithCallStacks = checked(eventsWithCallStacks + 1);
            }

            var provider = string.IsNullOrWhiteSpace(data.ProviderName)
                ? "<unknown>"
                : data.ProviderName;
            if (!providers.TryGetValue(provider, out var providerAccumulator))
            {
                providerAccumulator = new ProviderAccumulator(provider);
                providers.Add(provider, providerAccumulator);
            }
            providerAccumulator.EventCount = checked(providerAccumulator.EventCount + 1);
            if (hasStack)
            {
                providerAccumulator.EventsWithCallStacks = checked(
                    providerAccumulator.EventsWithCallStacks + 1);
            }

            if (!hasPoolEvents && MemoryResourceAnalysis.IsPoolEventName(data.EventName))
                hasPoolEvents = true;
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
            var bytes = CheckedNonNegativeMetric(data.Length);
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
        void ObserveNetworkConnectionLifecycleEndpoint(NetConnectionEvent endpoint)
        {
            hasNetConnections = true;
            networkConnectionLifecycleEndpointEventCount = checked(
                networkConnectionLifecycleEndpointEventCount + 1);
            networkConnectionEndpoints.Add(endpoint);
        }
        kernel.TcpIpConnect += data => ObserveNetworkConnectionLifecycleEndpoint(
            NetworkEndpoint(data.ProcessID, data.connid, NetConnectionEventKind.Connect,
                data.TimeStampRelativeMSec));
        kernel.TcpIpConnectIPV6 += data => ObserveNetworkConnectionLifecycleEndpoint(
            NetworkEndpoint(data.ProcessID, data.connid, NetConnectionEventKind.Connect,
                data.TimeStampRelativeMSec));
        kernel.TcpIpAccept += data => ObserveNetworkConnectionLifecycleEndpoint(
            NetworkEndpoint(data.ProcessID, data.connid, NetConnectionEventKind.Accept,
                data.TimeStampRelativeMSec));
        kernel.TcpIpAcceptIPV6 += data => ObserveNetworkConnectionLifecycleEndpoint(
            NetworkEndpoint(data.ProcessID, data.connid, NetConnectionEventKind.Accept,
                data.TimeStampRelativeMSec));
        kernel.TcpIpDisconnect += data => ObserveNetworkConnectionLifecycleEndpoint(
            NetworkEndpoint(data.ProcessID, data.connid, NetConnectionEventKind.Disconnect,
                data.TimeStampRelativeMSec));
        kernel.TcpIpDisconnectIPV6 += data => ObserveNetworkConnectionLifecycleEndpoint(
            NetworkEndpoint(data.ProcessID, data.connid, NetConnectionEventKind.Disconnect,
                data.TimeStampRelativeMSec));
        kernel.TcpIpReconnect += data => ObserveNetworkConnectionLifecycleEndpoint(
            NetworkEndpoint(data.ProcessID, data.connid, NetConnectionEventKind.Reconnect,
                data.TimeStampRelativeMSec));
        kernel.TcpIpReconnectIPV6 += data => ObserveNetworkConnectionLifecycleEndpoint(
            NetworkEndpoint(data.ProcessID, data.connid, NetConnectionEventKind.Reconnect,
                data.TimeStampRelativeMSec));
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
        kernel.ProcessStart += data =>
        {
            observedProcessStartEventCount = checked(observedProcessStartEventCount + 1);
            processEvents.Add(new ProcessLifecycleEvent(
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                ProcessLifecycleEventKind.Start));
        };
        kernel.ProcessStop += data => processEvents.Add(new ProcessLifecycleEvent(
            data.ProcessID,
            TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
            ProcessLifecycleEventKind.Stop));
        kernel.ProcessDCStart += data => processEvents.Add(new ProcessLifecycleEvent(
            data.ProcessID,
            TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
            ProcessLifecycleEventKind.RundownStart));
        kernel.ProcessDCStop += data => processEvents.Add(new ProcessLifecycleEvent(
            data.ProcessID,
            TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
            ProcessLifecycleEventKind.RundownStop));
        kernel.ThreadStart += data =>
        {
            threadEndpointCapabilities.Observe(ThreadLifecycleEventKind.Start);
            threadEvents.Add(new ThreadLifecycleEvent(
                data.ProcessID,
                data.ThreadID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                ThreadLifecycleEventKind.Start,
                Observed: true));
        };
        kernel.ThreadStop += data =>
        {
            threadEndpointCapabilities.Observe(ThreadLifecycleEventKind.Stop);
            threadEvents.Add(new ThreadLifecycleEvent(
                data.ProcessID,
                data.ThreadID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                ThreadLifecycleEventKind.Stop,
                Observed: true));
        };
        kernel.ThreadDCStart += data =>
        {
            threadEndpointCapabilities.Observe(ThreadLifecycleEventKind.RundownStart);
            threadEvents.Add(new ThreadLifecycleEvent(
                data.ProcessID,
                data.ThreadID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                ThreadLifecycleEventKind.RundownStart,
                Observed: false));
        };
        kernel.ThreadDCStop += data =>
        {
            threadEndpointCapabilities.Observe(ThreadLifecycleEventKind.RundownStop);
            threadEvents.Add(new ThreadLifecycleEvent(
                data.ProcessID,
                data.ThreadID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                ThreadLifecycleEventKind.RundownStop,
                Observed: false));
        };
        kernel.MemoryProcessMemInfo += _ => hasMemoryProcessInfo = true;
        kernel.MemorySystemMemInfo += _ => hasMemorySystemInfo = true;
        kernel.MemoryMemInfo += _ => hasMemorySystemInfo = true;
        kernel.ObjectCreateHandle += _ => hasHandleEvents = true;
        kernel.ObjectCloseHandle += _ => hasHandleEvents = true;
        kernel.ObjectDuplicateHandle += _ => hasHandleEvents = true;

        clr.GCStart += data =>
        {
            clrEndpointCapabilities.Observe(ClrCapabilityEndpoint.GcStart);
            gcIntervalEndpoints.Add(new ClrIntervalCapabilityEndpoint(
                ClrCapabilityEndpoint.GcStart,
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                GcAnalysis.TryReadClrInstanceId(data),
                data.Count,
                data.Depth,
                data.Reason.ToString()));
        };
        clr.GCStop += data =>
        {
            clrEndpointCapabilities.Observe(ClrCapabilityEndpoint.GcStop);
            gcIntervalEndpoints.Add(new ClrIntervalCapabilityEndpoint(
                ClrCapabilityEndpoint.GcStop,
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                GcAnalysis.TryReadClrInstanceId(data),
                data.Count));
        };
        clr.GCSuspendEEStart += data =>
        {
            clrEndpointCapabilities.Observe(ClrCapabilityEndpoint.GcSuspendEeStart);
            gcIntervalEndpoints.Add(new ClrIntervalCapabilityEndpoint(
                ClrCapabilityEndpoint.GcSuspendEeStart,
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                GcAnalysis.TryReadClrInstanceId(data)));
        };
        clr.GCRestartEEStop += data =>
        {
            clrEndpointCapabilities.Observe(ClrCapabilityEndpoint.GcRestartEeStop);
            gcIntervalEndpoints.Add(new ClrIntervalCapabilityEndpoint(
                ClrCapabilityEndpoint.GcRestartEeStop,
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                GcAnalysis.TryReadClrInstanceId(data)));
        };
        clr.GCHeapStats += _ =>
            clrGcHeapStatsEventCount = checked(clrGcHeapStatsEventCount + 1);
        clr.GCFinalizeObject += _ =>
            clrFinalizerObjectEventCount = checked(clrFinalizerObjectEventCount + 1);
        clr.GCFinalizersStart += data =>
        {
            clrFinalizerBatchStartEndpointEventCount = checked(
                clrFinalizerBatchStartEndpointEventCount + 1);
            finalizerBatchEndpoints.Add(FinalizerEvent.BatchStart(
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                GcAnalysis.TryReadClrInstanceId(data)));
        };
        clr.GCFinalizersStop += data =>
        {
            clrFinalizerBatchStopEndpointEventCount = checked(
                clrFinalizerBatchStopEndpointEventCount + 1);
            finalizerBatchEndpoints.Add(FinalizerEvent.BatchStop(
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                GcAnalysis.TryReadClrInstanceId(data),
                data.Count));
        };
        clr.MethodJittingStarted += data =>
        {
            clrEndpointCapabilities.Observe(ClrCapabilityEndpoint.MethodJittingStarted);
            jitIntervalEndpoints.Add(new JitIntervalCapabilityEndpoint(
                IsStart: true,
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                GcAnalysis.TryReadClrInstanceId(data),
                data.MethodID));
        };
        clr.MethodLoadVerbose += data =>
        {
            clrEndpointCapabilities.Observe(ClrCapabilityEndpoint.MethodLoadVerbose);
            jitIntervalEndpoints.Add(new JitIntervalCapabilityEndpoint(
                IsStart: false,
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                GcAnalysis.TryReadClrInstanceId(data),
                data.MethodID));
        };
        clr.GCAllocationTick += data =>
        {
            hasClrAlloc = true;
            var bytes = data.AllocationAmount64 > 0 ? data.AllocationAmount64 : data.AllocationAmount;
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
            clrEndpointCapabilities.Observe(ClrCapabilityEndpoint.ContentionStart);
            contentionEndpoints.Add(new ClrContentionEndpoint(
                IsStart: true,
                data.ProcessID,
                data.ThreadID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                data.CallStackIndex()));
        };
        clr.ContentionStop += data =>
        {
            if (data.ContentionFlags != ContentionFlags.Managed)
                return;
            clrEndpointCapabilities.Observe(ClrCapabilityEndpoint.ContentionStop);
            contentionEndpoints.Add(new ClrContentionEndpoint(
                IsStart: false,
                data.ProcessID,
                data.ThreadID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                CallStackIndex.Invalid));
        };

        void ObserveHeap(TraceEvent data, long bytes)
        {
            hasNtHeap = true;
            Observe(heapAllocCoverage, data, bytes);
        }
        heap.HeapTraceAlloc += data => ObserveHeap(data, data.AllocSize);
        heap.HeapTraceReAlloc += data => ObserveHeap(data, data.NewAllocSize);

        AnalysisEvents.Process(source, cancellationToken);
        budget.ThrowIfExceeded(totalEvents, elapsed, cancellationToken);

        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var backfill = AnalysisEvents.Enumerate(trace.Processes, cancellationToken)
            .Select(process => new ProcessLifetimeBackfill(
                process.ProcessID,
                TraceTime.FromMilliseconds(process.StartTimeRelativeMsec),
                TraceTime.FromMilliseconds(process.EndTimeRelativeMsec)))
            .ToArray();
        var processLifetimes = TraceIdentityIndex.BuildProcessLifetimes(
            traceEndUs,
            processEvents,
            backfill);
        var identities = TraceIdentityIndex.Register(
            trace,
            TraceIdentityIndex.BuildFromEvents(
                traceEndUs,
                processLifetimes,
                threadEvents));

        var threadLifecycleEvidence = CountThreadLifecycleEvidence(
            identities,
            threadEndpointCapabilities);
        var networkConnectionEvidence = CountNetworkConnectionEvidence(
            identities,
            networkConnectionEndpoints);
        var gcIntervalEvidence = CountGcIntervalEvidence(
            identities,
            gcIntervalEndpoints);
        var jitIntervalEvidence = CountJitIntervalEvidence(
            identities,
            jitIntervalEndpoints);

        var clrFinalizerCompletedBatchCount = CountCompletedFinalizerBatches(
            identities,
            finalizerBatchEndpoints);
        var clrFinalizerSourceEventCount = checked(
            clrFinalizerObjectEventCount +
            clrFinalizerBatchStartEndpointEventCount +
            clrFinalizerBatchStopEndpointEventCount);
        budget.ThrowIfExceeded(totalEvents, elapsed, cancellationToken);

        foreach (var endpoint in AnalysisEvents.Enumerate(contentionEndpoints))
        {
            var resolution = endpoint.IsStart
                ? identities.Threads.ResolveAt(
                    endpoint.Pid,
                    endpoint.Tid,
                    endpoint.TimestampUs)
                : identities.Threads.ResolveAtEndpoint(
                    endpoint.Pid,
                    endpoint.Tid,
                    endpoint.TimestampUs);
            if (resolution.Status != InstanceResolutionStatus.Resolved ||
                !resolution.Value.HasValue)
            {
                continue;
            }

            if (endpoint.IsStart)
            {
                contentionPairer.AddStart(
                    resolution.Value.Value,
                    endpoint.TimestampUs,
                    new ContentionStartData(endpoint.Stack));
            }
            else
            {
                contentionPairer.AddStop(
                    resolution.Value.Value,
                    endpoint.TimestampUs,
                    new ContentionStopData());
            }
        }

        var clrContentionCoverage = ProjectClrContentionCoverage(contentionPairer.Complete());
        budget.ThrowIfExceeded(totalEvents, elapsed, cancellationToken);

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

        var capabilities = new TraceCapabilities(
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
            HasThreadEvents: threadEndpointCapabilities.HasAnySourceEvidence,
            HasClrGc: clrEndpointCapabilities.HasClrGc,
            HasClrJit: clrEndpointCapabilities.HasClrJit,
            HasClrAlloc: hasClrAlloc,
            HasClrException: hasClrException,
            HasClrContention: clrEndpointCapabilities.HasClrContention,
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
            StackCoverageByDomain: coverageByDomain.ToFrozenDictionary(StringComparer.Ordinal),
            HasMemorySystemInfo: hasMemorySystemInfo,
            ObservedProcessStartEventCount: observedProcessStartEventCount,
            ObservedThreadLifecycleEndpointEventCount:
                threadEndpointCapabilities.ObservedEndpointEventCount,
            ThreadRundownEndpointEventCount:
                threadEndpointCapabilities.RundownEndpointEventCount,
            ThreadLifecycleSourceEventCount:
                threadEndpointCapabilities.SourceEventCount,
            ThreadCompletedObservedLifetimeCount:
                threadLifecycleEvidence.CompletedCount,
            ThreadUnmatchedLifecycleEndpointCount:
                threadLifecycleEvidence.UnmatchedCount,
            ThreadInferredBoundaryCount:
                threadLifecycleEvidence.BoundaryCount,
            ClrGcIntervalEndpointEventCount:
                clrEndpointCapabilities.GcIntervalEndpointEventCount,
            ClrGcCompletedIntervalCount: gcIntervalEvidence.CompletedCount,
            ClrGcUnmatchedEndpointCount: gcIntervalEvidence.UnmatchedCount,
            ClrGcBoundaryEvidenceCount: gcIntervalEvidence.BoundaryCount,
            ClrGcHeapStatsEventCount: clrGcHeapStatsEventCount,
            ClrFinalizerObjectEventCount: clrFinalizerObjectEventCount,
            ClrFinalizerBatchStartEndpointEventCount:
                clrFinalizerBatchStartEndpointEventCount,
            ClrFinalizerBatchStopEndpointEventCount:
                clrFinalizerBatchStopEndpointEventCount,
            ClrFinalizerCompletedBatchCount: clrFinalizerCompletedBatchCount,
            ClrFinalizerSourceEventCount: clrFinalizerSourceEventCount,
            NetworkConnectionLifecycleEndpointEventCount:
                networkConnectionLifecycleEndpointEventCount,
            NetworkConnectionCompletedLifecycleCount:
                networkConnectionEvidence.CompletedCount,
            NetworkConnectionUnmatchedEndpointCount:
                networkConnectionEvidence.UnmatchedCount,
            NetworkConnectionBoundaryEvidenceCount:
                networkConnectionEvidence.BoundaryCount,
            ClrJitIntervalEndpointEventCount:
                clrEndpointCapabilities.JitIntervalEndpointEventCount,
            ClrJitCompletedIntervalCount: jitIntervalEvidence.CompletedCount,
            ClrJitUnmatchedEndpointCount: jitIntervalEvidence.UnmatchedCount,
            ClrJitBoundaryEvidenceCount: jitIntervalEvidence.BoundaryCount);
        IReadOnlyList<ProviderEventCount> topProviders = Array.AsReadOnly(
            providers.Values
                .OrderByDescending(provider => provider.EventCount)
                .ThenBy(provider => provider.Provider, StringComparer.OrdinalIgnoreCase)
                .Take(MaxProviderRows)
                .Select(provider => new ProviderEventCount(
                    provider.Provider,
                    provider.EventCount,
                    provider.EventsWithCallStacks,
                    RatioOrNull(provider.EventsWithCallStacks, provider.EventCount),
                    PercentOrNull(provider.EventsWithCallStacks, provider.EventCount)))
                .ToArray());
        var providerSummary = new ProviderEventCountSummary(
            providers.Count,
            totalEvents,
            Math.Max(0, totalEvents - topProviders.Sum(provider => provider.EventCount)),
            topProviders);
        var stackwalkSummary = new TraceStackwalkSummary(
            capabilities.HasExplicitStackWalkEvents,
            capabilities.ExplicitStackWalkEventCount,
            eventsWithCallStacks,
            RatioOrNull(eventsWithCallStacks, totalEvents),
            capabilities.HasExplicitStackWalkEvents,
            capabilities.HasAttachedEventStacks,
            PercentOrNull(eventsWithCallStacks, totalEvents));
        return new TraceCapabilityScanResult(
            capabilities,
            identities,
            new TraceLogicalEventSummary(
                totalEvents,
                eventsWithCallStacks,
                stackwalkSummary,
                providerSummary),
            elapsed.Elapsed);
    }

    private static double? RatioOrNull(long numerator, long denominator) =>
        denominator == 0 ? null : numerator / (double)denominator;

    private static double? PercentOrNull(long numerator, long denominator) =>
        denominator == 0 ? null : 100.0 * numerator / denominator;

    internal static long CheckedUnsignedMetric(ulong value) => checked((long)value);

    internal static long CheckedNonNegativeMetric(long value) => value >= 0
        ? value
        : throw new InvalidDataException("A trace byte-size metric decoded as negative.");

    internal static DomainStackCoverage ProjectClrContentionCoverage(
        IntervalPairResult<ThreadInstanceKey, ContentionStartData, ContentionStopData> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var coverage = new DomainStackCoverageAccumulator("clr_contention", "us");
        foreach (var pair in AnalysisEvents.Enumerate(intervals.Pairs))
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

    private static NetConnectionEvent NetworkEndpoint(
        int pid,
        ulong connId,
        NetConnectionEventKind kind,
        double timestampMilliseconds) =>
        new(
            pid,
            connId,
            kind,
            TraceTime.FromMilliseconds(timestampMilliseconds));

    internal static CompletionEvidenceCountSet CountThreadLifecycleEvidence(
        TraceIdentityIndex identities,
        ThreadEndpointCapabilityAccumulator endpoints)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(endpoints);
        var validLifetimes = identities.Threads.Lifetimes
            .Where(lifetime => lifetime.EndUs > lifetime.StartUs)
            .ToArray();
        var completed = validLifetimes.LongCount(lifetime =>
            lifetime.StartObserved && lifetime.EndObserved);
        var resolvedObserved = identities
            .ObservedThreadLifecycleEndpointEventCountsByProcess
            .Values
            .Aggregate(0L, (total, count) => checked(total + count));
        var resolvedSource = identities.ThreadLifecycleEventCountsByProcess
            .Values
            .Aggregate(0L, (total, count) => checked(total + count));
        var unmatchedObserved = Math.Max(
            0,
            checked(resolvedObserved - checked(completed * 2)));
        var identityUnresolved = Math.Max(
            0,
            checked(endpoints.SourceEventCount - resolvedSource));
        var inferredBoundaries = validLifetimes.Aggregate(
            0L,
            (total, lifetime) => checked(
                total +
                (lifetime.StartObserved ? 0 : 1) +
                (lifetime.EndObserved ? 0 : 1)));
        return new CompletionEvidenceCountSet(
            completed,
            checked(unmatchedObserved + identityUnresolved),
            Math.Max(
                inferredBoundaries,
                endpoints.RundownEndpointEventCount));
    }

    internal static CompletionEvidenceCountSet CountNetworkConnectionEvidence(
        TraceIdentityIndex identities,
        IReadOnlyList<NetConnectionEvent> endpoints)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(endpoints);
        var open = new Dictionary<NetworkCapabilityKey, byte>();
        long completed = 0;
        long unmatched = 0;
        long boundary = 0;

        foreach (var item in AnalysisEvents.Enumerate(endpoints)
                     .Select((value, index) => (value, index))
                     .OrderBy(item => item.value.TimeUs)
                     .ThenBy(item => item.index))
        {
            var endpoint = item.value;
            var isOpen = endpoint.Kind is
                NetConnectionEventKind.Connect or NetConnectionEventKind.Accept;
            var resolution = isOpen
                ? identities.Processes.Resolve(
                    endpoint.Pid,
                    endpoint.TimeUs,
                    processStartUs: null)
                : identities.Processes.ResolveAtEndpoint(
                    endpoint.Pid,
                    endpoint.TimeUs);
            if (resolution.Status != InstanceResolutionStatus.Resolved ||
                !resolution.Value.HasValue)
            {
                boundary = checked(boundary + 1);
                continue;
            }

            var key = new NetworkCapabilityKey(
                resolution.Value.Value,
                endpoint.ConnId);
            if (isOpen)
            {
                if (open.ContainsKey(key))
                    boundary = checked(boundary + 1);
                open[key] = 0;
            }
            else if (open.Remove(key))
            {
                completed = checked(completed + 1);
            }
            else
            {
                unmatched = checked(unmatched + 1);
            }
        }

        boundary = checked(boundary + open.Count);
        return new CompletionEvidenceCountSet(completed, unmatched, boundary);
    }

    internal static CompletionEvidenceCountSet CountGcIntervalEvidence(
        TraceIdentityIndex identities,
        IReadOnlyList<ClrIntervalCapabilityEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(endpoints);
        var accumulator = new GcIntervalAccumulator();
        long identityUnresolved = 0;

        foreach (var item in AnalysisEvents.Enumerate(endpoints)
                     .Select((value, index) => (value, index))
                     .OrderBy(item => item.value.TimeUs)
                     .ThenBy(item => item.index))
        {
            var endpoint = item.value;
            var atEnd = endpoint.Kind is
                ClrCapabilityEndpoint.GcStop or
                ClrCapabilityEndpoint.GcRestartEeStop;
            var resolution = atEnd
                ? identities.Processes.ResolveAtEndpoint(
                    endpoint.Pid,
                    endpoint.TimeUs)
                : identities.Processes.Resolve(
                    endpoint.Pid,
                    endpoint.TimeUs,
                    processStartUs: null);
            if (resolution.Status != InstanceResolutionStatus.Resolved ||
                !resolution.Value.HasValue)
            {
                identityUnresolved = checked(identityUnresolved + 1);
                continue;
            }

            var process = resolution.Value.Value;
            switch (endpoint.Kind)
            {
                case ClrCapabilityEndpoint.GcStart:
                    accumulator.AddGcStart(
                        process,
                        endpoint.ClrInstanceId,
                        endpoint.GcCount,
                        endpoint.TimeUs,
                        endpoint.Generation,
                        endpoint.Reason);
                    break;
                case ClrCapabilityEndpoint.GcStop:
                    accumulator.AddGcStop(
                        process,
                        endpoint.ClrInstanceId,
                        endpoint.GcCount,
                        endpoint.TimeUs);
                    break;
                case ClrCapabilityEndpoint.GcSuspendEeStart:
                    accumulator.AddSuspendStart(
                        process,
                        endpoint.ClrInstanceId,
                        endpoint.TimeUs);
                    break;
                case ClrCapabilityEndpoint.GcRestartEeStop:
                    accumulator.AddRestartStop(
                        process,
                        endpoint.ClrInstanceId,
                        endpoint.TimeUs);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(endpoint.Kind),
                        endpoint.Kind,
                        "Endpoint is not part of CLR GC interval evidence.");
            }
        }

        var result = accumulator.Complete();
        var completed = checked(
            (long)result.Gcs.Count +
            result.Gcs.Sum(gc => (long)gc.Pauses.Count) +
            result.OrphanPauses.Count);
        var unmatched = checked(
            (long)result.UnmatchedGcStartCount +
            result.UnmatchedGcStopCount +
            result.UnmatchedSuspendStartCount +
            result.UnmatchedRestartStopCount);
        var boundary = checked(
            identityUnresolved +
            result.IncompleteEvidence.Count +
            result.InvalidIntervalCount);
        return new CompletionEvidenceCountSet(completed, unmatched, boundary);
    }

    internal static CompletionEvidenceCountSet CountJitIntervalEvidence(
        TraceIdentityIndex identities,
        IReadOnlyList<JitIntervalCapabilityEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(endpoints);
        var pairer = new IntervalPairAccumulator<
            JitPairKey,
            JitStartData,
            JitStopData>();
        long identityUnresolved = 0;

        foreach (var item in AnalysisEvents.Enumerate(endpoints)
                     .Select((value, index) => (value, index))
                     .OrderBy(item => item.value.TimeUs)
                     .ThenBy(item => item.index))
        {
            var endpoint = item.value;
            var resolution = endpoint.IsStart
                ? identities.Processes.Resolve(
                    endpoint.Pid,
                    endpoint.TimeUs,
                    processStartUs: null)
                : identities.Processes.ResolveAtEndpoint(
                    endpoint.Pid,
                    endpoint.TimeUs);
            if (resolution.Status != InstanceResolutionStatus.Resolved ||
                !resolution.Value.HasValue ||
                !endpoint.ClrInstanceId.HasValue)
            {
                identityUnresolved = checked(identityUnresolved + 1);
                continue;
            }

            var key = new JitPairKey(
                resolution.Value.Value,
                endpoint.ClrInstanceId.Value,
                endpoint.MethodId);
            if (endpoint.IsStart)
            {
                pairer.AddStart(
                    key,
                    endpoint.TimeUs,
                    new JitStartData(string.Empty, 0));
            }
            else
            {
                pairer.AddStop(key, endpoint.TimeUs, new JitStopData());
            }
        }

        var result = pairer.Complete();
        return new CompletionEvidenceCountSet(
            result.Pairs.Count,
            checked(result.UnmatchedStarts.Count + result.UnmatchedStops.Count),
            checked(identityUnresolved + result.InvalidIntervals.Count));
    }

    internal static long CountCompletedFinalizerBatches(
        TraceIdentityIndex identities,
        IReadOnlyList<FinalizerEvent> endpoints)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(endpoints);
        var pairer = new IntervalPairAccumulator<
            FinalizerPairKey,
            FinalizerStartData,
            FinalizerStopData>();
        foreach (var endpoint in AnalysisEvents.Enumerate(endpoints)
                     .Select((value, index) => (value, index))
                     .OrderBy(item => item.value.TimeUs)
                     .ThenBy(item => item.index)
                     .Select(item => item.value))
        {
            if (endpoint.Kind == FinalizerEventKind.Object ||
                !endpoint.ClrInstanceId.HasValue)
            {
                continue;
            }

            var processResolution = endpoint.Kind == FinalizerEventKind.BatchStop
                ? identities.Processes.ResolveAtEndpoint(endpoint.Pid, endpoint.TimeUs)
                : identities.Processes.Resolve(
                    endpoint.Pid,
                    endpoint.TimeUs,
                    processStartUs: null);
            if (processResolution.Status != InstanceResolutionStatus.Resolved ||
                !processResolution.Value.HasValue)
            {
                continue;
            }

            var key = new FinalizerPairKey(
                processResolution.Value.Value,
                endpoint.ClrInstanceId.Value);
            if (endpoint.Kind == FinalizerEventKind.BatchStart)
            {
                pairer.AddStart(key, endpoint.TimeUs, new FinalizerStartData());
            }
            else
            {
                pairer.AddStop(
                    key,
                    endpoint.TimeUs,
                    new FinalizerStopData(endpoint.Count));
            }
        }

        return pairer.Complete().Pairs.Count;
    }
}

internal sealed record TraceLogicalEventSummary(
    long TotalLogicalEvents,
    long EventsWithAttachedStacks,
    TraceStackwalkSummary Stackwalks,
    ProviderEventCountSummary ProviderEvents);

internal sealed record TraceCapabilityScanResult(
    TraceCapabilities Capabilities,
    TraceIdentityIndex Identity,
    TraceLogicalEventSummary LogicalEvents,
    TimeSpan Elapsed);

internal readonly record struct ClrContentionEndpoint(
    bool IsStart,
    int Pid,
    int Tid,
    long TimestampUs,
    CallStackIndex Stack);

internal readonly record struct CompletionEvidenceCountSet(
    long CompletedCount,
    long UnmatchedCount,
    long BoundaryCount);

internal readonly record struct ClrIntervalCapabilityEndpoint(
    ClrCapabilityEndpoint Kind,
    int Pid,
    long TimeUs,
    ushort? ClrInstanceId,
    int GcCount = 0,
    int Generation = 0,
    string Reason = "");

internal readonly record struct JitIntervalCapabilityEndpoint(
    bool IsStart,
    int Pid,
    long TimeUs,
    ushort? ClrInstanceId,
    long MethodId);

internal readonly record struct NetworkCapabilityKey(
    ProcessInstanceKey Process,
    ulong ConnectionId);

internal sealed class ProviderAccumulator(string provider)
{
    internal string Provider { get; } = provider;
    internal long EventCount { get; set; }
    internal long EventsWithCallStacks { get; set; }
}

internal sealed class ThreadEndpointCapabilityAccumulator
{
    internal bool HasAnySourceEvidence => SourceEventCount > 0;

    internal long ObservedEndpointEventCount { get; private set; }

    internal long RundownEndpointEventCount { get; private set; }

    internal long SourceEventCount => checked(
        ObservedEndpointEventCount + RundownEndpointEventCount);

    internal void Observe(ThreadLifecycleEventKind endpoint)
    {
        switch (endpoint)
        {
            case ThreadLifecycleEventKind.Start:
            case ThreadLifecycleEventKind.Stop:
                ObservedEndpointEventCount = checked(
                    ObservedEndpointEventCount + 1);
                break;
            case ThreadLifecycleEventKind.RundownStart:
            case ThreadLifecycleEventKind.RundownStop:
                RundownEndpointEventCount = checked(
                    RundownEndpointEventCount + 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint));
        }
    }
}

internal enum ClrCapabilityEndpoint
{
    GcStart,
    GcStop,
    GcSuspendEeStart,
    GcRestartEeStop,
    MethodJittingStarted,
    MethodLoadVerbose,
    ContentionStart,
    ContentionStop,
}

// Endpoint presence means an analyzer has source evidence; it does not imply that a
// complete interval can be formed or that the endpoint carried a usable stack.
internal sealed class ClrEndpointCapabilityAccumulator
{
    public bool HasClrGc { get; private set; }

    public long GcIntervalEndpointEventCount { get; private set; }

    public bool HasClrJit { get; private set; }

    public long JitIntervalEndpointEventCount { get; private set; }

    public bool HasClrContention { get; private set; }

    public void Observe(ClrCapabilityEndpoint endpoint)
    {
        switch (endpoint)
        {
            case ClrCapabilityEndpoint.GcStart:
            case ClrCapabilityEndpoint.GcStop:
            case ClrCapabilityEndpoint.GcSuspendEeStart:
            case ClrCapabilityEndpoint.GcRestartEeStop:
                HasClrGc = true;
                GcIntervalEndpointEventCount = checked(
                    GcIntervalEndpointEventCount + 1);
                break;
            case ClrCapabilityEndpoint.MethodJittingStarted:
            case ClrCapabilityEndpoint.MethodLoadVerbose:
                HasClrJit = true;
                JitIntervalEndpointEventCount = checked(
                    JitIntervalEndpointEventCount + 1);
                break;
            case ClrCapabilityEndpoint.ContentionStart:
            case ClrCapabilityEndpoint.ContentionStop:
                HasClrContention = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null);
        }
    }
}
