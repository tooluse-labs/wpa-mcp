using WpaMcp.Core;

namespace WpaMcp.Analyzers;

internal readonly record struct ClrGcKey(
    ProcessInstanceKey Process,
    ushort ClrInstanceId,
    int GcCount);

internal readonly record struct ClrPauseKey(
    ProcessInstanceKey Process,
    ushort ClrInstanceId);

internal readonly record struct GcStartData(int Generation, string Reason);

internal readonly record struct GcStopData;

internal readonly record struct SuspendStartData;

internal readonly record struct RestartStopData;

internal sealed record GcPauseInterval(
    ClrPauseKey Key,
    long StartUs,
    long EndUs,
    long FullDurationUs);

internal sealed record GcWallWithPauses(
    ClrGcKey Key,
    long StartUs,
    long EndUs,
    long FullDurationUs,
    int Generation,
    string Reason,
    IReadOnlyList<GcPauseInterval> Pauses);

internal sealed record GcIncompleteEvidence(
    string Code,
    ProcessInstanceKey Process,
    long TimestampUs,
    string EventKind);

internal readonly record struct GcIntervalAnomaly(
    ProcessInstanceKey Process,
    long StartUs,
    long EndUs);

internal sealed record GcIntervalSet(
    IReadOnlyList<GcWallWithPauses> Gcs,
    IReadOnlyList<GcPauseInterval> OrphanPauses,
    IReadOnlyList<GcIncompleteEvidence> IncompleteEvidence,
    int UnmatchedGcStartCount,
    int UnmatchedGcStopCount,
    int UnmatchedSuspendStartCount,
    int UnmatchedRestartStopCount,
    int InvalidIntervalCount,
    IReadOnlyList<GcIntervalAnomaly>? UnmatchedGcIntervals = null,
    IReadOnlyList<GcIntervalAnomaly>? UnmatchedPauseIntervals = null,
    IReadOnlyList<GcIntervalAnomaly>? InvalidIntervals = null);

internal sealed class GcIntervalAccumulator
{
    private readonly IntervalPairAccumulator<ClrGcKey, GcStartData, GcStopData> _gcs = new();
    private readonly IntervalPairAccumulator<ClrPauseKey, SuspendStartData, RestartStopData> _pauses = new();
    private readonly List<GcIncompleteEvidence> _incompleteEvidence = new();
    private GcIntervalSet? _completedResult;

    public void AddGcStart(
        ProcessInstanceKey process,
        ushort? clrInstanceId,
        int gcCount,
        long timestampUs,
        int generation,
        string reason)
    {
        ThrowIfCompleted();
        if (!clrInstanceId.HasValue)
        {
            AddMissingIdentity(process, timestampUs, "gc_start");
            return;
        }

        _gcs.AddStart(
            new ClrGcKey(process, clrInstanceId.Value, gcCount),
            timestampUs,
            new GcStartData(generation, reason));
    }

    public void AddGcStop(
        ProcessInstanceKey process,
        ushort? clrInstanceId,
        int gcCount,
        long timestampUs)
    {
        ThrowIfCompleted();
        if (!clrInstanceId.HasValue)
        {
            AddMissingIdentity(process, timestampUs, "gc_stop");
            return;
        }

        _gcs.AddStop(
            new ClrGcKey(process, clrInstanceId.Value, gcCount),
            timestampUs,
            new GcStopData());
    }

    public void AddSuspendStart(
        ProcessInstanceKey process,
        ushort? clrInstanceId,
        long timestampUs)
    {
        ThrowIfCompleted();
        if (!clrInstanceId.HasValue)
        {
            AddMissingIdentity(process, timestampUs, "suspend_start");
            return;
        }

        _pauses.AddStart(
            new ClrPauseKey(process, clrInstanceId.Value),
            timestampUs,
            new SuspendStartData());
    }

    public void AddRestartStop(
        ProcessInstanceKey process,
        ushort? clrInstanceId,
        long timestampUs)
    {
        ThrowIfCompleted();
        if (!clrInstanceId.HasValue)
        {
            AddMissingIdentity(process, timestampUs, "restart_stop");
            return;
        }

        _pauses.AddStop(
            new ClrPauseKey(process, clrInstanceId.Value),
            timestampUs,
            new RestartStopData());
    }

    public GcIntervalSet Complete()
    {
        if (_completedResult is not null)
            return _completedResult;

        var gcPairs = _gcs.Complete();
        var pausePairs = _pauses.Complete();

        var gcs = gcPairs.Pairs
            .Select(pair => new GcWallWithPauses(
                pair.Key,
                pair.StartUs,
                pair.EndUs,
                pair.FullDurationUs,
                pair.StartData.Generation,
                pair.StartData.Reason,
                Array.Empty<GcPauseInterval>()))
            .OrderBy(gc => gc.Key.Process.Pid)
            .ThenBy(gc => gc.Key.Process.StartUs)
            .ThenBy(gc => gc.Key.ClrInstanceId)
            .ThenBy(gc => gc.StartUs)
            .ThenBy(gc => gc.EndUs)
            .ThenBy(gc => gc.Key.GcCount)
            .ToList();

        var pauses = pausePairs.Pairs
            .Select(pair => new GcPauseInterval(
                pair.Key,
                pair.StartUs,
                pair.EndUs,
                pair.FullDurationUs))
            .OrderBy(pause => pause.Key.Process.Pid)
            .ThenBy(pause => pause.Key.Process.StartUs)
            .ThenBy(pause => pause.Key.ClrInstanceId)
            .ThenBy(pause => pause.StartUs)
            .ThenBy(pause => pause.EndUs)
            .ToList();

        var associatedPauses = gcs
            .Select(_ => new List<GcPauseInterval>())
            .ToArray();
        var orphanPauses = new List<GcPauseInterval>();

        foreach (var pause in pauses)
        {
            var owner = SelectOwner(pause, gcs);
            if (owner is null)
            {
                orphanPauses.Add(pause);
                continue;
            }

            var ownerIndex = gcs.FindIndex(gc => ReferenceEquals(gc, owner));
            associatedPauses[ownerIndex].Add(pause);
        }

        var completedGcs = gcs
            .Select((gc, index) => gc with { Pauses = associatedPauses[index].ToArray() })
            .ToArray();

        _completedResult = new GcIntervalSet(
            completedGcs,
            orphanPauses.ToArray(),
            _incompleteEvidence.ToArray(),
            gcPairs.UnmatchedStarts.Count,
            gcPairs.UnmatchedStops.Count,
            pausePairs.UnmatchedStarts.Count,
            pausePairs.UnmatchedStops.Count,
            gcPairs.InvalidIntervals.Count + pausePairs.InvalidIntervals.Count,
            gcPairs.UnmatchedStarts
                .Select(item => new GcIntervalAnomaly(
                    item.Key.Process, item.TimeUs, item.TimeUs))
                .Concat(gcPairs.UnmatchedStops.Select(item =>
                    new GcIntervalAnomaly(
                        item.Key.Process, item.TimeUs, item.TimeUs)))
                .ToArray(),
            pausePairs.UnmatchedStarts
                .Select(item => new GcIntervalAnomaly(
                    item.Key.Process, item.TimeUs, item.TimeUs))
                .Concat(pausePairs.UnmatchedStops.Select(item =>
                    new GcIntervalAnomaly(
                        item.Key.Process, item.TimeUs, item.TimeUs)))
                .ToArray(),
            gcPairs.InvalidIntervals
                .Select(item => new GcIntervalAnomaly(
                    item.Key.Process, item.StartUs, item.EndUs))
                .Concat(pausePairs.InvalidIntervals.Select(item =>
                    new GcIntervalAnomaly(
                        item.Key.Process, item.StartUs, item.EndUs)))
                .ToArray());
        return _completedResult;
    }

    private static GcWallWithPauses? SelectOwner(
        GcPauseInterval pause,
        IReadOnlyList<GcWallWithPauses> gcs)
    {
        var compatible = gcs
            .Where(gc => gc.Key.Process == pause.Key.Process &&
                         gc.Key.ClrInstanceId == pause.Key.ClrInstanceId)
            .Select(gc => new
            {
                Gc = gc,
                OverlapUs = new TimeWindow(gc.StartUs, gc.EndUs)
                    .IntersectDurationUs(pause.StartUs, pause.EndUs),
            })
            .Where(item => item.OverlapUs > 0)
            .ToList();

        var startInside = compatible
            .Where(item => pause.StartUs <= item.Gc.StartUs &&
                           item.Gc.StartUs < pause.EndUs)
            .OrderByDescending(item => item.Gc.StartUs)
            .ThenByDescending(item => item.Gc.Key.GcCount)
            .FirstOrDefault();
        if (startInside is not null)
            return startInside.Gc;

        return compatible
            .OrderByDescending(item => item.OverlapUs)
            .ThenByDescending(item => item.Gc.StartUs)
            .ThenByDescending(item => item.Gc.Key.GcCount)
            .Select(item => item.Gc)
            .FirstOrDefault();
    }

    private void AddMissingIdentity(
        ProcessInstanceKey process,
        long timestampUs,
        string eventKind) =>
        _incompleteEvidence.Add(new GcIncompleteEvidence(
            "missing_clr_instance", process, timestampUs, eventKind));

    private void ThrowIfCompleted()
    {
        if (_completedResult is not null)
            throw new InvalidOperationException("Cannot add GC events after completion.");
    }
}
