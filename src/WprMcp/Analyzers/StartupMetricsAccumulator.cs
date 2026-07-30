using System.Collections.ObjectModel;
using WprMcp.Core;

namespace WprMcp.Analyzers;

internal sealed record StartupSchedulerMetrics(
    long StartupCpuUs,
    long StartupBlockedUs,
    IReadOnlyDictionary<string, long> BlockedUsByReason,
    int RunningIntervalCount,
    int BlockedIntervalCount,
    IReadOnlyDictionary<string, long>? BlockedCountByReason = null);

internal sealed class StartupMetricsAccumulator : ISchedulerIntervalSink
{
    private readonly Dictionary<ProcessInstanceKey, ProcessAggregate> _byProcess;
    private bool _completed;

    public StartupMetricsAccumulator(
        IReadOnlyList<StartupProcessObservation> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        _byProcess = processes.ToDictionary(
            process => process.Process,
            process => new ProcessAggregate(process.Window.Bounds));
    }

    public void OnRunning(in RunningInterval interval)
    {
        EnsureMutable();
        if (!_byProcess.TryGetValue(interval.Thread.Process, out var aggregate))
            return;

        var accountedUs = aggregate.Window.IntersectDurationUs(
            interval.StartUs, interval.EndUs);
        if (accountedUs <= 0)
            return;

        aggregate.StartupCpuUs = checked(aggregate.StartupCpuUs + accountedUs);
        aggregate.RunningIntervalCount = checked(aggregate.RunningIntervalCount + 1);
    }

    public void OnBlocked(in BlockedInterval interval)
    {
        EnsureMutable();
        if (!_byProcess.TryGetValue(interval.Thread.Process, out var aggregate))
            return;

        var accountedUs = aggregate.Window.IntersectDurationUs(
            interval.StartUs, interval.EndUs);
        if (accountedUs <= 0)
            return;

        aggregate.StartupBlockedUs = checked(
            aggregate.StartupBlockedUs + accountedUs);
        aggregate.BlockedIntervalCount = checked(aggregate.BlockedIntervalCount + 1);
        aggregate.BlockedUsByReason[interval.WaitReason] = checked(
            aggregate.BlockedUsByReason.GetValueOrDefault(interval.WaitReason) + accountedUs);
        aggregate.BlockedCountByReason[interval.WaitReason] = checked(
            aggregate.BlockedCountByReason.GetValueOrDefault(interval.WaitReason) + 1);
    }

    public IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> Complete()
    {
        EnsureMutable();
        _completed = true;
        var result = _byProcess.ToDictionary(
            item => item.Key,
            item => new StartupSchedulerMetrics(
                item.Value.StartupCpuUs,
                item.Value.StartupBlockedUs,
                new ReadOnlyDictionary<string, long>(
                    new Dictionary<string, long>(
                        item.Value.BlockedUsByReason,
                        StringComparer.Ordinal)),
                item.Value.RunningIntervalCount,
                item.Value.BlockedIntervalCount,
                new ReadOnlyDictionary<string, long>(
                    new Dictionary<string, long>(
                        item.Value.BlockedCountByReason,
                        StringComparer.Ordinal))));
        return new ReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics>(result);
    }

    public static IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> Project(
        IReadOnlyList<StartupProcessObservation> processes,
        IEnumerable<RunningInterval> running,
        IEnumerable<BlockedInterval> blocked)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(blocked);
        var accumulator = new StartupMetricsAccumulator(processes);
        foreach (var interval in running)
            accumulator.OnRunning(interval);
        foreach (var interval in blocked)
            accumulator.OnBlocked(interval);
        return accumulator.Complete();
    }

    private void EnsureMutable()
    {
        if (_completed)
            throw new InvalidOperationException("Startup metrics are complete.");
    }

    private sealed class ProcessAggregate(TimeWindow window)
    {
        public TimeWindow Window { get; } = window;
        public long StartupCpuUs { get; set; }
        public long StartupBlockedUs { get; set; }
        public int RunningIntervalCount { get; set; }
        public int BlockedIntervalCount { get; set; }
        public Dictionary<string, long> BlockedUsByReason { get; } =
            new(StringComparer.Ordinal);
        public Dictionary<string, long> BlockedCountByReason { get; } =
            new(StringComparer.Ordinal);
    }
}
