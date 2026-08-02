using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal sealed record ThreadComparisonResultSnapshot(
    IReadOnlyList<ThreadComparisonWindowRow> Rows,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess,
    ThreadInstanceKey? SelectedThread,
    string ScopeMode,
    bool PidReuseObserved,
    IReadOnlyList<ProcessInstanceKey> IncludedProcesses,
    IReadOnlyList<ThreadScopeCandidate> IncludedThreads,
    string ScopeStatus,
    string CapabilityStatus,
    long MatchedEventCount,
    string? NoDataReason,
    IReadOnlyList<string> DoesNotProve,
    string? BaselineWindowName);

internal sealed class ThreadComparisonResultSnapshotRegistry
{
    private sealed class Entry(
        TimelineQueryContext context,
        ThreadComparisonResultSnapshot snapshot,
        long sizeBytes,
        DateTimeOffset now)
    {
        internal TimelineQueryContext Context { get; } = context;
        internal ThreadComparisonResultSnapshot Snapshot { get; } = snapshot;
        internal long SizeBytes { get; } = sizeBytes;
        internal DateTimeOffset CreatedAt { get; } = now;
        internal DateTimeOffset LastAccessAt { get; set; } = now;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _idleTtl;
    private readonly TimeSpan _absoluteTtl;
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private long _storedBytes;

    internal ThreadComparisonResultSnapshotRegistry(
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? idleTtl = null,
        TimeSpan? absoluteTtl = null,
        int maxEntries = 128,
        long maxBytes = 256L * 1024 * 1024)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _idleTtl = idleTtl ?? TimeSpan.FromMinutes(3);
        _absoluteTtl = absoluteTtl ?? TimeSpan.FromMinutes(16);
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    internal string Store(
        TimelineQueryContext context,
        ThreadComparisonResultSnapshot snapshot)
    {
        var sizeBytes = JsonSerializer.SerializeToUtf8Bytes(
            snapshot,
            McpJsonUtilities.DefaultOptions).LongLength;
        var now = _utcNow();
        lock (_gate)
        {
            PruneExpired(now);
            if (_entries.Count >= _maxEntries || sizeBytes > _maxBytes - _storedBytes)
            {
                throw new QueryResultCursorException(
                    QueryResultCursorFailureKind.RegistryCapacity,
                    "Thread comparison result snapshot capacity is exhausted.");
            }

            string id;
            do
            {
                id = "twr_" + Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            } while (_entries.ContainsKey(id));

            _entries.Add(id, new Entry(context, snapshot, sizeBytes, now));
            _storedBytes += sizeBytes;
            return id;
        }
    }

    internal ThreadComparisonResultSnapshot Get(
        string id,
        TimelineQueryContext context)
    {
        var now = _utcNow();
        lock (_gate)
        {
            PruneExpired(now);
            if (!_entries.TryGetValue(id, out var entry) || entry.Context != context)
            {
                throw new QueryResultCursorException(
                    QueryResultCursorFailureKind.Invalid,
                    "Thread comparison result snapshot is unavailable for this cursor binding.");
            }

            entry.LastAccessAt = now;
            return entry.Snapshot;
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var pair in _entries.ToArray())
        {
            if (now - pair.Value.LastAccessAt <= _idleTtl &&
                now - pair.Value.CreatedAt <= _absoluteTtl)
            {
                continue;
            }

            _entries.Remove(pair.Key);
            _storedBytes -= pair.Value.SizeBytes;
        }
    }
}

internal sealed class ThreadComparisonPaginationRuntime
{
    private static readonly ConditionalWeakTable<CapabilityDiscoveryRuntime, ThreadComparisonPaginationRuntime>
        SessionRuntimes = new();

    private readonly QueryResultCursorCoordinator _queryResults;
    private readonly ThreadComparisonResultSnapshotRegistry _snapshots;
    private int _analysisSnapshotCount;

    internal ThreadComparisonPaginationRuntime(
        QueryResultCursorCoordinator queryResults,
        ThreadComparisonResultSnapshotRegistry? snapshots = null)
    {
        _queryResults = queryResults;
        _snapshots = snapshots ?? new ThreadComparisonResultSnapshotRegistry();
    }

    internal static ThreadComparisonPaginationRuntime For(
        CapabilityDiscoveryRuntime runtime) =>
        SessionRuntimes.GetValue(runtime, static value => new(value.QueryResults));

    internal int AnalysisSnapshotCount => Volatile.Read(ref _analysisSnapshotCount);

    internal ThreadCompareWindowsResponse Start(
        TimelineQueryContext context,
        ThreadCompareWindowsResponse complete,
        int pageSize)
    {
        Interlocked.Increment(ref _analysisSnapshotCount);
        var snapshot = new ThreadComparisonResultSnapshot(
            complete.Rows.ToArray(),
            complete.Warnings.ToArray(),
            complete.SelectedProcess,
            complete.SelectedThread,
            complete.ScopeMode,
            complete.PidReuseObserved,
            complete.IncludedProcesses.ToArray(),
            complete.IncludedThreads.ToArray(),
            complete.ScopeStatus,
            complete.CapabilityStatus,
            complete.MatchedEventCount,
            complete.NoDataReason,
            complete.DoesNotProve.ToArray(),
            complete.BaselineWindowName);
        var resultSetId = _snapshots.Store(context, snapshot);
        return CreatePage(context, snapshot, resultSetId, 0, pageSize);
    }

    internal ThreadCompareWindowsResponse Resume(
        TimelineQueryContext context,
        string cursor,
        int pageSize)
    {
        var position = _queryResults.ResolveTimeline(context, cursor);
        if (position.Phase != TimelinePagination.Phase ||
            string.IsNullOrWhiteSpace(position.LastKey))
        {
            throw new QueryResultCursorException(
                QueryResultCursorFailureKind.Invalid,
                "Thread comparison cursor position is invalid.");
        }

        var snapshot = _snapshots.Get(position.LastKey, context);
        return CreatePage(context, snapshot, position.LastKey, position.Index, pageSize);
    }

    private static ThreadCompareWindowsResponse CreatePage(
        TimelineQueryContext context,
        ThreadComparisonResultSnapshot snapshot,
        string resultSetId,
        int startIndex,
        int pageSize)
    {
        if (startIndex < 0 || startIndex > snapshot.Rows.Count)
        {
            throw new QueryResultCursorException(
                QueryResultCursorFailureKind.Invalid,
                "Thread comparison cursor index is outside the immutable result snapshot.");
        }

        var rows = snapshot.Rows.Skip(startIndex).Take(pageSize).ToArray();
        var hasMore = startIndex + rows.Length < snapshot.Rows.Count;
        return new ThreadCompareWindowsResponse(
            Rows: rows,
            Warnings: snapshot.Warnings,
            SelectedProcess: snapshot.SelectedProcess,
            SelectedThread: snapshot.SelectedThread,
            ScopeMode: snapshot.ScopeMode,
            PidReuseObserved: snapshot.PidReuseObserved,
            IncludedProcesses: snapshot.IncludedProcesses,
            IncludedThreads: snapshot.IncludedThreads,
            ScopeStatus: snapshot.ScopeStatus,
            CapabilityStatus: snapshot.CapabilityStatus,
            MatchedEventCount: snapshot.MatchedEventCount,
            NoDataReason: snapshot.NoDataReason,
            DoesNotProve: snapshot.DoesNotProve,
            BaselineWindowName: snapshot.BaselineWindowName,
            PageContext: context.PageContext(
                startIndex,
                pageSize,
                snapshot.Rows.Count,
                rows.Length),
            TotalWindowCount: snapshot.Rows.Count,
            ReturnedCount: rows.Length,
            HasMore: hasMore,
            NextCursor: hasMore ? QueryResultCursorRegistry.PendingDeliveryToken : null,
            ResultSetId: resultSetId);
    }
}
