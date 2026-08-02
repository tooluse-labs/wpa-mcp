using System.Text.Json;
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

public sealed class SymbolContextProductionTests
{
    [Fact]
    public void ActiveCatalog_AdvertisesReviewedContextOverlayAndSecureLifecycleSurface()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        using var services = new ServiceCollection()
            .AddSingleton(_ => new TraceCache())
            .AddSingleton<SymbolService>()
            .BuildServiceProvider();
        var tools = catalog.CreateProtocolTools(services);
        var contextBound = tools.Where(tool =>
            HasProperty(tool.InputSchema, "resolveSymbols")).ToArray();

        Assert.Equal(SymbolToolSchemaOverlay.ExpectedToolCount, contextBound.Length);
        Assert.All(contextBound, tool =>
        {
            Assert.True(HasProperty(tool.InputSchema, "symbolContextId"));
            var description = tool.InputSchema
                .GetProperty("properties")
                .GetProperty("symbolContextId")
                .GetProperty("description")
                .GetString();
            Assert.Equal(SymbolToolSchemaOverlay.PropertyDescription, description);
            Assert.Contains("symbol_resolution_unavailable", description, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(tools, tool => tool.Name is
            "set_symbol_path" or "add_symbol_server" or "diagnose_symbols");
        var prepare = Assert.Single(tools, tool => tool.Name == "prepare_symbols");
        Assert.False(prepare.Annotations!.ReadOnlyHint);
        Assert.True(prepare.Annotations.IdempotentHint);
        Assert.True(prepare.Annotations.OpenWorldHint);
        Assert.False(prepare.Annotations.DestructiveHint);
        Assert.DoesNotContain("symbolContextId", prepare.InputSchema.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveOutputType_UnwrapsPrepareSymbolsTask()
    {
        var method = typeof(SymbolLifecycleTools).GetMethod(
            nameof(SymbolLifecycleTools.PrepareSymbols))!;

        Assert.Equal(
            typeof(PrepareSymbolsResponse),
            ActiveToolCatalog.EffectiveOutputType(method));
    }

    [Fact]
    public void StackReader_IgnoresProcessSymbolPathAndUsesClosedWorldEmptyPath()
    {
        var original = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable(
                "_NT_SYMBOL_PATH",
                "SRV*C:\\unapproved*https://example.invalid/symbols");
            using var reader = StackSourceTopN.OpenSymbolReader(TextWriter.Null);

            Assert.Equal(string.Empty, reader.SymbolPath);
            Assert.Equal(
                "SRV*C:\\unapproved*https://example.invalid/symbols",
                Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", original);
        }
    }

    [Fact]
    public void SecureDefaultSymbolPolicy_IsDisabledWithoutExplicitDisjointRootsAndStore()
    {
        var defaults = SymbolRuntimeOptions.Defaults(static _ => null).ValidatePure();
        Assert.Empty(defaults.ApprovedLocalRoots);
        Assert.Null(defaults.StoreRoot);
        var policy = defaults.CreatePolicySnapshot();
        Assert.Equal(SymbolNetworkPolicy.Denied, policy.NetworkPolicy);
        Assert.Empty(policy.ApprovedLocalRoots);

        var source = Path.Combine(Path.GetTempPath(), "symbol-source");
        var nestedStore = Path.Combine(source, "store");
        var overlapping = defaults with
        {
            ApprovedLocalRoots = [source],
            StoreRoot = nestedStore,
        };
        Assert.Throws<ArgumentException>(() => overlapping.ValidatePure());
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(
            ["--symbol-path", "SRV*C:\\symbols*https://example.invalid"]));
    }

    [Fact]
    public async Task ResolveSymbolsTrueWithoutContext_FailsClosedBeforeToolBinding()
    {
        await using var registry = new SymbolContextRegistry(
            SymbolContextRegistryOptions.Default);
        var principal = new StdioSessionPrincipal();
        var tool = new Tool
        {
            Name = "stack_probe",
            InputSchema = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"object","properties":{"resolveSymbols":{"type":"boolean"},"symbolContextId":{"anyOf":[{"type":"string"},{"type":"null"}]}}}"""),
        };
        var filters = new SymbolQueryExecutionFilters(
            [tool],
            registry,
            principal,
            static () => "trace-cache-generation-v1:1");
        var delegated = false;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            delegated = true;
            return Task.CompletedTask;
        });
        var request = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(
                new CallToolRequestParams
                {
                    Name = tool.Name,
                    Arguments = new Dictionary<string, JsonElement>
                    {
                        ["resolveSymbols"] = JsonSerializer.SerializeToElement(true),
                    },
                },
                McpJsonUtilities.DefaultOptions),
        };

        var exception = await Assert.ThrowsAsync<SymbolToolContractException>(() =>
            incoming(
                new MessageContext(Mock.Of<McpServer>(), request),
                CancellationToken.None));

        Assert.Equal("invalid_argument", exception.Code);
        Assert.False(delegated);
    }

    [Fact]
    public async Task ContextWithoutSymbolResolution_IsRejectedInsteadOfAdvertisedAsUsed()
    {
        await using var registry = new SymbolContextRegistry(
            SymbolContextRegistryOptions.Default);
        var principal = new StdioSessionPrincipal();
        var tool = new Tool
        {
            Name = "stack_probe",
            InputSchema = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"object","properties":{"resolveSymbols":{"type":"boolean"},"symbolContextId":{"anyOf":[{"type":"string"},{"type":"null"}]}}}"""),
        };
        var filters = new SymbolQueryExecutionFilters(
            [tool],
            registry,
            principal,
            static () => "trace-cache-generation-v1:1");
        var delegated = false;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            delegated = true;
            return Task.CompletedTask;
        });
        var request = new JsonRpcRequest
        {
            Id = new RequestId(2),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(
                new CallToolRequestParams
                {
                    Name = tool.Name,
                    Arguments = new Dictionary<string, JsonElement>
                    {
                        ["resolveSymbols"] = JsonSerializer.SerializeToElement(false),
                        ["symbolContextId"] = JsonSerializer.SerializeToElement(
                            "sym_0123456789abcdef0123456789abcdef"),
                    },
                },
                McpJsonUtilities.DefaultOptions),
        };

        var exception = await Assert.ThrowsAsync<SymbolToolContractException>(() =>
            incoming(
                new MessageContext(Mock.Of<McpServer>(), request),
                CancellationToken.None));

        Assert.Equal("invalid_argument", exception.Code);
        Assert.False(delegated);
    }

    [Fact]
    public void ContextBoundResolverGap_UsesStablePublicErrorInsteadOfGenericAnalysisFailure()
    {
        var exception = new SymbolToolContractException(
            "symbol_resolution_unavailable",
            "context_bound_frame_resolution_unavailable",
            "Context-bound frame-name resolution is unavailable.");

        var projected = ContractMcpServerTool.MapException(exception);

        Assert.Equal("symbol_resolution_unavailable", projected.Code);
        Assert.False(projected.Retryable);
        Assert.Contains("resolveSymbols=false", projected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareSymbols_HoldsTraceLeaseThroughContextPublication()
    {
        using var traceRuntime = TraceLifecycleProductionTests.TestRuntime.Create();
        var traces = new TraceToolRuntime(
            traceRuntime.Lifecycle,
            traceRuntime.Registry,
            traceRuntime.SessionPrincipal);
        // This lease-ordering test deliberately loads the checked-in fixture through
        // the registry backend. Secure source/artifact ACL behavior is covered by the
        // trace lifecycle suite and is orthogonal to the prepare/unload race.
        var loaded = traceRuntime.Registry.Load(
            traceRuntime.Principal,
            "fixtures/small_cpu.etl");
        await using var contexts = new SymbolContextRegistry(SymbolContextRegistryOptions.Default);
        var policy = new ApprovedSymbolPolicySnapshot(
            "local-disabled",
            "revision-1",
            [],
            SymbolNetworkPolicy.Denied,
            [],
            "none");
        var resolver = new BlockingPreparationResolver();
        var preparation = new SymbolPreparationService(
            contexts,
            new ApprovedSymbolPolicyCatalog([policy]),
            resolver);
        var symbols = new SymbolToolRuntime(
            traces,
            traceRuntime.SessionPrincipal,
            preparation,
            policy.PolicyReference);

        var prepare = symbols.PrepareAsync(
            loaded.TraceId,
            symbolPolicyReference: null,
            CancellationToken.None).AsTask();
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var unload = traces.Unload(loaded.TraceId);
        Assert.Equal(TraceHandleUnloadStatus.Unloaded, unload.Status);
        Assert.Equal(1, unload.ActiveLeases);
        Assert.False(unload.DrainTask.IsCompleted);

        resolver.Release.TrySetResult();
        var prepared = await prepare.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(SymbolContextRegistry.HasCanonicalShape(
            prepared.Descriptor.SymbolContextId));
        await unload.DrainTask.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static bool HasProperty(JsonElement schema, string propertyName) =>
        schema.TryGetProperty("properties", out var properties) &&
        properties.TryGetProperty(propertyName, out _);

    private sealed class BlockingPreparationResolver : ISymbolPreparationResolver
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ResolverVersion => "blocking-test-v1";

        public async ValueTask<ResolvedSymbolArtifacts> PrepareAsync(
            SymbolPreparationRequest request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ResolvedSymbolArtifacts([]);
        }
    }
}
