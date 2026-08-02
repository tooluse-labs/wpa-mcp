using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Tests;

public sealed class TraceQueryExecutionFiltersTests
{
    [Fact]
    public async Task CompatibilityHandle_CommitsOnlyAfterFinalSuccessfulTransport()
    {
        using var runtime = TraceLifecycleProductionTests.TestRuntime.Create();
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");
        var filters = CreateFilters(runtime);
        string? traceId = null;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            traceId = TraceQueryExecutionContext.CurrentReference?.TraceId;
            using var lease = runtime.Cache.Acquire(source);
            Assert.True(lease.Trace.EventCount > 0);
            return Task.CompletedTask;
        });
        var request = ToolRequest(101, source);

        await incoming(Context(request), CancellationToken.None);

        Assert.NotNull(traceId);
        Assert.Equal(1, filters.PendingCompatibilityHandleCount);
        using (var lease = runtime.Registry.Acquire(runtime.Principal, traceId!))
            Assert.True(lease.Trace.EventCount > 0);

        var response = SuccessfulResponse(101);
        var outgoing = filters.CreateOutgoingFilter()(static (_, _) => Task.CompletedTask);
        await outgoing(Context(response), CancellationToken.None);

        Assert.Equal(0, filters.PendingCompatibilityHandleCount);
        using var committed = runtime.Registry.Acquire(runtime.Principal, traceId!);
        Assert.True(committed.Trace.EventCount > 0);
        runtime.Registry.Unload(runtime.Principal, traceId!);
    }

    [Fact]
    public async Task CompatibilityHandle_RollsBackWhenLaterFilterChangesResultToError()
    {
        using var runtime = TraceLifecycleProductionTests.TestRuntime.Create();
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");
        var filters = CreateFilters(runtime);
        string? traceId = null;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            traceId = TraceQueryExecutionContext.CurrentReference?.TraceId;
            return Task.CompletedTask;
        });
        await incoming(Context(ToolRequest(102, source)), CancellationToken.None);

        var response = SuccessfulResponse(102);
        var outgoing = filters.CreateOutgoingFilter()((context, _) =>
        {
            var result = Assert.IsType<JsonObject>(
                Assert.IsType<JsonRpcResponse>(context.JsonRpcMessage).Result);
            result["isError"] = true;
            return Task.CompletedTask;
        });
        await outgoing(Context(response), CancellationToken.None);

        Assert.Equal(0, filters.PendingCompatibilityHandleCount);
        AssertRetired(runtime, traceId!);
    }

    [Fact]
    public async Task CompatibilityHandle_RollsBackWhenTransportThrows()
    {
        using var runtime = TraceLifecycleProductionTests.TestRuntime.Create();
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");
        var filters = CreateFilters(runtime);
        string? traceId = null;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            traceId = TraceQueryExecutionContext.CurrentReference?.TraceId;
            return Task.CompletedTask;
        });
        await incoming(Context(ToolRequest(103, source)), CancellationToken.None);
        var outgoing = filters.CreateOutgoingFilter()(
            static (_, _) => throw new IOException("synthetic transport failure"));

        await Assert.ThrowsAsync<IOException>(() => outgoing(
            Context(SuccessfulResponse(103)),
            CancellationToken.None));

        Assert.Equal(0, filters.PendingCompatibilityHandleCount);
        AssertRetired(runtime, traceId!);
    }

    [Fact]
    public async Task CompatibilityHandle_RollsBackWhenHandlerThrows()
    {
        using var runtime = TraceLifecycleProductionTests.TestRuntime.Create();
        var source = runtime.CopyFixture("small_cpu.etl", "trace.etl");
        var filters = CreateFilters(runtime);
        string? traceId = null;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            traceId = TraceQueryExecutionContext.CurrentReference?.TraceId;
            throw new InvalidOperationException("synthetic handler failure");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => incoming(
            Context(ToolRequest(104, source)),
            CancellationToken.None));

        Assert.Equal(0, filters.PendingCompatibilityHandleCount);
        AssertRetired(runtime, traceId!);
    }

    [Fact]
    public async Task BoundTraceArguments_AreExactAndConcurrentContextsDoNotCrossTalk()
    {
        using var runtime = TraceLifecycleProductionTests.TestRuntime.Create();
        var first = runtime.Lifecycle.Load(
            runtime.Principal,
            runtime.CopyFixture("small_cpu.etl", "first.etl"));
        var second = runtime.Lifecycle.Load(
            runtime.Principal,
            runtime.CopyFixture("small_fileio.etl", "second.etl"));
        var resolver = new TraceReferenceResolver(runtime.Registry, runtime.Lifecycle);
        using var barrier = new Barrier(2);

        async Task<long> ReadAsync(string ownId, string otherId)
        {
            using var resolved = resolver.ResolveQuery(
                runtime.Principal,
                ownId,
                TraceAccessMode.IdOnly);
            using var scope = TraceQueryExecutionContext.Begin(
                runtime.Cache,
                ownId,
                resolved);
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            await Task.Yield();
            Assert.Equal(ownId, TraceQueryExecutionContext.CurrentReference?.TraceId);
            Assert.NotNull(TraceQueryExecutionContext.CurrentCacheGenerationSequence);
            using var lease = runtime.Cache.Acquire(ownId);
            Assert.Throws<FileNotFoundException>(() => runtime.Cache.Acquire(otherId));
            return lease.Trace.EventCount;
        }

        var counts = await Task.WhenAll(
            Task.Run(() => ReadAsync(first.Handle.TraceId, second.Handle.TraceId)),
            Task.Run(() => ReadAsync(second.Handle.TraceId, first.Handle.TraceId)));

        Assert.All(counts, count => Assert.True(count > 0));
        Assert.Null(TraceQueryExecutionContext.CurrentReference);
        runtime.Registry.Unload(runtime.Principal, first.Handle.TraceId);
        runtime.Registry.Unload(runtime.Principal, second.Handle.TraceId);
    }

    [Fact]
    public async Task NonStringPath_BypassesResolutionWithoutPathIo()
    {
        using var runtime = TraceLifecycleProductionTests.TestRuntime.Create();
        var filters = CreateFilters(runtime);
        var nextCalls = 0;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            Interlocked.Increment(ref nextCalls);
            Assert.Null(TraceQueryExecutionContext.CurrentReference);
            return Task.CompletedTask;
        });
        JsonNode?[] malformed =
        [
            JsonValue.Create(123),
            new JsonObject { ["nested"] = true },
            null,
        ];

        for (var index = 0; index < malformed.Length; index++)
        {
            await incoming(
                Context(ToolRequest(200 + index, malformed[index])),
                CancellationToken.None);
        }

        Assert.Equal(malformed.Length, Volatile.Read(ref nextCalls));
        Assert.False(runtime.Store.ArtifactRootCreated);
        Assert.False(Directory.Exists(runtime.ArtifactRoot));
        Assert.Equal(0, runtime.Store.SnapshotCopyCount);
        Assert.Equal(0, filters.PendingCompatibilityHandleCount);
    }

    [Fact]
    public async Task ContractFilter_ClassifiesNonStringPathBeforeSdkBinding()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var serverTools = catalog.CreateServerTools(new DeferredCatalogServiceProvider());
        var filters = new ToolContractMessageFilters(
            catalog.Tools,
            serverTools,
            ToolExecutionBudgetOptions.FromEnvironment(_ => null));
        var reachedNext = false;
        var incoming = filters.CreateIncomingFilter()((context, _) =>
        {
            reachedNext = true;
            Assert.True(ToolContractMessageFilters.TryGetPreDispatchFailure(
                context,
                out var failure));
            Assert.Equal("invalid_argument", failure.Error.Code);
            return Task.CompletedTask;
        });

        await incoming(
            Context(ToolRequest(250, JsonValue.Create(123), "inspect_trace")),
            CancellationToken.None);

        Assert.True(reachedNext);
    }

    private static TraceQueryExecutionFilters CreateFilters(
        TraceLifecycleProductionTests.TestRuntime runtime)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var method = typeof(PathProbe).GetMethod(nameof(PathProbe.Query))!;
        var tool = ActiveToolCatalog.CreateServerTool(
            method,
            new McpServerToolCreateOptions { Services = services });
        return new TraceQueryExecutionFilters(
            [tool.ProtocolTool],
            new TraceReferenceResolver(runtime.Registry, runtime.Lifecycle),
            runtime.Cache,
            runtime.SessionPrincipal,
            TraceAccessMode.Compatibility);
    }

    private static JsonRpcRequest ToolRequest(long id, string path) => new()
    {
        Id = new RequestId(id),
        Method = RequestMethods.ToolsCall,
        Params = JsonSerializer.SerializeToNode(
            new CallToolRequestParams
            {
                Name = "path_probe",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(path),
                },
            },
            McpJsonUtilities.DefaultOptions),
    };

    private static JsonRpcRequest ToolRequest(
        long id,
        JsonNode? path,
        string toolName = "path_probe") => new()
    {
        Id = new RequestId(id),
        Method = RequestMethods.ToolsCall,
        Params = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = new JsonObject { ["path"] = path?.DeepClone() },
        },
    };

    private static JsonRpcResponse SuccessfulResponse(long id) => new()
    {
        Id = new RequestId(id),
        Result = new JsonObject { ["isError"] = false },
    };

    private static MessageContext Context(JsonRpcMessage message) =>
        new(Mock.Of<McpServer>(), message);

    private static void AssertRetired(
        TraceLifecycleProductionTests.TestRuntime runtime,
        string traceId)
    {
        var error = Assert.Throws<TraceReferenceException>(() =>
            runtime.Registry.Acquire(runtime.Principal, traceId));
        Assert.Equal("unloaded", error.DetailCode);
    }

    [McpServerToolType]
    private sealed class PathProbe
    {
        [McpServerTool(
            Name = "path_probe",
            ReadOnly = true,
            Idempotent = true,
            OpenWorld = false,
            Destructive = false)]
        [Description("Test-only trace query.")]
        public string Query(
            [Description("Canonical TraceId returned by load_trace")]
            string path) => path;
    }
}
