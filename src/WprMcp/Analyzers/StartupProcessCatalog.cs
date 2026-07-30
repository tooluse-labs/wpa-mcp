using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Core;

namespace WprMcp.Analyzers;

internal sealed record StartupProcessMetadata(
    ProcessLifetime Lifetime,
    int ParentPid,
    string Name,
    long LifetimeCpuUs,
    int LifetimeImageLoadCount);

internal sealed record StartupTraceProcessMetadata(
    ProcessInstanceKey Key,
    long EndUs,
    int ParentPid,
    string Name,
    long LifetimeCpuUs,
    Func<int> GetLifetimeImageLoadCount);

internal sealed record StartupProcessObservation(
    StartupProcessMetadata Metadata,
    StartupWindow Window)
{
    public ProcessInstanceKey Process => Metadata.Lifetime.Key;

    public long LifetimeWallUs => checked(
        Metadata.Lifetime.EndUs - Metadata.Lifetime.Key.StartUs);

    public double? LifetimeWaitRatio => Metadata.LifetimeCpuUs == 0
        ? null
        : LifetimeWallUs / (double)Metadata.LifetimeCpuUs;
}

internal sealed record StartupProcessExclusion(
    ProcessInstanceKey Process,
    string ProcessName,
    string Code,
    string Reason);

internal sealed record StartupProcessCatalogResult(
    IReadOnlyList<StartupProcessObservation> Eligible,
    int TotalEligibleCount,
    bool EligibleHasMore,
    IReadOnlyList<StartupProcessExclusion> Excluded,
    int TotalUnobservedStartCount,
    int TotalOtherExcludedCount,
    bool ExcludedHasMore,
    bool ExplicitNameTarget);

internal static class StartupProcessCatalog
{
    public static StartupProcessCatalogResult Build(
        IEnumerable<StartupProcessMetadata> processes,
        long startupWindowUs,
        long traceDurationUs,
        string? nameSubstring,
        int maxCollectionItems)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var builder = new CatalogBuilder(
            startupWindowUs,
            traceDurationUs,
            nameSubstring,
            maxCollectionItems);
        foreach (var metadata in processes)
            builder.Add(metadata);
        return builder.Build();
    }

    public static StartupProcessCatalogResult FromTrace(
        TraceLog trace,
        TraceIdentityIndex identities,
        long startupWindowUs,
        string? nameSubstring,
        int maxCollectionItems = Validation.MaxCollectionItems)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(identities);

        IEnumerable<StartupTraceProcessMetadata> ReadTraceMetadata()
        {
            foreach (var process in trace.Processes)
            {
                var key = new ProcessInstanceKey(
                    process.ProcessID,
                    TraceTime.FromMilliseconds(process.StartTimeRelativeMsec));
                var endUs = TraceTime.FromMilliseconds(process.EndTimeRelativeMsec);
                var capturedProcess = process;
                yield return new StartupTraceProcessMetadata(
                    key,
                    endUs,
                    process.ParentID,
                    process.Name ?? string.Empty,
                    TraceTime.FromMilliseconds(process.CPUMSec),
                    () => capturedProcess.LoadedModules.Count());
            }
        }

        bool HasTraceMetadata(ProcessInstanceKey key)
        {
            var process = trace.Processes.GetProcess(
                key.Pid,
                TraceMetadataLookupMilliseconds(key));
            return process is not null &&
                   TraceTime.FromMilliseconds(process.StartTimeRelativeMsec) == key.StartUs;
        }

        return BuildFromTraceMetadata(
            ReadTraceMetadata(),
            identities.Processes,
            startupWindowUs,
            identities.TraceEndUs,
            nameSubstring,
            maxCollectionItems,
            HasTraceMetadata);
    }

    // Query at the exclusive end of the floored microsecond bucket. TraceEvent returns
    // the last process whose raw start precedes the query, including starts in the bucket's
    // latter half that a midpoint probe would miss.
    internal static double TraceMetadataLookupMilliseconds(ProcessInstanceKey key) =>
        checked(key.StartUs + 1) / 1_000d;

    internal static StartupProcessCatalogResult BuildFromTraceMetadata(
        IEnumerable<StartupTraceProcessMetadata> traceProcesses,
        ProcessInstanceResolver processes,
        long startupWindowUs,
        long traceDurationUs,
        string? nameSubstring,
        int maxCollectionItems,
        Func<ProcessInstanceKey, bool> hasTraceMetadata)
    {
        ArgumentNullException.ThrowIfNull(traceProcesses);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(hasTraceMetadata);
        var builder = new CatalogBuilder(
            startupWindowUs,
            traceDurationUs,
            nameSubstring,
            maxCollectionItems);

        foreach (var traceProcess in traceProcesses)
        {
            var exactLifetimes = processes.FindExact(traceProcess.Key);
            if (exactLifetimes.Count > 1)
            {
                builder.AddAmbiguous(traceProcess.Key, traceProcess.Name);
                continue;
            }

            var lifetime = exactLifetimes.Count == 1
                ? exactLifetimes[0]
                : InferredLifetime(traceProcess, traceDurationUs);
            if (lifetime is null ||
                !builder.TryPrepareEligible(
                    lifetime,
                    traceProcess.Name,
                    out var window,
                    out var insertionIndex))
            {
                continue;
            }

            builder.RetainEligible(
                traceProcess,
                lifetime,
                window,
                insertionIndex);
        }

        ProcessInstanceKey? previousKey = null;
        foreach (var lifetime in processes.Lifetimes)
        {
            if (previousKey.HasValue && previousKey.Value == lifetime.Key)
                continue;
            previousKey = lifetime.Key;
            if (hasTraceMetadata(lifetime.Key))
                continue;

            var exactLifetimes = processes.FindExact(lifetime.Key);
            var name = $"Process({lifetime.Key.Pid})";
            if (exactLifetimes.Count > 1)
            {
                builder.AddAmbiguous(lifetime.Key, name);
                continue;
            }

            builder.Add(new StartupProcessMetadata(
                lifetime,
                ParentPid: 0,
                Name: name,
                LifetimeCpuUs: 0,
                LifetimeImageLoadCount: 0));
        }

        return builder.Build();
    }

    private static ProcessLifetime? InferredLifetime(
        StartupTraceProcessMetadata process,
        long traceDurationUs)
    {
        var endUs = process.EndUs;
        if (endUs <= process.Key.StartUs)
            endUs = traceDurationUs;
        return endUs > process.Key.StartUs
            ? new ProcessLifetime(
                process.Key,
                endUs,
                StartObserved: false,
                EndObserved: false)
            : null;
    }

    private sealed class CatalogBuilder
    {
        private readonly long _startupWindowUs;
        private readonly long _traceDurationUs;
        private readonly string? _nameSubstring;
        private readonly int _maxCollectionItems;
        private readonly bool _explicitNameTarget;
        private readonly List<PendingObservation> _eligible;
        private readonly List<StartupProcessExclusion> _excluded;
        private int _totalEligibleCount;
        private int _totalUnobservedStartCount;
        private int _totalOtherExcludedCount;

        public CatalogBuilder(
            long startupWindowUs,
            long traceDurationUs,
            string? nameSubstring,
            int maxCollectionItems)
        {
            Validation.RequireCollectionCount(maxCollectionItems);
            if (startupWindowUs <= 0)
                throw new ArgumentOutOfRangeException(nameof(startupWindowUs));
            if (traceDurationUs <= 0)
                throw new ArgumentOutOfRangeException(nameof(traceDurationUs));

            _startupWindowUs = startupWindowUs;
            _traceDurationUs = traceDurationUs;
            _nameSubstring = nameSubstring;
            _maxCollectionItems = maxCollectionItems;
            _explicitNameTarget = !string.IsNullOrEmpty(nameSubstring);
            _eligible = new List<PendingObservation>(maxCollectionItems);
            _excluded = new List<StartupProcessExclusion>(maxCollectionItems);
        }

        public void Add(StartupProcessMetadata metadata)
        {
            if (TryPrepareEligible(
                    metadata.Lifetime,
                    metadata.Name,
                    out var window,
                    out var insertionIndex))
            {
                RetainEligible(metadata, window, insertionIndex);
            }
        }

        public bool TryPrepareEligible(
            ProcessLifetime lifetime,
            string name,
            out StartupWindow window,
            out int insertionIndex)
        {
            window = null!;
            insertionIndex = -1;
            if (!MatchesName(name))
                return false;
            if (!lifetime.StartObserved)
            {
                _totalUnobservedStartCount = checked(_totalUnobservedStartCount + 1);
                RetainExclusion(new StartupProcessExclusion(
                    lifetime.Key,
                    name,
                    "startup_start_not_observed",
                    "The process instance has no observed ProcessStart event."));
                return false;
            }

            try
            {
                window = StartupWindow.Create(
                    lifetime,
                    _startupWindowUs,
                    _traceDurationUs);
            }
            catch (InvalidOperationException exception)
                when (exception.Message == "startup_window_empty")
            {
                _totalOtherExcludedCount = checked(_totalOtherExcludedCount + 1);
                RetainExclusion(new StartupProcessExclusion(
                    lifetime.Key,
                    name,
                    "startup_window_empty",
                    "The observed process start has no positive interval inside the trace."));
                return false;
            }

            _totalEligibleCount = checked(_totalEligibleCount + 1);
            insertionIndex = InsertionIndex(
                _eligible,
                lifetime.Key,
                observation => observation.Lifetime.Key);
            return _eligible.Count < _maxCollectionItems ||
                   insertionIndex < _maxCollectionItems;
        }

        public void RetainEligible(
            StartupProcessMetadata metadata,
            StartupWindow window,
            int insertionIndex)
            => RetainEligible(
                new PendingObservation(
                    metadata.Lifetime,
                    metadata.ParentPid,
                    metadata.Name,
                    metadata.LifetimeCpuUs,
                    metadata.LifetimeImageLoadCount,
                    GetLifetimeImageLoadCount: null,
                    window),
                insertionIndex);

        public void RetainEligible(
            StartupTraceProcessMetadata metadata,
            ProcessLifetime lifetime,
            StartupWindow window,
            int insertionIndex)
            => RetainEligible(
                new PendingObservation(
                    lifetime,
                    metadata.ParentPid,
                    metadata.Name,
                    metadata.LifetimeCpuUs,
                    LifetimeImageLoadCount: null,
                    metadata.GetLifetimeImageLoadCount,
                    window),
                insertionIndex);

        private void RetainEligible(
            PendingObservation observation,
            int insertionIndex)
        {
            if (_eligible.Count == _maxCollectionItems)
                _eligible.RemoveAt(_maxCollectionItems - 1);
            _eligible.Insert(insertionIndex, observation);
        }

        public void AddAmbiguous(ProcessInstanceKey key, string name)
        {
            if (!MatchesName(name))
                return;
            _totalOtherExcludedCount = checked(_totalOtherExcludedCount + 1);
            RetainExclusion(new StartupProcessExclusion(
                key,
                name,
                "startup_process_instance_ambiguous",
                "Multiple process lifetimes have the same exact process instance key."));
        }

        public StartupProcessCatalogResult Build()
        {
            var totalExcludedCount = checked(
                _totalUnobservedStartCount + _totalOtherExcludedCount);
            return new StartupProcessCatalogResult(
                _eligible.Select(observation => observation.Materialize()).ToArray(),
                _totalEligibleCount,
                EligibleHasMore: _totalEligibleCount > _eligible.Count,
                _excluded,
                _totalUnobservedStartCount,
                _totalOtherExcludedCount,
                ExcludedHasMore: totalExcludedCount > _excluded.Count,
                ExplicitNameTarget: _explicitNameTarget);
        }

        private bool MatchesName(string name) =>
            !_explicitNameTarget ||
            name.Contains(_nameSubstring!, StringComparison.OrdinalIgnoreCase);

        private void RetainExclusion(StartupProcessExclusion exclusion)
        {
            var insertionIndex = InsertionIndex(
                _excluded,
                exclusion.Process,
                item => item.Process);
            if (_excluded.Count >= _maxCollectionItems &&
                insertionIndex >= _maxCollectionItems)
            {
                return;
            }
            if (_excluded.Count == _maxCollectionItems)
                _excluded.RemoveAt(_maxCollectionItems - 1);
            _excluded.Insert(insertionIndex, exclusion);
        }

        private static int InsertionIndex<T>(
            IReadOnlyList<T> items,
            ProcessInstanceKey key,
            Func<T, ProcessInstanceKey> selectKey)
        {
            var low = 0;
            var high = items.Count;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                var comparison = Compare(selectKey(items[middle]), key);
                if (comparison <= 0)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private static int Compare(ProcessInstanceKey left, ProcessInstanceKey right)
        {
            var startComparison = left.StartUs.CompareTo(right.StartUs);
            return startComparison != 0
                ? startComparison
                : left.Pid.CompareTo(right.Pid);
        }

        private sealed record PendingObservation(
            ProcessLifetime Lifetime,
            int ParentPid,
            string Name,
            long LifetimeCpuUs,
            int? LifetimeImageLoadCount,
            Func<int>? GetLifetimeImageLoadCount,
            StartupWindow Window)
        {
            public StartupProcessObservation Materialize() =>
                new(
                    new StartupProcessMetadata(
                        Lifetime,
                        ParentPid,
                        Name,
                        LifetimeCpuUs,
                        GetLifetimeImageLoadCount?.Invoke() ??
                        LifetimeImageLoadCount!.Value),
                    Window);
        }
    }
}
