using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;

namespace WpaMcp.Analyzers;

internal enum ThreadLifecycleEventKind
{
    Start,
    Stop,
    RundownStart,
    RundownStop,
}

internal enum ProcessLifecycleEventKind
{
    Start,
    Stop,
    RundownStart,
    RundownStop,
}

internal readonly record struct ProcessLifecycleEvent(
    int Pid,
    long TimestampUs,
    ProcessLifecycleEventKind Kind);

internal readonly record struct ProcessLifetimeBackfill(
    int Pid,
    long StartUs,
    long EndUs);

internal readonly record struct ThreadLifecycleEvent(
    int Pid,
    int Tid,
    long TimestampUs,
    ThreadLifecycleEventKind Kind,
    bool Observed);

internal sealed record IdentityDiagnostic(
    string Code,
    int Pid,
    int? Tid,
    long TimestampUs,
    InstanceResolutionStatus ResolutionStatus,
    int CandidateCount);

internal sealed class TraceIdentityIndex
{
    private static readonly ConditionalWeakTable<TraceLog, IdentityCacheEntry> Cache = new();

    private TraceIdentityIndex(
        ProcessInstanceResolver processes,
        ThreadInstanceCatalog threads,
        long traceEndUs,
        IReadOnlyList<IdentityDiagnostic> diagnostics,
        long threadLifecycleEventCount,
        IReadOnlyDictionary<ProcessInstanceKey, long> threadLifecycleEventCountsByProcess,
        long observedThreadLifecycleEndpointEventCount,
        IReadOnlyDictionary<ProcessInstanceKey, long> observedThreadLifecycleEndpointEventCountsByProcess,
        long threadRundownEndpointEventCount,
        IReadOnlyDictionary<ProcessInstanceKey, long> threadRundownEndpointEventCountsByProcess)
    {
        Processes = processes;
        Threads = threads;
        TraceEndUs = traceEndUs;
        Diagnostics = diagnostics;
        ThreadLifecycleEventCount = threadLifecycleEventCount;
        ThreadLifecycleEventCountsByProcess = threadLifecycleEventCountsByProcess;
        ObservedThreadLifecycleEndpointEventCount =
            observedThreadLifecycleEndpointEventCount;
        ObservedThreadLifecycleEndpointEventCountsByProcess =
            observedThreadLifecycleEndpointEventCountsByProcess;
        ThreadRundownEndpointEventCount = threadRundownEndpointEventCount;
        ThreadRundownEndpointEventCountsByProcess =
            threadRundownEndpointEventCountsByProcess;
    }

    public ProcessInstanceResolver Processes { get; }

    public ThreadInstanceCatalog Threads { get; }

    public long TraceEndUs { get; }

    public IReadOnlyList<IdentityDiagnostic> Diagnostics { get; }

    public long ThreadLifecycleEventCount { get; }

    public IReadOnlyDictionary<ProcessInstanceKey, long>
        ThreadLifecycleEventCountsByProcess { get; }

    public long ObservedThreadLifecycleEndpointEventCount { get; }

    public IReadOnlyDictionary<ProcessInstanceKey, long>
        ObservedThreadLifecycleEndpointEventCountsByProcess { get; }

    public long ThreadRundownEndpointEventCount { get; }

    public IReadOnlyDictionary<ProcessInstanceKey, long>
        ThreadRundownEndpointEventCountsByProcess { get; }

    public static TraceIdentityIndex For(TraceLog trace) =>
        For(trace, TraceQueryExecutionContext.CurrentCancellationToken);

    internal static TraceIdentityIndex For(
        TraceLog trace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trace);
        cancellationToken.ThrowIfCancellationRequested();
        var entry = Cache.GetValue(trace, static _ => new IdentityCacheEntry());
        if (entry.TryGetProvider(cancellationToken, out var provider))
        {
            var identity = provider(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return identity;
        }
        return entry.GetOrBuild(
            () => BuildFromTrace(trace, cancellationToken),
            cancellationToken);
    }

    internal static TraceIdentityIndex For(
        TraceLog trace,
        Func<TraceLog, TraceIdentityIndex> builder)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(builder);
        var cancellationToken = TraceQueryExecutionContext.CurrentCancellationToken;
        return Cache.GetValue(trace, static _ => new IdentityCacheEntry())
            .GetOrBuild(() => builder(trace), cancellationToken);
    }

    internal static TraceIdentityIndex Register(
        TraceLog trace,
        TraceIdentityIndex identity)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(identity);
        return Cache.GetValue(trace, static _ => new IdentityCacheEntry())
            .Register(identity);
    }

    internal static void BindFactsProvider(
        TraceLog trace,
        Func<CancellationToken, TraceIdentityIndex> provider)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(provider);
        Cache.GetValue(trace, static _ => new IdentityCacheEntry())
            .BindProvider(provider);
    }

    internal static IReadOnlyList<ProcessLifetime> BuildProcessLifetimes(
        long traceEndUs,
        IReadOnlyList<ProcessLifecycleEvent> events,
        IReadOnlyList<ProcessLifetimeBackfill> backfill)
    {
        var active = new Dictionary<int, ActiveProcess>();
        var lifetimes = new List<ProcessLifetime>();

        foreach (var processEvent in AnalysisEvents.Enumerate(events)
                     .Select((value, index) => (value, index))
                     .OrderBy(item => item.value.TimestampUs)
                     .ThenBy(item => ProcessEventOrder(item.value.Kind))
                     .ThenBy(item => item.index)
                     .Select(item => item.value))
        {
            switch (processEvent.Kind)
            {
                case ProcessLifecycleEventKind.Start:
                    if (active.Remove(processEvent.Pid, out var reused))
                    {
                        lifetimes.Add(CloseProcess(reused, processEvent.TimestampUs, endObserved: false));
                    }
                    active[processEvent.Pid] = new ActiveProcess(
                        processEvent.Pid,
                        processEvent.TimestampUs,
                        StartObserved: true);
                    break;

                case ProcessLifecycleEventKind.RundownStart:
                    active.TryAdd(
                        processEvent.Pid,
                        new ActiveProcess(
                            processEvent.Pid,
                            StartUs: 0,
                            StartObserved: false));
                    break;

                case ProcessLifecycleEventKind.Stop:
                    if (active.Remove(processEvent.Pid, out var stopped))
                    {
                        lifetimes.Add(CloseProcess(stopped, processEvent.TimestampUs, endObserved: true));
                    }
                    else
                    {
                        lifetimes.Add(new ProcessLifetime(
                            new ProcessInstanceKey(processEvent.Pid, 0),
                            processEvent.TimestampUs,
                            StartObserved: false,
                            EndObserved: true));
                    }
                    break;

                case ProcessLifecycleEventKind.RundownStop:
                    if (active.TryGetValue(processEvent.Pid, out var rundownObserved))
                    {
                        // ProcessDCStop is end-of-capture rundown evidence, not a real
                        // process termination. Keep the lifetime active so a later
                        // ProcessStop (which can arrive after rundown enumeration) closes
                        // the same instance instead of inventing a second (pid, 0) row.
                        active[processEvent.Pid] = rundownObserved with
                        {
                            EndFromRundown = true,
                        };
                    }
                    else if (lifetimes.Any(lifetime => lifetime.Key.Pid == processEvent.Pid))
                    {
                        // A real stop is stronger endpoint evidence than ProcessDCStop.
                        // Without a later observed start, rundown cannot prove that a new
                        // PID generation exists and must not create an overlapping (pid, 0)
                        // lifetime. TraceLog backfill can still add a distinct later instance.
                    }
                    else
                    {
                        active[processEvent.Pid] = new ActiveProcess(
                            processEvent.Pid,
                            StartUs: 0,
                            StartObserved: false,
                            EndFromRundown: true);
                    }
                    break;
            }
        }

        foreach (var process in active.Values)
        {
            AnalysisEvents.ThrowIfCancellationRequested();
            lifetimes.Add(CloseProcess(
                process,
                traceEndUs,
                endObserved: false,
                endFromRundown: process.EndFromRundown));
        }

        foreach (var item in AnalysisEvents.Enumerate(backfill)
                     .OrderBy(item => item.Pid).ThenBy(item => item.StartUs))
        {
            ReconcileBackfill(lifetimes, item);
        }

        return lifetimes
            .Where(lifetime => lifetime.EndUs > lifetime.Key.StartUs)
            .OrderBy(lifetime => lifetime.Key.Pid)
            .ThenBy(lifetime => lifetime.Key.StartUs)
            .ToArray();
    }

    internal static TraceIdentityIndex BuildFromEvents(
        long traceEndUs,
        IReadOnlyList<ProcessLifetime> processes,
        IReadOnlyList<ThreadLifecycleEvent> threads)
    {
        var processResolver = new ProcessInstanceResolver(processes);
        var threadCatalog = new ThreadInstanceCatalog(processResolver.Lifetimes);
        var diagnostics = new List<IdentityDiagnostic>();
        var threadEventCountsByProcess = new Dictionary<ProcessInstanceKey, long>();
        var observedThreadEndpointCountsByProcess =
            new Dictionary<ProcessInstanceKey, long>();
        var threadRundownEndpointCountsByProcess =
            new Dictionary<ProcessInstanceKey, long>();

        foreach (var threadEvent in AnalysisEvents.Enumerate(threads)
                     .Select((value, index) => (value, index))
                     .OrderBy(item => item.value.TimestampUs)
                     .ThenBy(item => ThreadEventOrder(item.value.Kind))
                     .ThenBy(item => item.index)
                     .Select(item => item.value))
        {
            var resolution = ResolveOwningProcess(processResolver, threadEvent);
            if (resolution.Status != InstanceResolutionStatus.Resolved ||
                !resolution.Value.HasValue)
            {
                diagnostics.Add(new IdentityDiagnostic(
                    resolution.Status == InstanceResolutionStatus.Ambiguous
                        ? "thread_process_ambiguous"
                        : "thread_process_unresolved",
                    threadEvent.Pid,
                    threadEvent.Tid,
                    threadEvent.TimestampUs,
                    resolution.Status,
                    resolution.Candidates.Count));
                continue;
            }

            var process = resolution.Value.Value;
            threadEventCountsByProcess[process] = checked(
                threadEventCountsByProcess.GetValueOrDefault(process) + 1);
            var endpointCounts = threadEvent.Observed
                ? observedThreadEndpointCountsByProcess
                : threadRundownEndpointCountsByProcess;
            endpointCounts[process] = checked(
                endpointCounts.GetValueOrDefault(process) + 1);
            switch (threadEvent.Kind)
            {
                case ThreadLifecycleEventKind.Start:
                    threadCatalog.Start(
                        process,
                        threadEvent.Tid,
                        threadEvent.TimestampUs,
                        startObserved: threadEvent.Observed);
                    break;

                case ThreadLifecycleEventKind.RundownStart:
                    threadCatalog.StartIfAbsent(
                        process,
                        threadEvent.Tid,
                        process.StartUs,
                        startObserved: false);
                    break;

                case ThreadLifecycleEventKind.Stop:
                    threadCatalog.Stop(
                        process,
                        threadEvent.Tid,
                        threadEvent.TimestampUs,
                        endObserved: threadEvent.Observed);
                    break;

                case ThreadLifecycleEventKind.RundownStop:
                    threadCatalog.ObserveRundownStop(
                        process,
                        threadEvent.Tid,
                        ProcessEndUs(processResolver, process, traceEndUs),
                        process.StartUs);
                    break;
            }
        }

        AnalysisEvents.ThrowIfCancellationRequested();
        threadCatalog.Complete(traceEndUs);
        AnalysisEvents.ThrowIfCancellationRequested();
        return new TraceIdentityIndex(
            processResolver,
            threadCatalog,
            traceEndUs,
            Array.AsReadOnly(diagnostics.ToArray()),
            threads.Count,
            threadEventCountsByProcess.ToFrozenDictionary(),
            threads.LongCount(thread => thread.Observed),
            observedThreadEndpointCountsByProcess.ToFrozenDictionary(),
            threads.LongCount(thread => !thread.Observed),
            threadRundownEndpointCountsByProcess.ToFrozenDictionary());
    }

    private static long ProcessEndUs(
        ProcessInstanceResolver processResolver,
        ProcessInstanceKey process,
        long traceEndUs)
    {
        var lifetimes = processResolver.FindExact(process);
        return lifetimes.Count == 1
            ? TimeWindow.ClipEnd(traceEndUs, lifetimes[0].EndUs)
            : traceEndUs;
    }

    private static InstanceResolution<ProcessInstanceKey> ResolveOwningProcess(
        ProcessInstanceResolver processResolver,
        ThreadLifecycleEvent threadEvent)
    {
        if (threadEvent.Kind is not ThreadLifecycleEventKind.Stop and
            not ThreadLifecycleEventKind.RundownStop)
        {
            return processResolver.Resolve(
                threadEvent.Pid,
                threadEvent.TimestampUs,
                processStartUs: null);
        }

        return processResolver.ResolveAtEndpoint(
            threadEvent.Pid,
            threadEvent.TimestampUs);
    }

    private static TraceIdentityIndex BuildFromTrace(
        TraceLog trace,
        CancellationToken cancellationToken)
    {
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var processEvents = new List<ProcessLifecycleEvent>();
        var threadEvents = new List<ThreadLifecycleEvent>();
        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ProcessStart += data => processEvents.Add(new ProcessLifecycleEvent(
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                ProcessLifecycleEventKind.Start));
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
            kernel.ThreadStart += data => threadEvents.Add(new ThreadLifecycleEvent(
                data.ProcessID,
                data.ThreadID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                ThreadLifecycleEventKind.Start,
                Observed: true));
            kernel.ThreadStop += data => threadEvents.Add(new ThreadLifecycleEvent(
                data.ProcessID,
                data.ThreadID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                ThreadLifecycleEventKind.Stop,
                Observed: true));
            kernel.ThreadDCStart += data => threadEvents.Add(new ThreadLifecycleEvent(
                data.ProcessID,
                data.ThreadID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                ThreadLifecycleEventKind.RundownStart,
                Observed: false));
            kernel.ThreadDCStop += data => threadEvents.Add(new ThreadLifecycleEvent(
                data.ProcessID,
                data.ThreadID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                ThreadLifecycleEventKind.RundownStop,
                Observed: false));
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var backfill = AnalysisEvents.Enumerate(trace.Processes, cancellationToken)
            .Select(process => new ProcessLifetimeBackfill(
                process.ProcessID,
                TraceTime.FromMilliseconds(process.StartTimeRelativeMsec),
                TraceTime.FromMilliseconds(process.EndTimeRelativeMsec)))
            .ToArray();
        var processes = BuildProcessLifetimes(traceEndUs, processEvents, backfill);

        cancellationToken.ThrowIfCancellationRequested();

        return BuildFromEvents(traceEndUs, processes, threadEvents);
    }

    private static ProcessLifetime CloseProcess(
        ActiveProcess process,
        long endUs,
        bool endObserved,
        bool endFromRundown = false) =>
        new(
            new ProcessInstanceKey(process.Pid, process.StartUs),
            endUs,
            process.StartObserved,
            endObserved,
            endFromRundown);

    private static void ReconcileBackfill(
        List<ProcessLifetime> lifetimes,
        ProcessLifetimeBackfill backfill)
    {
        if (backfill.EndUs <= backfill.StartUs)
        {
            return;
        }

        var exactIndex = lifetimes.FindIndex(lifetime =>
            lifetime.Key.Pid == backfill.Pid && lifetime.Key.StartUs == backfill.StartUs);
        if (exactIndex >= 0)
        {
            var exact = lifetimes[exactIndex];
            lifetimes[exactIndex] = exact with
            {
                EndUs = exact.EndObserved || exact.EndFromRundown
                    ? exact.EndUs
                    : TimeWindow.ClipEnd(exact.EndUs, backfill.EndUs),
            };
            return;
        }

        var inferred = lifetimes
            .Select((lifetime, index) => (lifetime, index))
            .Where(item =>
                item.lifetime.Key.Pid == backfill.Pid &&
                !item.lifetime.StartObserved &&
                item.lifetime.Key.StartUs == 0 &&
                item.lifetime.EndUs == backfill.EndUs)
            .ToArray();
        if (inferred.Length == 1)
        {
            var existing = inferred[0].lifetime;
            lifetimes[inferred[0].index] = existing with
            {
                Key = new ProcessInstanceKey(backfill.Pid, backfill.StartUs),
                EndUs = existing.EndObserved ? existing.EndUs : backfill.EndUs,
            };
            return;
        }

        var overlapsExisting = lifetimes.Any(lifetime =>
            lifetime.Key.Pid == backfill.Pid &&
            lifetime.Key.StartUs < backfill.EndUs &&
            lifetime.EndUs > backfill.StartUs);
        if (!overlapsExisting)
        {
            lifetimes.Add(new ProcessLifetime(
                new ProcessInstanceKey(backfill.Pid, backfill.StartUs),
                backfill.EndUs,
                StartObserved: false,
                EndObserved: false));
        }
    }

    private static int ProcessEventOrder(ProcessLifecycleEventKind kind) => kind switch
    {
        ProcessLifecycleEventKind.Stop => 0,
        ProcessLifecycleEventKind.RundownStop => 1,
        ProcessLifecycleEventKind.Start => 2,
        ProcessLifecycleEventKind.RundownStart => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static int ThreadEventOrder(ThreadLifecycleEventKind kind) => kind switch
    {
        ThreadLifecycleEventKind.Stop => 0,
        ThreadLifecycleEventKind.RundownStop => 1,
        ThreadLifecycleEventKind.Start => 2,
        ThreadLifecycleEventKind.RundownStart => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private readonly record struct ActiveProcess(
        int Pid,
        long StartUs,
        bool StartObserved,
        bool EndFromRundown = false);

    private sealed class IdentityCacheEntry
    {
        private readonly object _gate = new();
        private TraceIdentityIndex? _identity;
        private Func<CancellationToken, TraceIdentityIndex>? _provider;

        internal bool TryGetProvider(
            CancellationToken cancellationToken,
            out Func<CancellationToken, TraceIdentityIndex> provider)
        {
            Enter(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_identity is not null)
                {
                    provider = _ => _identity;
                    return true;
                }
                provider = _provider!;
                return provider is not null;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        internal void BindProvider(
            Func<CancellationToken, TraceIdentityIndex> provider)
        {
            lock (_gate)
                _provider ??= provider;
        }

        internal TraceIdentityIndex GetOrBuild(
            Func<TraceIdentityIndex> builder,
            CancellationToken cancellationToken)
        {
            Enter(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_identity is not null)
                    return _identity;
                var built = builder();
                cancellationToken.ThrowIfCancellationRequested();
                _identity = built;
                return built;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        internal TraceIdentityIndex Register(TraceIdentityIndex identity)
        {
            lock (_gate)
                return _identity ??= identity;
        }

        private void Enter(CancellationToken cancellationToken)
        {
            while (!Monitor.TryEnter(_gate, millisecondsTimeout: 25))
                cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
