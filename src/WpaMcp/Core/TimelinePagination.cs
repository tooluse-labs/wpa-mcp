using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal readonly record struct TimelineQueryContext(
    string TraceId,
    string TraceGenerationId,
    string ToolName,
    string ContractVersion,
    string? SymbolContextId,
    string QueryHash,
    string Ordering)
{
    internal TimelinePageContext PageContext(
        int startIndex,
        int requestedPageSize,
        int totalCount,
        int returnedCount) =>
        new(
            TraceId,
            TraceGenerationId,
            ToolName,
            ContractVersion,
            SymbolContextId,
            QueryHash,
            Ordering,
            startIndex,
            requestedPageSize,
            totalCount,
            returnedCount);
}

internal readonly record struct TimelinePageSlice<T>(
    IReadOnlyList<T> Rows,
    int StartIndex,
    int TotalCount,
    bool HasMore);

internal static class TimelinePagination
{
    internal const string Phase = "rows";
    internal const string ThreadLifetimeTool = "thread_lifetime";
    internal const string ProcessCreateTimingTool = "process_create_timing";
    internal const string ImageLoadTimingTool = "image_load_timing";
    internal const string ListProcessesTool = "list_processes";
    internal const string CpuTopFunctionsBatchTool = "cpu_top_functions_batch";
    internal const string ThreadCompareWindowsTool = "thread_compare_windows";
    internal const string ThreadLifetimeOrdering =
        "start_time_us_asc_tid_asc_thread_generation_asc";
    internal const string ProcessCreateTimingOrdering =
        "start_time_us_asc_pid_asc_source_ordinal_asc";
    internal const string ImageLoadTimingOrdering =
        "time_us_asc_event_index_asc";
    internal const string ListProcessesCpuOrdering =
        "cpu_us_desc_pid_asc_process_start_us_asc";
    internal const string CpuTopFunctionsBatchOrdering =
        "request_selector_index_asc";
    internal const string ThreadCompareWindowsOrdering =
        "request_window_index_asc";
    internal const string ListProcessesWallOrdering =
        "wall_us_desc_pid_asc_process_start_us_asc";
    internal const string ListProcessesWaitRatioOrdering =
        "wait_ratio_rank_desc_wall_us_desc_pid_asc_process_start_us_asc";

    internal static bool IsTimelineTool(string toolName) => toolName is
        ThreadLifetimeTool or ProcessCreateTimingTool or ImageLoadTimingTool or
        ListProcessesTool or CpuTopFunctionsBatchTool or ThreadCompareWindowsTool;

    internal static TimelineQueryContext CreateContext(
        TraceLease lease,
        string suppliedTraceReference,
        string toolName,
        string canonicalQuery,
        string ordering)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedTraceReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalQuery);
        ArgumentException.ThrowIfNullOrWhiteSpace(ordering);
        return new TimelineQueryContext(
            TraceQueryExecutionContext.CurrentReference?.TraceId ?? suppliedTraceReference,
            BuildTraceGenerationId(lease.GenerationIdentity),
            toolName,
            ToolContractVersions.V2,
            SymbolQueryExecutionContext.CurrentSymbolContextId,
            Hash(canonicalQuery),
            ordering);
    }

    internal static TimelineQueryContext DirectContext(
        string toolName,
        string canonicalQuery,
        string ordering) =>
        new(
            "direct",
            "tgen_direct",
            toolName,
            ToolContractVersions.V2,
            SymbolContextId: null,
            Hash(canonicalQuery),
            ordering);

    internal static string CanonicalQuery(
        string toolName,
        params (string Name, string? Value)[] fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(fields);
        var builder = new StringBuilder(toolName);
        foreach (var (name, value) in fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            builder.Append('\n').Append(name).Append('=').Append(value ?? "null");
        }
        return builder.ToString();
    }

    internal static string Number(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    internal static string? OptionalNumber(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    internal static TimelinePageSlice<T> Slice<T>(
        IReadOnlyList<T> orderedRows,
        QueryResultCursorPosition position,
        int pageSize,
        Func<T, string> keySelector)
    {
        ArgumentNullException.ThrowIfNull(orderedRows);
        ArgumentNullException.ThrowIfNull(keySelector);
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (!string.Equals(position.Phase, Phase, StringComparison.Ordinal) ||
            position.Index < 0 || position.Index > orderedRows.Count)
        {
            throw InvalidPosition();
        }

        for (var index = 1; index < orderedRows.Count; index++)
        {
            if (string.Equals(
                    keySelector(orderedRows[index - 1]),
                    keySelector(orderedRows[index]),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Timeline ordering did not produce a unique continuation key.");
            }
        }

        if (position.Index == 0)
        {
            if (position.LastKey is not null)
                throw InvalidPosition();
        }
        else if (!string.Equals(
                     position.LastKey,
                     keySelector(orderedRows[position.Index - 1]),
                     StringComparison.Ordinal))
        {
            throw InvalidPosition();
        }

        var returned = Math.Min(pageSize, orderedRows.Count - position.Index);
        var rows = orderedRows.Skip(position.Index).Take(returned).ToArray();
        return new TimelinePageSlice<T>(
            rows,
            position.Index,
            orderedRows.Count,
            checked(position.Index + returned) < orderedRows.Count);
    }

    internal static string ThreadKey(ThreadLifetimeRow row) =>
        ThreadKey(row.StartTimeUs, row.Tid, row.ThreadGeneration);

    internal static string ThreadKey(long startTimeUs, int tid, long threadGeneration) =>
        Composite(startTimeUs, tid, threadGeneration);

    internal static string ProcessCreateKey(ChildSpawnTiming row) =>
        ProcessCreateKey(row.StartTimeUs, row.Pid, row.SourceOrdinal);

    internal static string ProcessCreateKey(long startTimeUs, int pid, long sourceOrdinal) =>
        Composite(startTimeUs, pid, sourceOrdinal);

    internal static string ImageLoadKey(ImageLoadRow row) =>
        ImageLoadKey(row.TimeUs, row.EventIndex);

    internal static string ImageLoadKey(long timeUs, long eventIndex) =>
        Composite(timeUs, eventIndex);

    internal static string ProcessKey(ProcessRow row) =>
        ProcessKey(row.Pid, row.StartUs);

    internal static string ProcessKey(int pid, long processStartUs) =>
        Composite(pid, processStartUs);

    internal static string ListProcessesOrdering(string orderBy) => orderBy switch
    {
        "cpu" => ListProcessesCpuOrdering,
        "wall" => ListProcessesWallOrdering,
        "wait_ratio" => ListProcessesWaitRatioOrdering,
        _ => throw new ArgumentException(
            "orderBy must be 'cpu', 'wall', or 'wait_ratio'.",
            nameof(orderBy)),
    };

    private static string Composite(params long[] values) =>
        string.Join('\u001f', values.Select(Number));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string BuildTraceGenerationId(
        TraceCache.GenerationIdentity generation)
    {
        var stamp = generation.Stamp;
        var material = string.Join(
            "\n",
            generation.Sequence.ToString(CultureInfo.InvariantCulture),
            stamp.CanonicalPath,
            stamp.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            stamp.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            stamp.Length.ToString(CultureInfo.InvariantCulture),
            stamp.VolumeSerialNumber?.ToString(CultureInfo.InvariantCulture) ?? "-",
            stamp.FileId?.ToString(CultureInfo.InvariantCulture) ?? "-");
        return "tgen_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32].ToLowerInvariant();
    }

    private static QueryResultCursorException InvalidPosition() =>
        new(
            QueryResultCursorFailureKind.Invalid,
            "The query-result cursor position does not match the bound timeline result set.");
}
