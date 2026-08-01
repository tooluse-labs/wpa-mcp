namespace WpaMcp.Core;

public readonly record struct ProcessInstanceKey(int Pid, long StartUs);

public readonly record struct ThreadInstanceKey(
    ProcessInstanceKey Process,
    int Tid,
    long Generation);

public readonly record struct ThreadScopeCandidate(
    ThreadInstanceKey Thread,
    long ThreadStartUs,
    long ThreadEndUs);

internal readonly record struct ThreadSelector(
    int Pid,
    int Tid,
    long? ProcessStartUs,
    long? ThreadStartUs);

internal sealed record ProcessLifetime(
    ProcessInstanceKey Key,
    long EndUs,
    bool StartObserved,
    bool EndObserved,
    bool EndFromRundown = false)
{
    public bool Contains(long timestampUs) =>
        Key.StartUs <= timestampUs && timestampUs < EndUs;
}

internal sealed record ThreadLifetime(
    ThreadInstanceKey Key,
    long StartUs,
    long EndUs,
    bool StartObserved,
    bool EndObserved)
{
    public bool Intersects(TimeWindow window) =>
        StartUs < window.EndUs && EndUs > window.StartUs;
}

internal enum InstanceResolutionStatus
{
    Resolved,
    Unresolved,
    Ambiguous,
}

internal readonly record struct InstanceResolution<T>(
    InstanceResolutionStatus Status,
    T? Value,
    IReadOnlyList<T> Candidates) where T : struct;
