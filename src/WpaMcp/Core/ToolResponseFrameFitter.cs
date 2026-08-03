using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Core;

internal sealed record ToolResponseBudgetOptions(int MaxResponseFrameBytes)
{
    internal const int DefaultMaxResponseFrameBytes = 100_000;
    internal const int HardMaxResponseFrameBytes = 100_000;
    internal const int MinimumResponseFrameBytes = 4_096;

    internal static ToolResponseBudgetOptions Default { get; } = new(DefaultMaxResponseFrameBytes);

    internal ToolResponseBudgetOptions Validate()
    {
        if (MaxResponseFrameBytes is < MinimumResponseFrameBytes or > HardMaxResponseFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResponseFrameBytes),
                $"Tool response frames must be from {MinimumResponseFrameBytes} through {HardMaxResponseFrameBytes} bytes.");
        }
        return this;
    }
}

internal static class ToolRequestIdPolicy
{
    internal const int MaxSerializedBytes = 128;

    internal static int SerializedBytes(RequestId requestId) =>
        JsonSerializer.SerializeToUtf8Bytes(requestId, McpJsonUtilities.DefaultOptions).Length;

    internal static void Validate(RequestId requestId)
    {
        if (SerializedBytes(requestId) > MaxSerializedBytes)
            throw new ToolRequestIdTooLargeException();
    }
}

internal sealed class ToolRequestIdTooLargeException()
    : InvalidOperationException("The decoded JSON-RPC request identifier exceeds the public correlation limit.");

internal sealed record FittedToolResponse(CallToolResult Result, int FrameBytes, bool RowsTruncated);

internal sealed class ToolResponseFrameFitter(
    ToolResponseBudgetOptions options,
    IToolPrivacyRedactor privacy,
    IServiceProvider? services = null)
{
    private const int StdioFramingBytes = 1;
    private static readonly RequestId WorstCaseRequestId = new(new string('r', 126));
    private const string BudgetWarning =
        "Response rows were truncated at a manifest-declared section boundary to fit the JSON-RPC frame budget.";
    private const string ScopeBudgetWarning =
        "Scope identity detail arrays were omitted to fit the JSON-RPC frame budget; candidateTotal and includedTotal remain exact.";
    private readonly ToolResponseBudgetOptions _options = options.Validate();
    private readonly IToolPrivacyRedactor _privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
    private readonly IServiceProvider? _services = services;

    internal FittedToolResponse Fit(
        RequestId requestId,
        JsonObject projectedEnvelope,
        JsonObject outputSchema,
        ActiveToolDefinition tool,
        IReadOnlyDictionary<string, JsonElement>? arguments = null)
    {
        ToolRequestIdPolicy.Validate(requestId);
        ArgumentNullException.ThrowIfNull(projectedEnvelope);
        ArgumentNullException.ThrowIfNull(outputSchema);
        ArgumentNullException.ThrowIfNull(tool);

        var redacted = _privacy.Redact(projectedEnvelope, tool);
        var sizingRequestId = tool.PaginationMode == "cursor"
            ? WorstCaseRequestId
            : requestId;
        var initial = BuildAndValidate(sizingRequestId, redacted, outputSchema);
        if (initial.FrameBytes <= _options.MaxResponseFrameBytes)
        {
            FinalizePendingCursor(redacted, tool, arguments, responseBudgetTrimmed: false);
            var delivered = BuildAndValidate(requestId, redacted, outputSchema);
            return new(delivered.Result, delivered.FrameBytes, RowsTruncated: false);
        }

        // Aggregate/whole-trace requests may legitimately include hundreds of exact
        // process identities even when their domain section is empty. Preserve the
        // successful/no-data contract and exact identity totals before considering an
        // atomic response_too_large failure. A partial identity sample would invite
        // callers to mistake the sample for the complete scope, so omission is all-or-none.
        var scopeReduced = redacted.DeepClone().AsObject();
        if (OmitScopeIdentityDetailsForBudget(scopeReduced))
        {
            var reduced = BuildAndValidate(sizingRequestId, scopeReduced, outputSchema);
            if (reduced.FrameBytes <= _options.MaxResponseFrameBytes)
            {
                FinalizePendingCursor(
                    scopeReduced,
                    tool,
                    arguments,
                    responseBudgetTrimmed: false);
                var delivered = BuildAndValidate(requestId, scopeReduced, outputSchema);
                return new(delivered.Result, delivered.FrameBytes, RowsTruncated: false);
            }

            redacted = scopeReduced;
        }

        if (redacted["status"]?.GetValue<string>() == "failed")
        {
            var budgetFailure = ToResponseTooLargeFailure(redacted, preserveOriginalError: true);
            var fittedFailure = BuildFailureWithinBudget(requestId, budgetFailure, outputSchema);
            if (fittedFailure.FrameBytes <= _options.MaxResponseFrameBytes)
                return new(fittedFailure.Result, fittedFailure.FrameBytes, RowsTruncated: false);
            throw new InvalidOperationException(
                $"The schema-required failure response requires {fittedFailure.FrameBytes} bytes, " +
                $"exceeding the configured {_options.MaxResponseFrameBytes}-byte frame budget.");
        }

        // Composite arrays contain cross-section call/evidence references. Until a
        // typed dependency-aware pager exists, the whole validated composite is the
        // atomic delivery unit: never trim one JSON pointer and leave dangling proof.
        if (CompositeResultContractValidator.IsCompositeTool(tool.ToolName))
        {
            var atomicFailure = ToResponseTooLargeFailure(redacted);
            var fittedFailure = BuildFailureWithinBudget(requestId, atomicFailure, outputSchema);
            if (fittedFailure.FrameBytes > _options.MaxResponseFrameBytes)
            {
                throw new InvalidOperationException(
                    $"The schema-required atomic composite failure requires {fittedFailure.FrameBytes} bytes, " +
                    $"exceeding the configured {_options.MaxResponseFrameBytes}-byte frame budget.");
            }
            return new(fittedFailure.Result, fittedFailure.FrameBytes, RowsTruncated: false);
        }

        // A successful response may claim response-budget continuation only when
        // the tool exposes a real cursor that can retrieve every omitted row.
        // Fixed/top-N tools fail atomically instead of publishing hasMore without
        // a continuation mechanism.
        if (tool.PaginationMode != "cursor")
        {
            var atomicFailure = ToResponseTooLargeFailure(redacted);
            var fittedFailure = BuildFailureWithinBudget(requestId, atomicFailure, outputSchema);
            if (fittedFailure.FrameBytes > _options.MaxResponseFrameBytes)
            {
                throw new InvalidOperationException(
                    $"The schema-required atomic response_too_large failure requires {fittedFailure.FrameBytes} bytes, " +
                    $"exceeding the configured {_options.MaxResponseFrameBytes}-byte frame budget.");
            }
            return new(fittedFailure.Result, fittedFailure.FrameBytes, RowsTruncated: false);
        }

        var working = redacted;
        foreach (var pointer in tool.PageableSections.Reverse())
        {
            if (tool.PaginationMode == "cursor" &&
                !CanResumeSection(tool.ToolName, pointer))
            {
                continue;
            }
            var array = ResolveDataPointer(working, pointer) as JsonArray
                ?? throw new InvalidOperationException($"Manifest pageable section '{pointer}' is absent from finalized data.");
            if (array.Count <= 1)
                continue;

            var originalCount = array.Count;
            var low = 1;
            var high = originalCount - 1;
            JsonObject? best = null;
            BuildResult? bestResult = null;
            while (low <= high)
            {
                var keep = low + ((high - low) / 2);
                var candidate = working.DeepClone().AsObject();
                TrimSection(candidate, pointer, keep, originalCount);
                if (tool.PaginationMode == "cursor")
                    SetCursor(
                        candidate,
                        pointer,
                        tool.ToolName == "inspect_trace" ||
                        TimelinePagination.IsTimelineTool(tool.ToolName)
                            ? QueryResultCursorRegistry.PendingDeliveryToken
                            : CapabilityCursorRegistry.PendingDeliveryToken,
                        responseBudgetTrimmed: true);
                var measured = BuildAndValidate(sizingRequestId, candidate, outputSchema);
                if (measured.FrameBytes <= _options.MaxResponseFrameBytes)
                {
                    best = candidate;
                    bestResult = measured;
                    low = keep + 1;
                }
                else
                {
                    high = keep - 1;
                }
            }

            if (best is not null && bestResult is not null)
            {
                FinalizePendingCursor(best, tool, arguments, responseBudgetTrimmed: true);
                var delivered = BuildAndValidate(requestId, best, outputSchema);
                return new(delivered.Result, delivered.FrameBytes, RowsTruncated: true);
            }

            TrimSection(working, pointer, keep: 1, originalCount);
        }

        var failure = ToResponseTooLargeFailure(working);
        var fallback = BuildFailureWithinBudget(requestId, failure, outputSchema);
        if (fallback.FrameBytes > _options.MaxResponseFrameBytes)
        {
            throw new InvalidOperationException(
                $"The schema-required response_too_large failure requires {fallback.FrameBytes} bytes, " +
                $"exceeding the configured {_options.MaxResponseFrameBytes}-byte frame budget.");
        }
        return new(fallback.Result, fallback.FrameBytes, RowsTruncated: true);
    }

    /// <summary>
    /// Measures the exact validated success projection before any response-budget
    /// fallback. Startup discovery preflight uses this with the maximum legal request
    /// id so every advertised immutable contract page is known to be deliverable.
    /// </summary>
    internal FittedToolResponse MeasureUnboundedSuccess(
        RequestId requestId,
        JsonObject projectedEnvelope,
        JsonObject outputSchema,
        ActiveToolDefinition tool)
    {
        ToolRequestIdPolicy.Validate(requestId);
        ArgumentNullException.ThrowIfNull(projectedEnvelope);
        ArgumentNullException.ThrowIfNull(outputSchema);
        ArgumentNullException.ThrowIfNull(tool);

        var redacted = _privacy.Redact(projectedEnvelope, tool);
        var measured = BuildAndValidate(requestId, redacted, outputSchema);
        if (measured.Result.IsError == true)
        {
            throw new InvalidOperationException(
                $"The successful preflight projection for '{tool.ToolName}' became an error.");
        }
        return new(measured.Result, measured.FrameBytes, RowsTruncated: false);
    }

    private static bool CanResumeSection(string toolName, string pointer) =>
        toolName != "inspect_trace" || pointer is
            "/traceEvidenceMap/capabilities" or
            "/traceEvidenceMap/workflows";

    internal static int MeasureFrame(RequestId requestId, CallToolResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new JsonRpcResponse
            {
                Id = requestId,
                Result = JsonSerializer.SerializeToNode(result, McpJsonUtilities.DefaultOptions),
            },
            McpJsonUtilities.DefaultOptions).Length + StdioFramingBytes;

    internal static CallToolResult BuildCallResult(JsonObject envelope) =>
        new()
        {
            Content = [new TextContentBlock
            {
                Text = envelope.ToJsonString(McpJsonUtilities.DefaultOptions),
            }],
            StructuredContent = JsonSerializer.SerializeToElement(envelope, McpJsonUtilities.DefaultOptions),
            IsError = envelope["status"]?.GetValue<string>() == "failed",
        };

    private static BuildResult BuildAndValidate(
        RequestId requestId,
        JsonObject envelope,
        JsonObject outputSchema)
    {
        ToolWireSchemaValidator.ValidateOrThrow(envelope, outputSchema);
        ValidateEnvelopeSemantics(envelope);
        var result = BuildCallResult(envelope);
        return new BuildResult(result, MeasureFrame(requestId, result));
    }

    private BuildResult BuildFailureWithinBudget(
        RequestId requestId,
        JsonObject envelope,
        JsonObject outputSchema) =>
        BuildAndValidate(requestId, envelope, outputSchema);

    private static void TrimSection(
        JsonObject envelope,
        string pointer,
        int keep,
        int originalCount)
    {
        var array = ResolveDataPointer(envelope, pointer) as JsonArray
            ?? throw new InvalidOperationException($"Manifest pageable section '{pointer}' is absent.");
        if (keep <= 0 || keep >= originalCount || array.Count != originalCount)
            throw new ArgumentOutOfRangeException(nameof(keep));
        while (array.Count > keep)
            array.RemoveAt(array.Count - 1);

        var sectionName = SectionName(pointer);
        var section = envelope["sections"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => item["section"]!.GetValue<string>() == sectionName);
        section["returned"] = keep.ToString(CultureInfo.InvariantCulture);
        section["hasMore"] = true;
        section["moreState"] = "present";
        section["truncationReason"] = "response_budget";
        if (section["totalState"]!.GetValue<string>() == "unknown")
        {
            section["totalAvailable"] = originalCount.ToString(CultureInfo.InvariantCulture);
            section["totalState"] = "lower_bound";
        }

        envelope["hasMore"] = true;
        envelope["completeness"]!["hasMore"] = true;
        var warnings = envelope["warnings"]!.AsArray();
        if (!warnings.Any(item => item?.GetValue<string>() == BudgetWarning))
            warnings.Add(BudgetWarning);

        SynchronizeReturnedCounters(envelope, pointer, keep);
    }

    internal static bool OmitScopeIdentityDetailsForBudget(JsonObject envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope["scope"] is not JsonObject scope ||
            scope["candidates"] is not JsonArray candidates ||
            scope["included"] is not JsonArray included ||
            (candidates.Count == 0 && included.Count == 0))
        {
            return false;
        }
        if (scope["candidateTotal"] is null || scope["includedTotal"] is null)
            throw new InvalidOperationException("Scope identity totals must precede budget omission.");

        candidates.Clear();
        included.Clear();
        scope["detailCompleteness"] = "omitted_due_to_response_budget";
        var warnings = envelope["warnings"]?.AsArray()
            ?? throw new InvalidOperationException("Tool envelope omitted warnings.");
        if (!warnings.Any(item => item?.GetValue<string>() == ScopeBudgetWarning))
            warnings.Add(ScopeBudgetWarning);
        return true;
    }

    private static void SetCursor(
        JsonObject envelope,
        string pointer,
        string cursor,
        bool responseBudgetTrimmed)
    {
        var data = envelope["data"]!.AsObject();
        data["hasMore"] = true;
        data["nextCursor"] = cursor;
        if (data["totals"] is JsonObject totals &&
            ResolveDataPointer(envelope, pointer) is JsonArray retainedRows)
        {
            totals["returnedCapabilities"] = retainedRows.Count;
        }
        var sectionName = SectionName(pointer);
        var section = envelope["sections"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => item["section"]!.GetValue<string>() == sectionName);
        section["hasMore"] = true;
        section["moreState"] = "present";
        section["continuationAvailable"] = true;
        section["nextCursor"] = cursor;
        if (responseBudgetTrimmed)
            section["truncationReason"] = "response_budget";
        SynchronizeReturnedCounters(envelope, pointer, retainedRows: null);
    }

    private void FinalizePendingCursor(
        JsonObject envelope,
        ActiveToolDefinition tool,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        bool responseBudgetTrimmed)
    {
        if (tool.PaginationMode != "cursor" ||
            envelope["data"] is not JsonObject data ||
            data["nextCursor"]?.GetValue<string>() is not { } pending ||
            !string.Equals(
                pending,
                tool.ToolName == "inspect_trace" ||
                TimelinePagination.IsTimelineTool(tool.ToolName)
                    ? QueryResultCursorRegistry.PendingDeliveryToken
                    : CapabilityCursorRegistry.PendingDeliveryToken,
                StringComparison.Ordinal))
        {
            return;
        }

        if (tool.ToolName == "inspect_trace")
        {
            FinalizeInspectTraceCursor(
                envelope,
                arguments,
                runtime: _services?.GetService(typeof(CapabilityDiscoveryRuntime)) as CapabilityDiscoveryRuntime
                    ?? throw new InvalidOperationException(
                        "inspect_trace cursor fitting requires CapabilityDiscoveryRuntime."),
                responseBudgetTrimmed);
            return;
        }
        if (TimelinePagination.IsTimelineTool(tool.ToolName))
        {
            FinalizeTimelineCursor(
                envelope,
                tool,
                arguments,
                runtime: _services?.GetService(typeof(CapabilityDiscoveryRuntime)) as CapabilityDiscoveryRuntime
                    ?? throw new InvalidOperationException(
                        "Timeline cursor fitting requires CapabilityDiscoveryRuntime."),
                responseBudgetTrimmed);
            return;
        }
        var pointer = tool.PageableSections.Single();
        var retained = (ResolveDataPointer(envelope, pointer) as JsonArray)?.Count
            ?? throw new InvalidOperationException(
                "Cursor-aware frame fitting requires one declared array section.");
        var runtime = _services?.GetService(typeof(CapabilityDiscoveryRuntime)) as CapabilityDiscoveryRuntime
            ?? throw new InvalidOperationException(
                "Cursor-aware frame fitting requires CapabilityDiscoveryRuntime.");
        var continuation = runtime.FinalizePageContinuation(
            ReadArgumentString(arguments, "domain"),
            ReadArgumentString(arguments, "goal"),
            ReadArgumentString(arguments, "cursor"),
            retained)
            ?? throw new InvalidOperationException(
                "A cursor-marked capability page must have a continuation.");
        SetCursor(envelope, pointer, continuation, responseBudgetTrimmed);
    }

    private static void FinalizeTimelineCursor(
        JsonObject envelope,
        ActiveToolDefinition tool,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        CapabilityDiscoveryRuntime runtime,
        bool responseBudgetTrimmed)
    {
        var data = envelope["data"]?.AsObject()
            ?? throw new InvalidOperationException("Timeline cursor response omitted data.");
        var page = data["pageContext"]?.AsObject()
            ?? throw new InvalidOperationException("Timeline cursor response omitted PageContext.");
        var pointer = tool.PageableSections.Single();
        var rows = ResolveDataPointer(envelope, pointer) as JsonArray
            ?? throw new InvalidOperationException(
                "Timeline cursor response omitted its declared row section.");
        var last = rows.LastOrDefault()?.AsObject()
            ?? throw new InvalidOperationException(
                "A cursor-marked timeline page must retain at least one row.");
        var lastKey = tool.ToolName switch
        {
            TimelinePagination.ThreadLifetimeTool => TimelinePagination.ThreadKey(
                ReadExactInt64(last, "startTimeUs"),
                last["tid"]!.GetValue<int>(),
                ReadExactInt64(last, "threadGeneration")),
            TimelinePagination.ProcessCreateTimingTool => TimelinePagination.ProcessCreateKey(
                ReadExactInt64(last, "startTimeUs"),
                last["pid"]!.GetValue<int>(),
                ReadExactInt64(last, "sourceOrdinal")),
            TimelinePagination.ImageLoadTimingTool => TimelinePagination.ImageLoadKey(
                ReadExactInt64(last, "timeUs"),
                ReadExactInt64(last, "eventIndex")),
            TimelinePagination.ListProcessesTool => TimelinePagination.ProcessKey(
                last["pid"]!.GetValue<int>(),
                ReadExactInt64(last, "startUs")),
            TimelinePagination.CpuTopFunctionsBatchTool =>
                envelope["data"]?["resultSetId"]?.GetValue<string>() ??
                throw new InvalidOperationException(
                    "CPU batch pagination requires data.resultSetId."),
            _ => throw new InvalidOperationException("Unsupported timeline cursor tool."),
        };
        var context = new TimelineQueryContext(
            page["traceId"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Timeline PageContext omitted traceId."),
            page["traceGenerationId"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Timeline PageContext omitted traceGenerationId."),
            page["toolName"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Timeline PageContext omitted toolName."),
            page["contractVersion"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Timeline PageContext omitted contractVersion."),
            page["symbolContextId"]?.GetValue<string>(),
            page["queryHash"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Timeline PageContext omitted queryHash."),
            page["ordering"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Timeline PageContext omitted ordering."));
        var startIndex = page["startIndex"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Timeline PageContext omitted startIndex.");
        var totalCount = page["totalCount"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Timeline PageContext omitted totalCount.");
        var continuation = runtime.QueryResults.FinalizeTimeline(
            context,
            ReadArgumentString(arguments, "cursor"),
            startIndex,
            rows.Count,
            totalCount,
            lastKey)
            ?? throw new InvalidOperationException(
                "A cursor-marked timeline page must have a continuation.");
        SetTimelineCursor(
            envelope,
            pointer,
            continuation,
            responseBudgetTrimmed);
    }

    private static long ReadExactInt64(JsonObject row, string property)
    {
        if (row[property] is not JsonValue value)
            throw new InvalidOperationException($"Timeline row omitted {property}.");
        if (value.TryGetValue<long>(out var number))
            return number;
        if (value.TryGetValue<int>(out var intNumber))
            return intNumber;
        if (value.TryGetValue<string>(out var text) &&
            long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out number))
        {
            return number;
        }
        throw new InvalidOperationException(
            $"Timeline row field {property} is not a canonical Int64.");
    }

    private static void SetTimelineCursor(
        JsonObject envelope,
        string pointer,
        string cursor,
        bool responseBudgetTrimmed)
    {
        var data = envelope["data"]!.AsObject();
        var rows = ResolveDataPointer(envelope, pointer) as JsonArray
            ?? throw new InvalidOperationException("Timeline page omitted its row section.");
        data["returnedCount"] = rows.Count;
        data["hasMore"] = true;
        data["nextCursor"] = cursor;
        if (data["pageContext"] is JsonObject page)
            page["returnedCount"] = rows.Count;

        var sectionName = SectionName(pointer);
        var section = envelope["sections"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => item["section"]!.GetValue<string>() == sectionName);
        section["returned"] = rows.Count.ToString(CultureInfo.InvariantCulture);
        section["hasMore"] = true;
        section["moreState"] = "present";
        section["continuationAvailable"] = true;
        section["nextCursor"] = cursor;
        if (responseBudgetTrimmed)
            section["truncationReason"] = "response_budget";
        SynchronizeReturnedCounters(envelope, pointer, rows.Count);
    }

    private static void FinalizeInspectTraceCursor(
        JsonObject envelope,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        CapabilityDiscoveryRuntime runtime,
        bool responseBudgetTrimmed)
    {
        var data = envelope["data"]?.AsObject()
            ?? throw new InvalidOperationException("inspect_trace cursor response omitted data.");
        var map = data["traceEvidenceMap"]?.AsObject()
            ?? throw new InvalidOperationException("inspect_trace cursor response omitted TraceEvidenceMap.");
        var page = data["pageContext"]?.AsObject()
            ?? throw new InvalidOperationException("inspect_trace cursor response omitted PageContext.");
        var capabilities = map["capabilities"]?.AsArray()
            ?? throw new InvalidOperationException("inspect_trace cursor response omitted capability assessments.");
        var workflows = map["workflows"]?.AsArray()
            ?? throw new InvalidOperationException("inspect_trace cursor response omitted workflow assessments.");
        var phase = page["phase"]?.GetValue<string>()
            ?? throw new InvalidOperationException("inspect_trace cursor response omitted its phase.");
        var activePointer = phase switch
        {
            "capabilities" => "/traceEvidenceMap/capabilities",
            "workflows" => "/traceEvidenceMap/workflows",
            _ => throw new InvalidOperationException("inspect_trace cursor response has an invalid phase."),
        };
        var lastKey = phase == "capabilities"
            ? capabilities.LastOrDefault()?["capabilityId"]?.GetValue<string>()
            : workflows.LastOrDefault()?["workflowId"]?.GetValue<string>();
        var continuation = runtime.QueryResults.FinalizeInspectTrace(
            data["trace"]?["traceId"]?.GetValue<string>()
                ?? throw new InvalidOperationException("inspect_trace cursor response omitted Trace.TraceId."),
            page["traceGenerationId"]?.GetValue<string>()
                ?? throw new InvalidOperationException("inspect_trace cursor response omitted its trace generation binding."),
            map["catalogVersion"]?.GetValue<string>()
                ?? throw new InvalidOperationException("inspect_trace cursor response omitted its catalog binding."),
            ReadArgumentString(arguments, "domain"),
            ReadArgumentString(arguments, "goal"),
            ReadArgumentString(arguments, "cursor"),
            phase,
            capabilities.Count,
            workflows.Count,
            map["totalCapabilities"]!.GetValue<int>(),
            map["totalWorkflows"]!.GetValue<int>(),
            lastKey)
            ?? throw new InvalidOperationException(
                "A cursor-marked inspect_trace page must have a continuation.");
        var startIndex = page["startIndex"]?.GetValue<int>()
            ?? throw new InvalidOperationException("inspect_trace cursor response omitted its start index.");
        var activeSectionHasMore = phase == "capabilities"
            ? checked(startIndex + capabilities.Count) < map["totalCapabilities"]!.GetValue<int>()
            : checked(startIndex + workflows.Count) < map["totalWorkflows"]!.GetValue<int>();
        SetInspectCursor(
            envelope,
            activePointer,
            continuation,
            activeSectionHasMore,
            responseBudgetTrimmed);
    }

    private static void SetInspectCursor(
        JsonObject envelope,
        string activePointer,
        string cursor,
        bool activeSectionHasMore,
        bool responseBudgetTrimmed)
    {
        var data = envelope["data"]!.AsObject();
        data["hasMore"] = true;
        data["nextCursor"] = cursor;
        envelope["hasMore"] = true;
        envelope["completeness"]!["hasMore"] = true;
        envelope["completeness"]!["status"] = "paged";
        foreach (var section in envelope["sections"]!.AsArray()
                     .Select(item => item!.AsObject())
                     .Where(item => item["section"]?.GetValue<string>() is
                         "traceEvidenceMap.capabilities" or "traceEvidenceMap.workflows"))
        {
            var isActive = string.Equals(
                section["section"]!.GetValue<string>(),
                SectionName(activePointer),
                StringComparison.Ordinal);
            var sectionHasMore = isActive && activeSectionHasMore;
            section["hasMore"] = sectionHasMore;
            section["moreState"] = sectionHasMore ? "present" : "absent";
            section["continuationAvailable"] = sectionHasMore;
            section["nextCursor"] = sectionHasMore ? cursor : null;
            if (sectionHasMore)
                section["truncationReason"] = responseBudgetTrimmed ? "response_budget" : "cursor_page";
            if (!sectionHasMore)
                section["truncationReason"] = null;
        }
        SynchronizeReturnedCounters(envelope, activePointer, retainedRows: null);
    }

    private static void SynchronizeReturnedCounters(
        JsonObject envelope,
        string pointer,
        int? retainedRows)
    {
        var count = retainedRows ?? (ResolveDataPointer(envelope, pointer) as JsonArray)?.Count;
        if (count is null)
            return;
        if (pointer == "/capabilities" && envelope["data"]?["totals"] is JsonObject totals)
            totals["returnedCapabilities"] = count.Value;
        if (pointer == "/traceEvidenceMap/capabilities" &&
            envelope["data"]?["traceEvidenceMap"] is JsonObject evidenceMap)
        {
            evidenceMap["returnedCapabilities"] = count.Value;
        }
        if (pointer == "/traceEvidenceMap/workflows" &&
            envelope["data"]?["traceEvidenceMap"] is JsonObject workflowEvidenceMap)
        {
            workflowEvidenceMap["returnedWorkflows"] = count.Value;
        }
        if (envelope["data"] is JsonObject data &&
            data["pageContext"] is JsonObject pageContext &&
            TimelinePagination.IsTimelineTool(
                pageContext["toolName"]?.GetValue<string>() ?? string.Empty))
        {
            data["returnedCount"] = count.Value;
            pageContext["returnedCount"] = count.Value;
        }
    }

    private static string? ReadArgumentString(
        IReadOnlyDictionary<string, JsonElement>? arguments,
        string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static JsonObject ToResponseTooLargeFailure(
        JsonObject source,
        bool preserveOriginalError = false)
    {
        var failure = source.DeepClone().AsObject();
        failure["status"] = "failed";
        failure["data"] = null;
        if (preserveOriginalError && failure["error"] is JsonObject originalError)
        {
            var code = originalError["code"]?.GetValue<string>() ?? "analysis_failed";
            originalError["message"] = CompactFailureMessage(code);
        }
        else
        {
            failure["error"] = new JsonObject
            {
                ["code"] = "response_too_large",
                ["message"] = "Too large.",
                ["retryable"] = true,
            };
        }
        failure["failedSections"] = new JsonArray();
        failure["sections"] = new JsonArray();
        failure["warnings"] = new JsonArray();
        failure["hasMore"] = false;
        failure["completeness"] = new JsonObject
        {
            ["status"] = "failed",
            ["requestedSectionCount"] = 1,
            ["sectionsWithData"] = 0,
            ["failedSectionCount"] = 0,
            ["hasMore"] = false,
        };
        failure["noData"] = null;
        // This terminal delivery failure produced no analyzable result. A null
        // scope is more truthful and substantially smaller than preserving a
        // selector object that the failed response cannot substantiate.
        failure["scope"] = null;
        ProjectBudgetFailureEvidence(failure);
        failure["precision"] = new JsonObject
        {
            ["identifierPrecision"] = "exact",
            ["metricPrecision"] = "not_applicable",
            ["rounding"] = null,
            ["accounting"] = "none",
            ["denominator"] = null,
        };
        return failure;
    }

    private static string CompactFailureMessage(string code) => code switch
    {
        "invalid_argument" => "Invalid argument.",
        "process_instance_not_found" => "Process not found.",
        "process_start_required" => "Process start required.",
        "ambiguous_process_instance" => "Ambiguous process.",
        "thread_instance_not_found" => "Thread not found.",
        "ambiguous_thread_instance" => "Ambiguous thread.",
        "trace_not_loaded" => "Trace not loaded.",
        "trace_access_denied" => "Trace access denied.",
        "trace_conversion_failed" => "Trace conversion failed.",
        "symbol_context_expired" => "Symbol context expired.",
        "symbol_policy_denied" => "Symbol policy denied.",
        "symbol_resolution_unavailable" => "Symbol resolution unavailable.",
        "invalid_cursor" => "Invalid cursor.",
        "cancelled" => "Cancelled.",
        "budget_exceeded" => "Budget exceeded.",
        "response_too_large" => "Too large.",
        _ => "Analysis failed.",
    };

    private static void ProjectBudgetFailureEvidence(JsonObject failure)
    {
        var capabilityIds = failure["toolRef"]?["capabilityIds"]?.AsArray()
            .Select(node => node?.GetValue<string>() ?? string.Empty)
            .Where(capabilityId => capabilityId.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (capabilityIds.Length == 0)
            throw new InvalidOperationException("A terminal failure omitted its declared capability IDs.");

        // A terminal delivery failure does not deliver an analysis result. Keep a
        // capability assessment for every declared capability, but expose the
        // absence of analysis evidence as an empty, closed reference graph instead
        // of inventing a synthetic trace/provenance item.
        failure["capabilityEvidence"] = new JsonArray(capabilityIds
            .Select(capabilityId => (JsonNode?)new JsonObject
            {
                ["capabilityId"] = capabilityId,
                ["traceStatus"] = "unknown",
                ["scopedStatus"] = "unknown",
                ["totalEventCount"] = null,
                ["matchedEventCount"] = null,
                ["traceEligibleEventCount"] = null,
                ["scopedMatchedEventCount"] = null,
                ["traceEligibleEventCountRepresentation"] = "not_measured",
                ["traceEligibleEventCountScope"] = "not_applicable",
                ["scopedMatchedEventCountScope"] = "not_applicable",
                ["crossScopeRatioDenominatorState"] = "not_defined",
                ["traceCompletedEvidenceCount"] = null,
                ["traceUnmatchedEvidenceCount"] = null,
                ["traceBoundaryEvidenceCount"] = null,
                ["evidenceCompletionState"] = "not_applicable",
                ["captureIntegrity"] = "unknown",
                ["evidenceIds"] = new JsonArray(),
            })
            .ToArray());
        failure["evidenceBoundary"] = new JsonObject
        {
            ["items"] = new JsonArray(),
        };
    }

    private static JsonNode? ResolveDataPointer(JsonObject envelope, string pointer)
    {
        JsonNode? current = envelope["data"];
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current?[segment];
        }
        return current;
    }

    private static string SectionName(string pointer) => pointer.Trim('/').Replace('/', '.');

    private static void ValidateEnvelopeSemantics(JsonObject envelope)
    {
        ValidateEvidenceClosure(envelope);
        var sections = envelope["sections"]!.AsArray().Select(item => item!.AsObject()).ToArray();
        var sectionHasMore = sections.Any(section => section["hasMore"]!.GetValue<bool>());
        var dataHasMore = envelope["data"]?["hasMore"] is JsonValue dataHasMoreValue &&
            dataHasMoreValue.TryGetValue<bool>(out var typedDataHasMore)
                ? typedDataHasMore
                : sectionHasMore;
        if (sectionHasMore && !dataHasMore ||
            envelope["hasMore"]!.GetValue<bool>() != dataHasMore ||
            envelope["completeness"]!["hasMore"]!.GetValue<bool>() != dataHasMore)
            throw new InvalidOperationException("Envelope continuation fields are inconsistent with data and section state.");
        foreach (var section in sections)
        {
            var moreState = section["moreState"]?.GetValue<string>() ??
                throw new InvalidOperationException("A section omitted moreState.");
            var hasMore = section["hasMore"]!.GetValue<bool>();
            if (hasMore != string.Equals(moreState, "present", StringComparison.Ordinal))
                throw new InvalidOperationException("A section hasMore value contradicts moreState.");
            var continuationAvailable = section["continuationAvailable"]?.GetValue<bool>() ??
                throw new InvalidOperationException("A section omitted continuationAvailable.");
            if (continuationAvailable != (section["nextCursor"] is not null))
                throw new InvalidOperationException("A section continuationAvailable value contradicts nextCursor.");
            var returned = ParseCanonicalLong(section["returned"]!);
            var role = section["role"]?.GetValue<string>() ??
                throw new InvalidOperationException("A section omitted its semantic role.");
            var contributesDomainData = role is "domain_data" or "domain_evidence";
            if (returned == 0 && contributesDomainData && section["noData"] is null)
                throw new InvalidOperationException("An empty domain section requires structured noData.");
            if (!contributesDomainData && section["noData"] is not null)
                throw new InvalidOperationException("A support section cannot publish domain noData.");
            if (returned > 0 && section["noData"] is not null)
                throw new InvalidOperationException("A non-empty section cannot publish noData.");
        }

        var textStatus = envelope["status"]!.GetValue<string>();
        if ((textStatus == "failed") != (envelope["data"] is null))
            throw new InvalidOperationException("Failed/data envelope semantics are inconsistent.");
    }

    private static void ValidateEvidenceClosure(JsonObject envelope)
    {
        var declaredCapabilities = ReadStringArray(
            envelope["toolRef"]?["capabilityIds"],
            "toolRef.capabilityIds");
        if (declaredCapabilities.Length != declaredCapabilities.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("toolRef.capabilityIds contains duplicates.");

        var assessments = envelope["capabilityEvidence"]!.AsArray()
            .Select(node => node!.AsObject())
            .ToArray();
        var assessedCapabilities = assessments
            .Select(item => item["capabilityId"]!.GetValue<string>())
            .ToArray();
        if (assessedCapabilities.Length != assessedCapabilities.Distinct(StringComparer.Ordinal).Count() ||
            !declaredCapabilities.ToHashSet(StringComparer.Ordinal)
                .SetEquals(assessedCapabilities))
        {
            throw new InvalidOperationException(
                "capabilityEvidence must cover every and only declared tool capability.");
        }

        var boundaryItems = envelope["evidenceBoundary"]!["items"]!.AsArray()
            .Select(node => node!.AsObject())
            .ToArray();
        var boundaryIds = boundaryItems
            .Select(item => item["evidenceId"]!.GetValue<string>())
            .ToArray();
        if (boundaryIds.Length != boundaryIds.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("evidenceBoundary contains duplicate evidence IDs.");

        var evidenceReferences = assessments
            .SelectMany(item => ReadStringArray(item["evidenceIds"], "capabilityEvidence.evidenceIds"))
            .ToArray();
        if (!boundaryIds.ToHashSet(StringComparer.Ordinal).SetEquals(evidenceReferences))
            throw new InvalidOperationException("Evidence IDs do not close across capabilityEvidence and evidenceBoundary.");

        var evidenceFreeAssessments = assessments
            .Where(item => item["evidenceIds"]!.AsArray().Count == 0)
            .ToArray();
        if (evidenceFreeAssessments.Length == 0)
            return;

        var terminalWithoutAnalysisEvidence =
            envelope["status"]!.GetValue<string>() == "failed" &&
            envelope["scope"] is null &&
            evidenceFreeAssessments.Length == assessments.Length &&
            boundaryItems.Length == 0;
        if (!terminalWithoutAnalysisEvidence)
        {
            throw new InvalidOperationException(
                "Empty evidence references are valid only for a failed, scope-null terminal response with no analysis evidence.");
        }

        foreach (var assessment in evidenceFreeAssessments)
        {
            if (assessment["traceStatus"]!.GetValue<string>() != "unknown" ||
                assessment["scopedStatus"]!.GetValue<string>() != "unknown" ||
                assessment["totalEventCount"] is not null ||
                assessment["matchedEventCount"] is not null ||
                assessment["traceEligibleEventCount"] is not null ||
                assessment["scopedMatchedEventCount"] is not null ||
                assessment["traceEligibleEventCountRepresentation"]!.GetValue<string>() != "not_measured" ||
                assessment["traceEligibleEventCountScope"]!.GetValue<string>() != "not_applicable" ||
                assessment["scopedMatchedEventCountScope"]!.GetValue<string>() != "not_applicable" ||
                assessment["crossScopeRatioDenominatorState"]!.GetValue<string>() != "not_defined" ||
                assessment["traceCompletedEvidenceCount"] is not null ||
                assessment["traceUnmatchedEvidenceCount"] is not null ||
                assessment["traceBoundaryEvidenceCount"] is not null ||
                assessment["evidenceCompletionState"]!.GetValue<string>() != "not_applicable" ||
                assessment["captureIntegrity"]!.GetValue<string>() != "unknown")
            {
                throw new InvalidOperationException(
                    "An evidence-free capability assessment must be wholly unknown, unmeasured, and not applicable to any trace scope.");
            }
        }
    }

    private static string[] ReadStringArray(JsonNode? node, string context)
    {
        if (node is not JsonArray array)
            throw new InvalidOperationException($"{context} must be an array.");
        return array
            .Select(item => item?.GetValue<string>() ??
                throw new InvalidOperationException($"{context} contains null."))
            .ToArray();
    }

    private static long ParseCanonicalLong(JsonNode node)
    {
        var value = node.GetValue<string>();
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidOperationException("Expected canonical Int64 wire string.");
        return parsed;
    }

    private sealed record BuildResult(CallToolResult Result, int FrameBytes);
}
