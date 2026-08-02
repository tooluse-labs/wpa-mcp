using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal sealed record CpuBatchResultSnapshot(
    IReadOnlyList<CpuBatchScopeResult> ScopeResults,
    IReadOnlyList<string> Warnings,
    bool Partial,
    string? PartialErrorCode,
    int RequestedPidCount,
    int CompletedPidCount);

internal sealed class CpuBatchResultSnapshotRegistry
{
    private sealed class Entry(
        TimelineQueryContext context,
        CpuBatchResultSnapshot snapshot,
        long sizeBytes,
        DateTimeOffset now)
    {
        public TimelineQueryContext Context { get; } = context;
        public CpuBatchResultSnapshot Snapshot { get; } = snapshot;
        public long SizeBytes { get; } = sizeBytes;
        public DateTimeOffset CreatedAt { get; } = now;
        public DateTimeOffset LastAccessAt { get; set; } = now;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _idleTtl;
    private readonly TimeSpan _absoluteTtl;
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private long _storedBytes;

    internal CpuBatchResultSnapshotRegistry(
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

    internal string Store(TimelineQueryContext context, CpuBatchResultSnapshot snapshot)
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
                    "CPU batch result snapshot capacity is exhausted.");
            }

            string id;
            do
            {
                id = "cbr_" + Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            } while (_entries.ContainsKey(id));

            _entries.Add(id, new Entry(context, snapshot, sizeBytes, now));
            _storedBytes += sizeBytes;
            return id;
        }
    }

    internal CpuBatchResultSnapshot Get(string id, TimelineQueryContext context)
    {
        var now = _utcNow();
        lock (_gate)
        {
            PruneExpired(now);
            if (!_entries.TryGetValue(id, out var entry) || entry.Context != context)
            {
                throw new QueryResultCursorException(
                    QueryResultCursorFailureKind.Invalid,
                    "CPU batch result snapshot is unavailable for this cursor binding.");
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

internal sealed class CpuBatchPaginationRuntime
{
    private static readonly ConditionalWeakTable<CapabilityDiscoveryRuntime, CpuBatchPaginationRuntime>
        SessionRuntimes = new();

    private readonly QueryResultCursorCoordinator _queryResults;
    private readonly CpuBatchResultSnapshotRegistry _snapshots;
    private int _analysisSnapshotCount;

    internal CpuBatchPaginationRuntime(
        QueryResultCursorCoordinator queryResults,
        CpuBatchResultSnapshotRegistry? snapshots = null)
    {
        _queryResults = queryResults;
        _snapshots = snapshots ?? new CpuBatchResultSnapshotRegistry();
    }

    internal static CpuBatchPaginationRuntime For(CapabilityDiscoveryRuntime runtime) =>
        SessionRuntimes.GetValue(runtime, static value => new(value.QueryResults));

    internal int AnalysisSnapshotCount => Volatile.Read(ref _analysisSnapshotCount);

    internal CpuTopFunctionsBatchResponse Start(
        TimelineQueryContext context,
        CpuTopFunctionsBatchResponse complete,
        int pageSize)
    {
        Interlocked.Increment(ref _analysisSnapshotCount);
        var snapshot = new CpuBatchResultSnapshot(
            complete.ScopeResults.ToArray(),
            complete.Warnings.ToArray(),
            complete.Partial,
            complete.PartialErrorCode,
            complete.RequestedPidCount,
            complete.CompletedPidCount);
        var resultSetId = _snapshots.Store(context, snapshot);
        return CreatePage(context, snapshot, resultSetId, 0, pageSize, includeWarnings: true);
    }

    internal CpuTopFunctionsBatchResponse Resume(
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
                "CPU batch cursor position is invalid.");
        }

        var snapshot = _snapshots.Get(position.LastKey, context);
        return CreatePage(
            context,
            snapshot,
            position.LastKey,
            position.Index,
            pageSize,
            includeWarnings: true);
    }

    private static CpuTopFunctionsBatchResponse CreatePage(
        TimelineQueryContext context,
        CpuBatchResultSnapshot snapshot,
        string resultSetId,
        int startIndex,
        int pageSize,
        bool includeWarnings)
    {
        if (startIndex < 0 || startIndex > snapshot.ScopeResults.Count)
        {
            throw new QueryResultCursorException(
                QueryResultCursorFailureKind.Invalid,
                "CPU batch cursor index is outside the immutable result snapshot.");
        }

        var rows = snapshot.ScopeResults.Skip(startIndex).Take(pageSize).ToArray();
        var hasMore = startIndex + rows.Length < snapshot.ScopeResults.Count;
        return new CpuTopFunctionsBatchResponse(
            ScopeResults: rows,
            Warnings: includeWarnings ? snapshot.Warnings : Array.Empty<string>(),
            Partial: snapshot.Partial,
            PartialErrorCode: snapshot.PartialErrorCode,
            RequestedPidCount: snapshot.RequestedPidCount,
            CompletedPidCount: snapshot.CompletedPidCount,
            PageContext: context.PageContext(
                startIndex,
                pageSize,
                snapshot.ScopeResults.Count,
                rows.Length),
            ReturnedCount: rows.Length,
            HasMore: hasMore,
            NextCursor: hasMore ? QueryResultCursorRegistry.PendingDeliveryToken : null,
            ResultSetId: resultSetId);
    }
}
