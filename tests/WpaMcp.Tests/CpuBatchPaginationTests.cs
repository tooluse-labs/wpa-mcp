using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class CpuBatchPaginationTests
{
    [Fact]
    public async Task RealEtl_OversizedBatchTraversesOneSnapshotWithoutResponseTooLarge()
    {
        var source = Environment.GetEnvironmentVariable("WPAMCP_REAL_ETL");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;

        const int frameBudget = ToolResponseBudgetOptions.DefaultMaxResponseFrameBytes;
        int[] pids = [13424, 5964, 25968, 20548, 4448, 6272, 20636, 4024, 24168, 19444, 11756, 17252];
        using var traceRuntime = TraceLifecycleProductionTests.TestRuntime.Create();
        var loaded = traceRuntime.Registry.Load(traceRuntime.Principal, source);
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var capabilityRuntime = new CapabilityDiscoveryRuntime(
            catalog,
            traceRuntime.SessionPrincipal,
            maxResponseFrameBytes: frameBudget);
        var servicesCollection = new ServiceCollection();
        servicesCollection.AddSingleton(traceRuntime.Cache);
        servicesCollection.AddSingleton(new TraceToolRuntime(
            traceRuntime.Lifecycle,
            traceRuntime.Registry,
            traceRuntime.SessionPrincipal));
        servicesCollection.AddSingleton<SymbolService>();
        servicesCollection.AddSingleton(capabilityRuntime);
        using var services = servicesCollection.BuildServiceProvider();
        var tool = catalog.CreateServerTools(
                services,
                responseBudget: new ToolResponseBudgetOptions(frameBudget))
            .Single(candidate => candidate.ProtocolTool.Name ==
                TimelinePagination.CpuTopFunctionsBatchTool);
        var server = new Mock<McpServer>();
        server.SetupGet(candidate => candidate.Services).Returns(services);
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

        var seen = new List<int>();
        var pageFrameBytes = new List<int>();
        var pageReturnedCounts = new List<int>();
        string? cursor = null;
        string? resultSetId = null;
        var pageNumber = 0;
        var stopwatch = Stopwatch.StartNew();
        do
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(loaded.TraceId),
                ["pids"] = JsonSerializer.SerializeToElement(pids),
                ["top"] = JsonSerializer.SerializeToElement(12),
                ["excludeEtwSelfOverhead"] = JsonSerializer.SerializeToElement(true),
                ["resolveSymbols"] = JsonSerializer.SerializeToElement(false),
                ["timeBudgetMs"] = JsonSerializer.SerializeToElement(100_000),
                ["pageSize"] = JsonSerializer.SerializeToElement(100),
            };
            if (cursor is not null)
                arguments["cursor"] = JsonSerializer.SerializeToElement(cursor);
            var parameters = new CallToolRequestParams
            {
                Name = TimelinePagination.CpuTopFunctionsBatchTool,
                Arguments = arguments,
            };
            var request = new JsonRpcRequest
            {
                Id = new RequestId($"cpu-batch-real-{pageNumber}"),
                Method = RequestMethods.ToolsCall,
                Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
            };
            var result = await tool.InvokeAsync(
                new RequestContext<CallToolRequestParams>(server.Object, request, parameters),
                CancellationToken.None);
            Assert.False(result.IsError, result.StructuredContent?.GetRawText());
            var frameBytes = ToolResponseFrameFitter.MeasureFrame(request.Id, result);
            Assert.True(frameBytes <= frameBudget);
            pageFrameBytes.Add(frameBytes);
            var envelope = JsonNode.Parse(
                result.StructuredContent!.Value.GetRawText())!.AsObject();
            Assert.NotEqual("response_too_large", envelope["error"]?["code"]?.GetValue<string>());
            var data = envelope["data"]!.AsObject();
            var pageRows = data["scopeResults"]!.AsArray();
            pageReturnedCounts.Add(pageRows.Count);
            seen.AddRange(pageRows.Select(row => row!["pid"]!.GetValue<int>()));
            var currentResultSetId = data["resultSetId"]!.GetValue<string>();
            resultSetId ??= currentResultSetId;
            Assert.Equal(resultSetId, currentResultSetId);
            cursor = data["nextCursor"]?.GetValue<string>();
            Assert.Equal(cursor is not null, data["hasMore"]!.GetValue<bool>());
            pageNumber++;
            Assert.True(pageNumber <= pids.Length);
        } while (cursor is not null);
        stopwatch.Stop();

        Assert.Equal(pids, seen);
        Assert.Equal(pids.Length, seen.Distinct().Count());
        Assert.True(pageNumber > 1, "The real regression trace must exercise frame continuation.");
        Assert.Equal(1, CpuBatchPaginationRuntime.For(capabilityRuntime).AnalysisSnapshotCount);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Observation = "cpu_batch_snapshot_pagination",
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            PageCount = pageNumber,
            PageReturnedCounts = pageReturnedCounts,
            PageFrameBytes = pageFrameBytes,
            RequestedPidCount = pids.Length,
            AnalysisSnapshotCount = 1,
        }));
    }

    [Fact]
    public void SnapshotPages_AreStableRetryableAndBoundToTheFullQueryContext()
    {
        var coordinator = new QueryResultCursorCoordinator(
            "principal-a",
            "off",
            new QueryResultCursorRegistry());
        var runtime = new CpuBatchPaginationRuntime(coordinator);
        var context = new TimelineQueryContext(
            "trace-a",
            "generation-a",
            TimelinePagination.CpuTopFunctionsBatchTool,
            ToolContractVersions.V2,
            null,
            new string('a', 64),
            TimelinePagination.CpuTopFunctionsBatchOrdering);
        var rows = Enumerable.Range(0, 5)
            .Select(index => new CpuBatchScopeResult(
                100 + index,
                null,
                "scope_not_found",
                "scope_not_found",
                "unresolved",
                null,
                false,
                [],
                0,
                "unknown",
                "scope_not_found"))
            .ToArray();
        var complete = new CpuTopFunctionsBatchResponse(
            ScopeResults: rows,
            Warnings: ["snapshot warning"],
            Partial: false,
            RequestedPidCount: rows.Length,
            CompletedPidCount: 0);

        var first = runtime.Start(context, complete, pageSize: 2);
        Assert.Equal([100, 101], first.ScopeResults.Select(row => row.Pid));
        Assert.Equal(["snapshot warning"], first.Warnings);
        Assert.True(first.HasMore);
        Assert.StartsWith("cbr_", first.ResultSetId, StringComparison.Ordinal);

        var cursor = Assert.IsType<string>(coordinator.FinalizeTimeline(
            context,
            sourceCursor: null,
            startIndex: 0,
            retainedRows: first.ReturnedCount,
            totalRows: rows.Length,
            lastKey: first.ResultSetId!));
        var second = runtime.Resume(context, cursor, pageSize: 2);
        var retry = runtime.Resume(context, cursor, pageSize: 2);
        Assert.Equal([102, 103], second.ScopeResults.Select(row => row.Pid));
        Assert.Equal(
            second.ScopeResults.Select(row => row.Pid),
            retry.ScopeResults.Select(row => row.Pid));
        Assert.Equal(["snapshot warning"], second.Warnings);
        Assert.Equal(first.ResultSetId, second.ResultSetId);
        Assert.Equal(second.ResultSetId, retry.ResultSetId);

        var mismatch = context with { QueryHash = new string('b', 64) };
        var error = Assert.Throws<QueryResultCursorException>(() =>
            runtime.Resume(mismatch, cursor, pageSize: 2));
        Assert.Equal(QueryResultCursorFailureKind.Invalid, error.Kind);
    }

    [Fact]
    public void SnapshotRegistry_IsBoundedWithoutEvictingLiveContinuations()
    {
        var registry = new CpuBatchResultSnapshotRegistry(maxEntries: 1);
        var context = new TimelineQueryContext(
            "trace-a",
            "generation-a",
            TimelinePagination.CpuTopFunctionsBatchTool,
            ToolContractVersions.V2,
            null,
            new string('a', 64),
            TimelinePagination.CpuTopFunctionsBatchOrdering);
        var snapshot = new CpuBatchResultSnapshot([], [], false, null, 0, 0);
        var first = registry.Store(context, snapshot);

        var error = Assert.Throws<QueryResultCursorException>(() =>
            registry.Store(context, snapshot));
        Assert.Equal(QueryResultCursorFailureKind.RegistryCapacity, error.Kind);
        Assert.Same(snapshot, registry.Get(first, context));
    }
}
