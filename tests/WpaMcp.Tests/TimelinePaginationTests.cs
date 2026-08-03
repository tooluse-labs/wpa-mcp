using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;
using WpaMcp.Tools;

namespace WpaMcp.Tests;

public sealed class TimelinePaginationTests
{
    [Fact]
    public void Slice_TraversesFirstMiddleAndTerminalPagesWithDuplicateTimestamps()
    {
        ThreadLifetimeRow[] rows =
        [
            Row(tid: 4, generation: 1),
            Row(tid: 4, generation: 2),
            Row(tid: 5, generation: 1),
            Row(tid: 6, generation: 1),
            Row(tid: 7, generation: 1),
        ];

        var first = TimelinePagination.Slice(
            rows,
            new QueryResultCursorPosition(TimelinePagination.Phase, 0, null),
            pageSize: 2,
            TimelinePagination.ThreadKey);
        Assert.Equal([4, 4], first.Rows.Select(row => row.Tid));
        Assert.Equal([1L, 2L], first.Rows.Select(row => row.ThreadGeneration));
        Assert.Equal(5, first.TotalCount);
        Assert.True(first.HasMore);

        var middle = TimelinePagination.Slice(
            rows,
            new QueryResultCursorPosition(
                TimelinePagination.Phase,
                2,
                TimelinePagination.ThreadKey(first.Rows[^1])),
            pageSize: 2,
            TimelinePagination.ThreadKey);
        Assert.Equal([5, 6], middle.Rows.Select(row => row.Tid));
        Assert.True(middle.HasMore);

        var terminal = TimelinePagination.Slice(
            rows,
            new QueryResultCursorPosition(
                TimelinePagination.Phase,
                4,
                TimelinePagination.ThreadKey(middle.Rows[^1])),
            pageSize: 2,
            TimelinePagination.ThreadKey);
        Assert.Equal([7], terminal.Rows.Select(row => row.Tid));
        Assert.False(terminal.HasMore);
        Assert.Equal(4, terminal.StartIndex);
    }

    [Fact]
    public void Slice_RejectsWrongReplayKeyAndNonUniqueStableKey()
    {
        var rows = new[] { Row(4, 1), Row(4, 2) };
        var error = Assert.Throws<QueryResultCursorException>(() =>
            TimelinePagination.Slice(
                rows,
                new QueryResultCursorPosition(TimelinePagination.Phase, 1, "wrong"),
                1,
                TimelinePagination.ThreadKey));
        Assert.Equal("invalid_cursor", ContractMcpServerTool.MapException(error).Code);

        var duplicate = new[] { Row(4, 1), Row(4, 1) };
        Assert.Throws<InvalidOperationException>(() => TimelinePagination.Slice(
            duplicate,
            new QueryResultCursorPosition(TimelinePagination.Phase, 0, null),
            1,
            TimelinePagination.ThreadKey));
    }

    [Fact]
    public void Coordinator_BindsEveryContextDimensionAndAdvancesExactly()
    {
        var registry = new QueryResultCursorRegistry();
        var coordinator = new QueryResultCursorCoordinator(
            "principal-a",
            "off",
            registry);
        var context = Context();
        var cursor = Assert.IsType<string>(coordinator.FinalizeTimeline(
            context,
            sourceCursor: null,
            startIndex: 0,
            retainedRows: 2,
            totalRows: 5,
            lastKey: "k2"));
        Assert.True(QueryResultCursorRegistry.HasCanonicalShape(cursor));
        Assert.Equal(
            new QueryResultCursorPosition(TimelinePagination.Phase, 2, "k2"),
            coordinator.ResolveTimeline(context, cursor));

        foreach (var mismatch in new[]
                 {
                     context with { TraceId = "trace-b" },
                     context with { TraceGenerationId = "generation-b" },
                     context with { ToolName = TimelinePagination.ImageLoadTimingTool },
                     context with { ContractVersion = "2.1" },
                     context with { SymbolContextId = "symbol-b" },
                     context with { QueryHash = new string('b', 64) },
                     context with { Ordering = TimelinePagination.ImageLoadTimingOrdering },
                 })
        {
            AssertInvalid(() => coordinator.ResolveTimeline(mismatch, cursor));
        }
        AssertInvalid(() => new QueryResultCursorCoordinator(
            "principal-b", "off", registry).ResolveTimeline(context, cursor));
        AssertInvalid(() => new QueryResultCursorCoordinator(
            "principal-a", "strict", registry).ResolveTimeline(context, cursor));

        var middleCursor = Assert.IsType<string>(coordinator.FinalizeTimeline(
            context, cursor, 2, 2, 5, "k4"));
        Assert.Equal(4, coordinator.ResolveTimeline(context, middleCursor).Index);
        Assert.Null(coordinator.FinalizeTimeline(
            context, middleCursor, 4, 1, 5, "k5"));
    }

    [Fact]
    public async Task ProductionThreadLifetime_FrameTrimContinuationHasNoLossOrDuplication()
    {
        const string fixture = "fixtures/small_cpu.etl";
        const int frameBudget = 12_000;
        if (!File.Exists(fixture)) return;
        using var traceRuntime = TraceLifecycleProductionTests.TestRuntime.Create();
        var loaded = traceRuntime.Registry.Load(traceRuntime.Principal, fixture);
        var cache = traceRuntime.Cache;
        var trace = cache.Get(fixture);
        var selected = new MetaTools(cache).ListProcesses(fixture, top: 1000).Rows
            .Select(row => new
            {
                Row = row,
                Result = ThreadLifetimeAnalysis.Analyze(
                    trace, row.Pid, 1000, row.StartUs),
            })
            .OrderByDescending(item => item.Result.TotalThreads)
            .First();
        Assert.True(selected.Result.TotalThreads > 2);

        var catalog = ActiveToolCatalog.LoadAndValidate();
        var definition = catalog.Tools.Single(candidate =>
            candidate.ToolName == TimelinePagination.ThreadLifetimeTool);
        var reviewArguments = new Dictionary<string, JsonElement>
        {
            ["traceId"] = JsonSerializer.SerializeToElement(loaded.TraceId),
            ["pid"] = JsonSerializer.SerializeToElement(selected.Row.Pid),
            ["processStartUs"] = JsonSerializer.SerializeToElement(
                selected.Row.StartUs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ["pageSize"] = JsonSerializer.SerializeToElement(1000),
        };
        var rawDomain = JsonSerializer.SerializeToNode(
            new MetaTools(cache).ThreadLifetime(
                fixture,
                selected.Row.Pid,
                1000,
                selected.Row.StartUs),
            McpJsonUtilities.DefaultOptions)!;
        _ = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(definition, reviewArguments)
            .Adapt(rawDomain);
        var servicesCollection = new ServiceCollection();
        servicesCollection.AddSingleton(cache);
        servicesCollection.AddSingleton(new TraceToolRuntime(
            traceRuntime.Lifecycle,
            traceRuntime.Registry,
            traceRuntime.SessionPrincipal));
        servicesCollection.AddSingleton<SymbolService>();
        servicesCollection.AddSingleton(new CapabilityDiscoveryRuntime(
            catalog,
            traceRuntime.SessionPrincipal,
            maxResponseFrameBytes: frameBudget));
        using var services = servicesCollection.BuildServiceProvider();
        var tool = catalog.CreateServerTools(
                services,
                responseBudget: new ToolResponseBudgetOptions(frameBudget))
            .Single(candidate => candidate.ProtocolTool.Name ==
                TimelinePagination.ThreadLifetimeTool);
        var server = new Mock<McpServer>();
        server.SetupGet(candidate => candidate.Services).Returns(services);
        var seen = new List<string>();
        string? cursor = null;
        var pageNumber = 0;
        using var resolved = new TraceReferenceResolver(traceRuntime.Registry)
            .ResolveQuery(
                traceRuntime.Principal,
                loaded.TraceId,
                TraceAccessMode.IdOnly,
                CancellationToken.None);
        using var execution = TraceQueryExecutionContext.Begin(
            traceRuntime.Cache,
            loaded.TraceId,
            resolved,
            CancellationToken.None);
        do
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                ["traceId"] = JsonSerializer.SerializeToElement(loaded.TraceId),
                ["pid"] = JsonSerializer.SerializeToElement(selected.Row.Pid),
                ["processStartUs"] = JsonSerializer.SerializeToElement(
                    selected.Row.StartUs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ["pageSize"] = JsonSerializer.SerializeToElement(1000),
            };
            if (cursor is not null)
                arguments["cursor"] = JsonSerializer.SerializeToElement(cursor);
            var parameters = new CallToolRequestParams
            {
                Name = TimelinePagination.ThreadLifetimeTool,
                Arguments = arguments,
            };
            var request = new JsonRpcRequest
            {
                Id = new RequestId(new string('t', 64)),
                Method = RequestMethods.ToolsCall,
                Params = JsonSerializer.SerializeToNode(
                    parameters,
                    McpJsonUtilities.DefaultOptions),
            };
            var result = await tool.InvokeAsync(
                new RequestContext<CallToolRequestParams>(
                    server.Object,
                    request,
                    parameters),
                CancellationToken.None);
            Assert.False(result.IsError, result.StructuredContent?.GetRawText());
            Assert.True(ToolResponseFrameFitter.MeasureFrame(request.Id, result) <= frameBudget);
            var envelope = JsonNode.Parse(
                result.StructuredContent!.Value.GetRawText())!.AsObject();
            var data = envelope["data"]!.AsObject();
            var page = data["pageContext"]!.AsObject();
            var rows = data["threads"]!.AsArray();
            Assert.Equal(seen.Count, page["startIndex"]!.GetValue<int>());
            Assert.Equal(rows.Count, page["returnedCount"]!.GetValue<int>());
            Assert.Equal(rows.Count, data["returnedCount"]!.GetValue<int>());
            Assert.Equal(selected.Result.TotalThreads, page["totalCount"]!.GetValue<int>());
            seen.AddRange(rows.Select(row => TimelinePagination.ThreadKey(
                long.Parse(row!["startTimeUs"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture),
                row["tid"]!.GetValue<int>(),
                long.Parse(row["threadGeneration"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture))));
            cursor = data["nextCursor"]?.GetValue<string>();
            Assert.Equal(cursor is not null, data["hasMore"]!.GetValue<bool>());
            if (cursor is not null)
                Assert.True(QueryResultCursorRegistry.HasCanonicalShape(cursor));
            pageNumber++;
            Assert.True(pageNumber < 64);
        } while (cursor is not null);

        Assert.Equal(selected.Result.TotalThreads, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.True(pageNumber > 1, "The configured frame budget must exercise continuation after fitting.");
    }

    [Fact]
    public async Task ProductionListProcesses_FrameTrimContinuationHasNoLossOrDuplication()
    {
        const string fixture = "fixtures/small_cpu.etl";
        const int frameBudget = 12_000;
        if (!File.Exists(fixture)) return;
        using var traceRuntime = TraceLifecycleProductionTests.TestRuntime.Create();
        var loaded = traceRuntime.Registry.Load(traceRuntime.Principal, fixture);
        var cache = traceRuntime.Cache;
        var expected = new MetaTools(cache)
            .ListProcesses(fixture, top: 1000, includeSystem: true)
            .Rows
            .Select(TimelinePagination.ProcessKey)
            .ToArray();
        Assert.True(expected.Length > 2);

        var catalog = ActiveToolCatalog.LoadAndValidate();
        var definition = catalog.Tools.Single(candidate =>
            candidate.ToolName == TimelinePagination.ListProcessesTool);
        Assert.Equal("cursor", definition.PaginationMode);
        var servicesCollection = new ServiceCollection();
        servicesCollection.AddSingleton(cache);
        servicesCollection.AddSingleton(new TraceToolRuntime(
            traceRuntime.Lifecycle,
            traceRuntime.Registry,
            traceRuntime.SessionPrincipal));
        servicesCollection.AddSingleton<SymbolService>();
        servicesCollection.AddSingleton(new CapabilityDiscoveryRuntime(
            catalog,
            traceRuntime.SessionPrincipal,
            maxResponseFrameBytes: frameBudget));
        using var services = servicesCollection.BuildServiceProvider();
        var tool = catalog.CreateServerTools(
                services,
                responseBudget: new ToolResponseBudgetOptions(frameBudget))
            .Single(candidate => candidate.ProtocolTool.Name ==
                TimelinePagination.ListProcessesTool);
        var server = new Mock<McpServer>();
        server.SetupGet(candidate => candidate.Services).Returns(services);
        var seen = new List<string>();
        string? cursor = null;
        var pageNumber = 0;
        var mismatchChecked = false;
        using var resolved = new TraceReferenceResolver(traceRuntime.Registry)
            .ResolveQuery(
                traceRuntime.Principal,
                loaded.TraceId,
                TraceAccessMode.IdOnly,
                CancellationToken.None);
        using var execution = TraceQueryExecutionContext.Begin(
            traceRuntime.Cache,
            loaded.TraceId,
            resolved,
            CancellationToken.None);
        do
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                ["traceId"] = JsonSerializer.SerializeToElement(loaded.TraceId),
                ["orderBy"] = JsonSerializer.SerializeToElement("cpu"),
                ["top"] = JsonSerializer.SerializeToElement(1000),
                ["includeSystem"] = JsonSerializer.SerializeToElement(true),
            };
            if (cursor is not null)
                arguments["cursor"] = JsonSerializer.SerializeToElement(cursor);
            var result = await InvokeAsync(tool, server.Object, arguments, pageNumber);
            Assert.False(result.IsError, result.StructuredContent?.GetRawText());
            Assert.True(ToolResponseFrameFitter.MeasureFrame(
                new RequestId($"process-page-{pageNumber}"), result) <= frameBudget);
            var envelope = JsonNode.Parse(
                result.StructuredContent!.Value.GetRawText())!.AsObject();
            var data = envelope["data"]!.AsObject();
            var page = data["pageContext"]!.AsObject();
            var rows = data["rows"]!.AsArray();
            Assert.Equal(seen.Count, page["startIndex"]!.GetValue<int>());
            Assert.Equal(rows.Count, page["returnedCount"]!.GetValue<int>());
            Assert.Equal(rows.Count, data["returnedCount"]!.GetValue<int>());
            Assert.Equal(expected.Length, page["totalCount"]!.GetValue<int>());
            Assert.Equal(expected.Length, data["totalCount"]!.GetValue<int>());
            seen.AddRange(rows.Select(row => TimelinePagination.ProcessKey(
                row!["pid"]!.GetValue<int>(),
                long.Parse(
                    row["startUs"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture))));
            cursor = data["nextCursor"]?.GetValue<string>();
            Assert.Equal(cursor is not null, data["hasMore"]!.GetValue<bool>());
            if (cursor is not null)
            {
                Assert.True(QueryResultCursorRegistry.HasCanonicalShape(cursor));
                if (!mismatchChecked)
                {
                    var mismatched = new Dictionary<string, JsonElement>(arguments)
                    {
                        ["orderBy"] = JsonSerializer.SerializeToElement("wall"),
                        ["cursor"] = JsonSerializer.SerializeToElement(cursor),
                    };
                    var rejected = await InvokeAsync(
                        tool,
                        server.Object,
                        mismatched,
                        pageNumber + 1000);
                    Assert.True(rejected.IsError);
                    var failure = JsonNode.Parse(
                        rejected.StructuredContent!.Value.GetRawText())!.AsObject();
                    Assert.Equal(
                        "invalid_cursor",
                        failure["error"]!["code"]!.GetValue<string>());
                    mismatchChecked = true;
                }
            }
            pageNumber++;
            Assert.True(
                pageNumber <= expected.Length,
                "Every successful page must advance by at least one unique process lifetime.");
        } while (cursor is not null);

        Assert.Equal(expected, seen);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.True(pageNumber > 1, "The configured frame budget must exercise continuation after fitting.");
        Assert.True(mismatchChecked);

        static Task<CallToolResult> InvokeAsync(
            McpServerTool tool,
            McpServer server,
            Dictionary<string, JsonElement> arguments,
            int requestOrdinal)
        {
            var parameters = new CallToolRequestParams
            {
                Name = TimelinePagination.ListProcessesTool,
                Arguments = arguments,
            };
            var request = new JsonRpcRequest
            {
                Id = new RequestId($"process-page-{requestOrdinal}"),
                Method = RequestMethods.ToolsCall,
                Params = JsonSerializer.SerializeToNode(
                    parameters,
                    McpJsonUtilities.DefaultOptions),
            };
            return tool.InvokeAsync(
                new RequestContext<CallToolRequestParams>(server, request, parameters),
                CancellationToken.None).AsTask();
        }
    }

    private static ThreadLifetimeRow Row(int tid, long generation) => new(
        tid,
        StartTimeUs: 10,
        EndTimeUs: 20,
        LifetimeUs: 10,
        TraceResidentStart: false,
        TraceResidentEnd: false,
        ProcessStartUs: 1,
        ThreadGeneration: generation);

    private static TimelineQueryContext Context() => new(
        "trace-a",
        "generation-a",
        TimelinePagination.ThreadLifetimeTool,
        ToolContractVersions.V2,
        "symbol-a",
        new string('a', 64),
        TimelinePagination.ThreadLifetimeOrdering);

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<QueryResultCursorException>(action);
        Assert.Equal(QueryResultCursorFailureKind.Invalid, error.Kind);
        Assert.Equal("invalid_cursor", ContractMcpServerTool.MapException(error).Code);
    }
}
