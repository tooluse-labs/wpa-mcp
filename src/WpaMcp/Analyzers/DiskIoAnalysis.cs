using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Stack-independent physical Disk I/O aggregation. Counts and bytes are completion-event
// metrics in the selected half-open window. Busy time is the union of the matched events'
// disk-service intervals, clipped to that same window and kept separate per physical disk.
public static class DiskIoAnalysis
{
    private const int MaxTimelineBuckets = 512;

    public static DiskIoAnalysisResponse Analyze(
        TraceLog trace,
        int top,
        int? pid,
        long startUs,
        long endUs,
        long bucketUs,
        bool summaryOnly,
        long? processStartUs = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (bucketUs < 0)
            throw ToolFailureCaptureContext.Capture(
                new ArgumentOutOfRangeException(nameof(bucketUs), "must be non-negative"));

        var window = new TimeWindow(startUs, endUs);
        var identities = TraceIdentityIndex.For(trace);
        var scope = ProcessAnalysisScope.Resolve(window, pid, processStartUs, identities);
        var resolver = new FileObjectResolver();
        var total = new IoAccumulator();
        var totalMappings = new FileMappingStateAccumulator();
        var processes = new Dictionary<ProcessInstanceKey, IoAccumulator>();
        var files = new Dictionary<string, FileAccumulator>();
        var disks = new Dictionary<int, DiskAccumulator>();
        var timeline = TimelineAccumulator.Create(window, bucketUs, !summaryOnly);
        long traceEventCount = 0;
        long matchedEventCount = 0;
        long processIdentityUnresolvedEventCount = 0;

        void Handle(DiskIOTraceData data, bool isRead)
        {
            traceEventCount++;
            var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
            if (!scope.MatchesEvent(identities, data.ProcessID, timestampUs))
                return;

            matchedEventCount++;
            var bytes = (long)data.TransferSize;
            var serviceTimeUs = ToServiceTimeUs(data.DiskServiceTimeMSec);
            total.Add(isRead, bytes, serviceTimeUs);
            timeline.Add(timestampUs, isRead, bytes, serviceTimeUs);

            var disk = GetOrAdd(disks, data.DiskNumber, static () => new DiskAccumulator());
            disk.Add(isRead, bytes, serviceTimeUs, timestampUs, window);

            var mapping = resolver.ResolveDetailedAt(
                fileObject: 0,
                fileKey: data.FileKey,
                timestampUs,
                data.EventIndex);
            totalMappings.Add(mapping.MappingState);
            if (!summaryOnly)
            {
                GetOrAdd(files, mapping.File, static () => new FileAccumulator())
                    .Add(isRead, bytes, serviceTimeUs, mapping.MappingState);
            }

            if (scope.TryResolveEventProcess(
                    identities, data.ProcessID, timestampUs, out var process))
            {
                if (!summaryOnly)
                {
                    GetOrAdd(processes, process, static () => new IoAccumulator())
                        .Add(isRead, bytes, serviceTimeUs);
                }
            }
            else
            {
                processIdentityUnresolvedEventCount++;
            }
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            resolver.Subscribe(kernel);
            kernel.DiskIORead += data => Handle(data, isRead: true);
            kernel.DiskIOWrite += data => Handle(data, isRead: false);
        });

        var mergedIntervals = disks.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.MergeServiceIntervals());
        var measurableDisks = disks.Values.Count(disk => disk.Stats.ServiceTimeSampleCount > 0);
        var busyUsAcrossDisks = mergedIntervals.Values.Sum(SumDuration);
        var maxDiskBusyUs = mergedIntervals.Values
            .Select(SumDuration)
            .DefaultIfEmpty(0)
            .Max();
        var averageDiskBusyPct = measurableDisks == 0
            ? (double?)null
            : Percent(busyUsAcrossDisks, (double)window.DurationUs * measurableDisks);
        var maxDiskBusyPct = measurableDisks == 0
            ? (double?)null
            : Percent(maxDiskBusyUs, window.DurationUs);

        var processRows = summaryOnly
            ? []
            : processes
                .OrderByDescending(pair => pair.Value.TotalBytes)
                .ThenBy(pair => pair.Key.Pid)
                .ThenBy(pair => pair.Key.StartUs)
                .Take(top)
                .Select(pair => new DiskIoProcessRow(
                    pair.Key,
                    ProcessName(trace, pair.Key),
                    pair.Value.Snapshot()))
                .ToList();
        var fileRows = summaryOnly
            ? []
            : files
                .OrderByDescending(pair => pair.Value.Stats.TotalBytes)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(top)
                .Select(pair => new DiskIoFileRow(
                    pair.Key,
                    pair.Value.Stats.Snapshot(),
                    pair.Value.MappingStates.AggregateState,
                    pair.Value.MappingStates.Snapshot()))
                .ToList();
        var diskRows = summaryOnly
            ? []
            : disks
                .OrderBy(pair => pair.Key)
                .Select(pair =>
                {
                    var busyUs = SumDuration(mergedIntervals[pair.Key]);
                    return new DiskIoDiskRow(
                        pair.Key,
                        pair.Value.Stats.Snapshot(),
                        busyUs,
                        pair.Value.Stats.ServiceTimeSampleCount == 0
                            ? null
                            : Percent(busyUs, window.DurationUs));
                })
                .ToList();
        var timelineRows = total.TotalCount == 0
            ? []
            : timeline.Build(mergedIntervals, measurableDisks);

        var capabilityStatus = !scope.IsResolved
            ? "unknown"
            : matchedEventCount > 0
                ? "observed"
                : traceEventCount == 0 ? "not_observed" : "unknown";
        var noDataReason = FileIoAnalysis.ClassifyNoData(
            scope, traceEventCount, matchedEventCount);
        var warnings = BuildWarnings(
            scope,
            pid,
            processStartUs,
            noDataReason,
            processIdentityUnresolvedEventCount,
            total.ServiceTimeSampleCount,
            matchedEventCount,
            timeline,
            summaryOnly,
            bucketUs);

        return new DiskIoAnalysisResponse(
            Summary: new DiskIoSummary(
                StartUs: window.StartUs,
                EndUs: window.EndUs,
                WindowDurationUs: window.DurationUs,
                Metrics: total.Snapshot(),
                ObservedDiskCount: disks.Count,
                BusyTimeMeasuredDiskCount: measurableDisks,
                BusyUsAcrossDisks: busyUsAcrossDisks,
                AverageDiskBusyPct: averageDiskBusyPct,
                MaxDiskBusyPct: maxDiskBusyPct,
                ProcessIdentityUnresolvedEventCount: processIdentityUnresolvedEventCount,
                FileMappingState: totalMappings.AggregateState,
                FileMappingStateCounts: totalMappings.Snapshot()),
            TopProcesses: processRows,
            TopFiles: fileRows,
            Disks: diskRows,
            Timeline: summaryOnly ? [] : timelineRows,
            RequestedBucketUs: bucketUs,
            EffectiveBucketUs: summaryOnly ? 0 : timeline.EffectiveBucketUs,
            BucketWidthAdjusted: !summaryOnly && timeline.BucketWidthAdjusted,
            SummaryOnly: summaryOnly,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses,
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: capabilityStatus,
            MatchedEventCount: matchedEventCount,
            NoDataReason: noDataReason,
            Warnings: warnings);
    }

    private static IReadOnlyList<string> BuildWarnings(
        ProcessAnalysisScope scope,
        int? pid,
        long? processStartUs,
        string? noDataReason,
        long unresolvedProcessEvents,
        long serviceTimeSamples,
        long matchedEvents,
        TimelineAccumulator timeline,
        bool summaryOnly,
        long requestedBucketUs)
    {
        var warnings = new List<string>();
        if (scope.ScopeMode == "pid_aggregate")
        {
            warnings.Add(
                $"pid_aggregate: PID {pid} matched {scope.IncludedProcesses.Count} process lifetimes in the requested window; totals combine those instances. Specify processStartUs for one lifetime.");
        }

        switch (noDataReason)
        {
            case ProcessAnalysisScope.AmbiguousStatus:
                warnings.Add(ProcessAnalysisScope.ResolutionFailureWarning(
                    ProcessAnalysisScope.AmbiguousStatus));
                break;
            case "scope_not_found":
                warnings.Add(
                    $"scope_not_found: no process lifetime for PID {pid}" +
                    (processStartUs.HasValue
                        ? $" with processStartUs={processStartUs.Value}"
                        : string.Empty) +
                    " intersects the requested window.");
                break;
            case "event_class_not_observed":
                warnings.Add(
                    "event_class_not_observed: " +
                    WarningBuilder.MissingKeyword("DiskIO Read/Write", "DiskIO"));
                break;
            case "no_events_in_scope":
                warnings.Add(
                    "no_events_in_scope: DiskIO Read/Write events were observed elsewhere in the trace, but none matched the selected process lifetimes and half-open window.");
                break;
        }

        if (unresolvedProcessEvents > 0)
        {
            warnings.Add(
                $"process_identity_unresolved: {unresolvedProcessEvents} matched DiskIO events are included in summary/file/disk totals but excluded from process rows because no unique process lifetime could be resolved.");
        }
        if (serviceTimeSamples < matchedEvents)
        {
            warnings.Add(
                $"service_time_unavailable: {matchedEvents - serviceTimeSamples} matched DiskIO events had no finite non-negative DiskServiceTime; byte/count totals include them, service-time and busy-time metrics do not.");
        }
        if (timeline.BucketWidthAdjusted)
        {
            warnings.Add(
                $"timeline_bucket_adjusted: requested bucketUs={requestedBucketUs} was widened to {timeline.EffectiveBucketUs} so the complete timeline fits within {MaxTimelineBuckets} buckets.");
        }
        if (summaryOnly && requestedBucketUs > 0)
            warnings.Add("summary_only: bucketUs was ignored and timeline rows were omitted.");

        return warnings;
    }

    private static long? ToServiceTimeUs(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
            return null;
        return TraceTime.FromMilliseconds(milliseconds);
    }

    private static string ProcessName(TraceLog trace, ProcessInstanceKey process) =>
        AnalysisEvents.Enumerate(trace.Processes)
            .Where(candidate => candidate.ProcessID == process.Pid)
            .FirstOrDefault(candidate =>
                TraceTime.FromMilliseconds(candidate.StartTimeRelativeMsec) == process.StartUs)
            ?.Name ?? $"Process({process.Pid})";

    private static TValue GetOrAdd<TKey, TValue>(
        IDictionary<TKey, TValue> values,
        TKey key,
        Func<TValue> factory)
        where TKey : notnull
    {
        if (!values.TryGetValue(key, out var value))
        {
            value = factory();
            values.Add(key, value);
        }
        return value;
    }

    private static double Percent(long numerator, double denominator) =>
        denominator <= 0 ? 0 : numerator * 100.0 / denominator;

    private static long SumDuration(IReadOnlyList<ServiceInterval> intervals) =>
        intervals.Aggregate(
            0L,
            static (total, interval) => checked(total + interval.EndUs - interval.StartUs));

    private sealed class IoAccumulator
    {
        private readonly List<long> _serviceTimes = [];

        public long ReadCount { get; private set; }
        public long ReadBytes { get; private set; }
        public long WriteCount { get; private set; }
        public long WriteBytes { get; private set; }
        public long ServiceTimeTotalUs { get; private set; }
        public long? MaxServiceTimeUs { get; private set; }
        public long TotalCount => checked(ReadCount + WriteCount);
        public long TotalBytes => checked(ReadBytes + WriteBytes);
        public long ServiceTimeSampleCount => _serviceTimes.Count;

        public void Add(bool isRead, long bytes, long? serviceTimeUs)
        {
            checked
            {
                if (isRead)
                {
                    ReadCount++;
                    ReadBytes += bytes;
                }
                else
                {
                    WriteCount++;
                    WriteBytes += bytes;
                }

                if (serviceTimeUs.HasValue)
                {
                    ServiceTimeTotalUs += serviceTimeUs.Value;
                    _serviceTimes.Add(serviceTimeUs.Value);
                    MaxServiceTimeUs = !MaxServiceTimeUs.HasValue
                        ? serviceTimeUs.Value
                        : Math.Max(MaxServiceTimeUs.Value, serviceTimeUs.Value);
                }
            }
        }

        public DiskIoMetrics Snapshot()
        {
            var average = ServiceTimeSampleCount == 0
                ? (long?)null
                : (long)Math.Round(
                    ServiceTimeTotalUs / (double)ServiceTimeSampleCount,
                    MidpointRounding.AwayFromZero);
            return new DiskIoMetrics(
                ReadCount,
                ReadBytes,
                WriteCount,
                WriteBytes,
                TotalCount,
                TotalBytes,
                ServiceTimeSampleCount,
                average,
                Percentile95(_serviceTimes),
                MaxServiceTimeUs);
        }

        private static long? Percentile95(List<long> values)
        {
            if (values.Count == 0)
                return null;
            values.Sort();
            var rank = ((long)values.Count * 95 + 99) / 100;
            return values[checked((int)rank - 1)];
        }
    }

    private sealed class FileAccumulator
    {
        public IoAccumulator Stats { get; } = new();
        public FileMappingStateAccumulator MappingStates { get; } = new();

        public void Add(bool isRead, long bytes, long? serviceTimeUs, string mappingState)
        {
            Stats.Add(isRead, bytes, serviceTimeUs);
            MappingStates.Add(mappingState);
        }
    }

    private sealed class DiskAccumulator
    {
        private readonly List<ServiceInterval> _serviceIntervals = [];

        public IoAccumulator Stats { get; } = new();

        public void Add(
            bool isRead,
            long bytes,
            long? serviceTimeUs,
            long completionUs,
            TimeWindow window)
        {
            Stats.Add(isRead, bytes, serviceTimeUs);
            if (serviceTimeUs is not > 0)
                return;

            var rawStartUs = serviceTimeUs.Value >= completionUs
                ? 0
                : completionUs - serviceTimeUs.Value;
            var startUs = TimeWindow.ClipStart(rawStartUs, window.StartUs);
            var endUs = TimeWindow.ClipEnd(completionUs, window.EndUs);
            if (startUs < endUs)
                _serviceIntervals.Add(new ServiceInterval(startUs, endUs));
        }

        public IReadOnlyList<ServiceInterval> MergeServiceIntervals()
        {
            if (_serviceIntervals.Count == 0)
                return [];

            var ordered = _serviceIntervals
                .OrderBy(interval => interval.StartUs)
                .ThenBy(interval => interval.EndUs)
                .ToArray();
            var merged = new List<ServiceInterval>();
            var current = ordered[0];
            foreach (var next in ordered.Skip(1))
            {
                if (next.StartUs <= current.EndUs)
                {
                    current = current with { EndUs = Math.Max(current.EndUs, next.EndUs) };
                    continue;
                }
                merged.Add(current);
                current = next;
            }
            merged.Add(current);
            return merged;
        }
    }

    private sealed class TimelineAccumulator
    {
        private readonly TimeWindow _window;
        private readonly IoAccumulator[] _buckets;

        private TimelineAccumulator(
            TimeWindow window,
            long requestedBucketUs,
            long effectiveBucketUs,
            IoAccumulator[] buckets)
        {
            _window = window;
            RequestedBucketUs = requestedBucketUs;
            EffectiveBucketUs = effectiveBucketUs;
            _buckets = buckets;
        }

        public long RequestedBucketUs { get; }
        public long EffectiveBucketUs { get; }
        public bool BucketWidthAdjusted =>
            RequestedBucketUs > 0 && EffectiveBucketUs > RequestedBucketUs;

        public static TimelineAccumulator Create(
            TimeWindow window,
            long requestedBucketUs,
            bool enabled)
        {
            if (!enabled || requestedBucketUs == 0)
                return new TimelineAccumulator(window, requestedBucketUs, 0, []);

            var minimumWidthUs = DivideRoundUp(window.DurationUs, MaxTimelineBuckets);
            var effectiveWidthUs = Math.Max(requestedBucketUs, minimumWidthUs);
            var bucketCount = checked((int)DivideRoundUp(window.DurationUs, effectiveWidthUs));
            return new TimelineAccumulator(
                window,
                requestedBucketUs,
                effectiveWidthUs,
                Enumerable.Range(0, bucketCount)
                    .Select(_ => new IoAccumulator())
                    .ToArray());
        }

        public void Add(long timestampUs, bool isRead, long bytes, long? serviceTimeUs)
        {
            if (_buckets.Length == 0)
                return;
            var index = checked((int)((timestampUs - _window.StartUs) / EffectiveBucketUs));
            _buckets[Math.Min(index, _buckets.Length - 1)]
                .Add(isRead, bytes, serviceTimeUs);
        }

        public IReadOnlyList<DiskIoTimelineBucket> Build(
            IReadOnlyDictionary<int, IReadOnlyList<ServiceInterval>> intervalsByDisk,
            int measurableDiskCount)
        {
            if (_buckets.Length == 0)
                return [];

            var busyAcrossDisks = new long[_buckets.Length];
            var maxDiskBusy = new long[_buckets.Length];
            foreach (var intervals in intervalsByDisk.Values)
            {
                var diskBusy = new long[_buckets.Length];
                foreach (var interval in intervals)
                    AddInterval(diskBusy, interval);
                for (var index = 0; index < diskBusy.Length; index++)
                {
                    busyAcrossDisks[index] = checked(
                        busyAcrossDisks[index] + diskBusy[index]);
                    maxDiskBusy[index] = Math.Max(maxDiskBusy[index], diskBusy[index]);
                }
            }

            var rows = new List<DiskIoTimelineBucket>(_buckets.Length);
            for (var index = 0; index < _buckets.Length; index++)
            {
                var startUs = checked(_window.StartUs + index * EffectiveBucketUs);
                var endUs = TimeWindow.ClipEnd(
                    checked(startUs + EffectiveBucketUs),
                    _window.EndUs);
                var durationUs = checked(endUs - startUs);
                rows.Add(new DiskIoTimelineBucket(
                    startUs,
                    endUs,
                    durationUs,
                    _buckets[index].Snapshot(),
                    busyAcrossDisks[index],
                    measurableDiskCount == 0
                        ? null
                        : Percent(
                            busyAcrossDisks[index],
                            (double)durationUs * measurableDiskCount),
                    measurableDiskCount == 0
                        ? null
                        : Percent(maxDiskBusy[index], durationUs)));
            }
            return rows;
        }

        private void AddInterval(long[] buckets, ServiceInterval interval)
        {
            var first = checked((int)((interval.StartUs - _window.StartUs) / EffectiveBucketUs));
            var last = checked((int)((interval.EndUs - 1 - _window.StartUs) / EffectiveBucketUs));
            for (var index = first; index <= last; index++)
            {
                var bucketStartUs = checked(_window.StartUs + index * EffectiveBucketUs);
                var bucketEndUs = TimeWindow.ClipEnd(
                    checked(bucketStartUs + EffectiveBucketUs),
                    _window.EndUs);
                buckets[index] = checked(
                    buckets[index] +
                    new TimeWindow(bucketStartUs, bucketEndUs)
                        .IntersectDurationUs(interval.StartUs, interval.EndUs));
            }
        }

        private static long DivideRoundUp(long value, long divisor) =>
            checked(value / divisor + (value % divisor == 0 ? 0 : 1));
    }

    private readonly record struct ServiceInterval(long StartUs, long EndUs);
}
