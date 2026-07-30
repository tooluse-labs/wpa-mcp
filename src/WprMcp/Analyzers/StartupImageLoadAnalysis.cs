using System.Collections.ObjectModel;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Analyzers;

internal readonly record struct StartupImageLoadEvent(
    ProcessInstanceKey Process,
    long TimeUs,
    string FileName,
    long ImageSize);

internal sealed record StartupImageLoadBucket(
    long TotalAvailable,
    IReadOnlyList<ImageLoadRow> FirstLoads,
    bool HasMore);

internal sealed record StartupImageLoadResult(
    IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> ByProcess,
    long UnresolvedProcessInstanceCount,
    long AmbiguousProcessInstanceCount);

internal sealed class StartupImageLoadAccumulator
{
    private readonly int _maxRowsPerProcess;
    private readonly Dictionary<ProcessInstanceKey, ProcessBucket> _byProcess;
    private bool _completed;

    public StartupImageLoadAccumulator(
        IReadOnlyList<StartupProcessObservation> processes,
        int maxRowsPerProcess)
    {
        ArgumentNullException.ThrowIfNull(processes);
        _maxRowsPerProcess = Validation.RequireTop(maxRowsPerProcess);
        _byProcess = processes.ToDictionary(
            process => process.Process,
            process => new ProcessBucket(process.Window.Bounds));
    }

    public void OnImageLoad(in StartupImageLoadEvent imageLoad)
    {
        EnsureMutable();
        if (!_byProcess.TryGetValue(imageLoad.Process, out var bucket) ||
            !bucket.Window.ContainsPoint(imageLoad.TimeUs))
        {
            return;
        }

        bucket.TotalAvailable = checked(bucket.TotalAvailable + 1);
        var insertionIndex = FindInsertionIndex(bucket.FirstLoads, imageLoad);
        if (insertionIndex >= _maxRowsPerProcess)
            return;

        bucket.FirstLoads.Insert(insertionIndex, imageLoad);
        if (bucket.FirstLoads.Count > _maxRowsPerProcess)
            bucket.FirstLoads.RemoveAt(bucket.FirstLoads.Count - 1);
    }

    public IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> Complete()
    {
        EnsureMutable();
        _completed = true;
        var result = _byProcess.ToDictionary(
            item => item.Key,
            item => CompleteBucket(item.Key, item.Value));
        return new ReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket>(result);
    }

    private static int FindInsertionIndex(
        IReadOnlyList<StartupImageLoadEvent> events,
        StartupImageLoadEvent candidate)
    {
        var low = 0;
        var high = events.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = Compare(events[middle], candidate);
            if (comparison <= 0)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static int Compare(
        StartupImageLoadEvent left,
        StartupImageLoadEvent right)
    {
        var timeComparison = left.TimeUs.CompareTo(right.TimeUs);
        return timeComparison != 0
            ? timeComparison
            : StringComparer.Ordinal.Compare(left.FileName, right.FileName);
    }

    private static StartupImageLoadBucket CompleteBucket(
        ProcessInstanceKey process,
        ProcessBucket bucket)
    {
        var rows = new List<ImageLoadRow>(bucket.FirstLoads.Count);
        long? previousTimeUs = null;
        foreach (var imageLoad in bucket.FirstLoads)
        {
            rows.Add(new ImageLoadRow(
                imageLoad.TimeUs,
                checked(imageLoad.TimeUs - process.StartUs),
                imageLoad.FileName,
                imageLoad.ImageSize,
                previousTimeUs.HasValue
                    ? checked(imageLoad.TimeUs - previousTimeUs.Value)
                    : null));
            previousTimeUs = imageLoad.TimeUs;
        }

        return new StartupImageLoadBucket(
            bucket.TotalAvailable,
            rows.AsReadOnly(),
            HasMore: bucket.TotalAvailable > rows.Count);
    }

    private void EnsureMutable()
    {
        if (_completed)
            throw new InvalidOperationException("Startup image-load analysis is complete.");
    }

    private sealed class ProcessBucket(TimeWindow window)
    {
        public TimeWindow Window { get; } = window;
        public long TotalAvailable { get; set; }
        public List<StartupImageLoadEvent> FirstLoads { get; } = new();
    }
}

internal static class StartupImageLoadAnalysis
{
    public static StartupImageLoadResult Collect(
        TraceLog trace,
        TraceIdentityIndex identities,
        IReadOnlyList<StartupProcessObservation> processes,
        int maxRowsPerProcess)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(processes);
        Validation.RequireTop(maxRowsPerProcess);

        var accumulator = new StartupImageLoadAccumulator(
            processes, maxRowsPerProcess);
        long unresolvedCount = 0;
        long ambiguousCount = 0;

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ImageLoad += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(
                    data.TimeStampRelativeMSec);
                var resolution = identities.Processes.Resolve(
                    data.ProcessID, timestampUs, processStartUs: null);
                if (resolution.Status == InstanceResolutionStatus.Unresolved)
                {
                    unresolvedCount = checked(unresolvedCount + 1);
                    return;
                }
                if (resolution.Status == InstanceResolutionStatus.Ambiguous)
                {
                    ambiguousCount = checked(ambiguousCount + 1);
                    return;
                }
                if (!resolution.Value.HasValue)
                    return;

                accumulator.OnImageLoad(new StartupImageLoadEvent(
                    resolution.Value.Value,
                    timestampUs,
                    data.FileName ?? "<unknown>",
                    data.ImageSize));
            };
        });

        return new StartupImageLoadResult(
            accumulator.Complete(),
            unresolvedCount,
            ambiguousCount);
    }

    internal static IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> Project(
        IEnumerable<StartupImageLoadEvent> events,
        IReadOnlyList<StartupProcessObservation> processes,
        int maxRowsPerProcess)
    {
        ArgumentNullException.ThrowIfNull(events);
        var accumulator = new StartupImageLoadAccumulator(
            processes, maxRowsPerProcess);
        foreach (var imageLoad in events)
            accumulator.OnImageLoad(imageLoad);
        return accumulator.Complete();
    }
}
