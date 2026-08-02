using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class SymbolPreparationDeliveryTests
{
    [Fact]
    public async Task NewContext_CommitsOnlyAfterSuccessfulOutgoingDelivery()
    {
        await using var registry = Registry();
        await using var filters = new SymbolPreparationDeliveryFilters();
        var principal = new SymbolPrincipal("session:test");
        var trace = Trace();
        var (publication, disclosure) = await Disclosure(registry, principal, trace);

        await RegisterIncoming(filters, 1, disclosure, CancellationToken.None);
        Assert.Equal(1, filters.PendingCount);
        await filters.CreateOutgoingFilter()(static (_, _) => Task.CompletedTask)(
            Context(Response(1, isError: false)),
            CancellationToken.None);

        Assert.Equal(0, filters.PendingCount);
        Assert.Equal(1, registry.ActiveCount(principal));
        await using var lease = await registry.AcquireAsync(
            principal,
            publication.Descriptor.SymbolContextId,
            trace.GenerationIdentity);
    }

    [Fact]
    public async Task ResponseTooLargeFailure_RollsBackNewContextAndQuota()
    {
        await using var registry = Registry();
        await using var filters = new SymbolPreparationDeliveryFilters();
        var principal = new SymbolPrincipal("session:test");
        var trace = Trace();
        var (publication, disclosure) = await Disclosure(registry, principal, trace);

        await RegisterIncoming(filters, 2, disclosure, CancellationToken.None);
        await filters.CreateOutgoingFilter()(static (_, _) => Task.CompletedTask)(
            Context(Response(2, isError: true, errorCode: "response_too_large")),
            CancellationToken.None);

        Assert.Equal(0, registry.ActiveCount(principal));
        Assert.Equal(0, registry.OwnedCount(principal));
        await AssertUnavailable(registry, principal, trace, publication.Descriptor.SymbolContextId);
    }

    [Fact]
    public async Task OutgoingTransportFailure_RollsBackNewContext()
    {
        await using var registry = Registry();
        await using var filters = new SymbolPreparationDeliveryFilters();
        var principal = new SymbolPrincipal("session:test");
        var trace = Trace();
        var (publication, disclosure) = await Disclosure(registry, principal, trace);

        await RegisterIncoming(filters, 3, disclosure, CancellationToken.None);
        var outgoing = filters.CreateOutgoingFilter()(
            static (_, _) => throw new IOException("synthetic transport failure"));
        await Assert.ThrowsAsync<IOException>(() => outgoing(
            Context(Response(3, isError: false)),
            CancellationToken.None));

        Assert.Equal(0, registry.ActiveCount(principal));
        Assert.Equal(0, registry.OwnedCount(principal));
        await AssertUnavailable(registry, principal, trace, publication.Descriptor.SymbolContextId);
    }

    [Fact]
    public async Task CancellationBeforeOutgoing_RollsBackNewContext()
    {
        await using var registry = Registry();
        await using var filters = new SymbolPreparationDeliveryFilters();
        var principal = new SymbolPrincipal("session:test");
        var trace = Trace();
        var (publication, disclosure) = await Disclosure(registry, principal, trace);
        using var cancellation = new CancellationTokenSource();
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            Assert.True(SymbolPreparationDeliveryContext.TryRegister(disclosure));
            cancellation.Cancel();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => incoming(
            Context(Request(4)),
            cancellation.Token));
        await EventuallyAsync(() =>
            filters.PendingCount == 0 && registry.OwnedCount(principal) == 0);
        await AssertUnavailable(registry, principal, trace, publication.Descriptor.SymbolContextId);
    }

    [Fact]
    public async Task CanonicalReuse_RollbackNeverRetiresPreviouslyCommittedContext()
    {
        await using var registry = Registry();
        await using var filters = new SymbolPreparationDeliveryFilters();
        var principal = new SymbolPrincipal("session:test");
        var trace = Trace();
        var committed = await registry.PublishAsync(principal, Prepared(principal, trace));
        var (publication, disclosure) = await Disclosure(registry, principal, trace);
        Assert.False(publication.Created);
        Assert.Equal(committed.SymbolContextId, publication.Descriptor.SymbolContextId);

        await RegisterIncoming(filters, 5, disclosure, CancellationToken.None);
        await filters.CreateOutgoingFilter()(static (_, _) => Task.CompletedTask)(
            Context(Response(5, isError: true)),
            CancellationToken.None);

        Assert.Equal(1, registry.ActiveCount(principal));
        await using var lease = await registry.AcquireAsync(
            principal,
            committed.SymbolContextId,
            trace.GenerationIdentity);
    }

    [Fact]
    public async Task ConcurrentDisclosures_OneSuccessPreservesContextAfterOtherRollback()
    {
        await using var registry = Registry();
        await using var filters = new SymbolPreparationDeliveryFilters();
        var principal = new SymbolPrincipal("session:test");
        var trace = Trace();
        var publication = await registry.PublishWithDispositionAsync(
            principal,
            Prepared(principal, trace));
        var group = new SymbolContextPublicationGroup(
            registry,
            principal,
            publication,
            initialReservations: 2);

        await RegisterIncoming(
            filters,
            6,
            new SymbolContextDisclosure(group),
            CancellationToken.None);
        await RegisterIncoming(
            filters,
            7,
            new SymbolContextDisclosure(group),
            CancellationToken.None);
        var outgoing = filters.CreateOutgoingFilter()(static (_, _) => Task.CompletedTask);
        await outgoing(Context(Response(6, isError: true)), CancellationToken.None);
        Assert.Equal(1, registry.ActiveCount(principal));
        await outgoing(Context(Response(7, isError: false)), CancellationToken.None);
        Assert.Equal(1, registry.ActiveCount(principal));
    }

    private static async Task<(SymbolContextPublication Publication, SymbolContextDisclosure Disclosure)>
        Disclosure(
            SymbolContextRegistry registry,
            SymbolPrincipal principal,
            ISymbolTraceGenerationReference trace)
    {
        var publication = await registry.PublishWithDispositionAsync(
            principal,
            Prepared(principal, trace));
        var group = new SymbolContextPublicationGroup(
            registry,
            principal,
            publication,
            initialReservations: 1);
        return (publication, new SymbolContextDisclosure(group));
    }

    private static Task RegisterIncoming(
        SymbolPreparationDeliveryFilters filters,
        int id,
        SymbolContextDisclosure disclosure,
        CancellationToken cancellationToken) =>
        filters.CreateIncomingFilter()((_, _) =>
        {
            Assert.True(SymbolPreparationDeliveryContext.TryRegister(disclosure));
            return Task.CompletedTask;
        })(Context(Request(id)), cancellationToken);

    private static JsonRpcRequest Request(int id) => new()
    {
        Id = new RequestId(id),
        Method = RequestMethods.ToolsCall,
        Params = JsonSerializer.SerializeToNode(
            new CallToolRequestParams
            {
                Name = "prepare_symbols",
                Arguments = new Dictionary<string, JsonElement>(),
            },
            McpJsonUtilities.DefaultOptions),
    };

    private static JsonRpcResponse Response(
        int id,
        bool isError,
        string? errorCode = null) => new()
    {
        Id = new RequestId(id),
        Result = new JsonObject
        {
            ["isError"] = isError,
            ["errorCode"] = errorCode,
        },
    };

    private static MessageContext Context(JsonRpcMessage message) =>
        new(Mock.Of<McpServer>(), message);

    private static SymbolContextRegistry Registry() => new(
        SymbolContextRegistryOptions.Default);

    private static OpaqueSymbolTraceGenerationReference Trace() =>
        new("trace-cache-generation-v1:1", []);

    private static PreparedSymbolContext Prepared(
        SymbolPrincipal principal,
        ISymbolTraceGenerationReference trace)
    {
        var policy = new ApprovedSymbolPolicySnapshot(
            "local-disabled",
            "revision-1",
            [],
            SymbolNetworkPolicy.Denied,
            [],
            "none");
        var definition = SymbolContextDefinition.Create(
            principal,
            trace,
            policy,
            "resolver-v1",
            [],
            "off",
            "2.0");
        return new PreparedSymbolContext(
            definition,
            SymbolPreparationEvidence.Create([], []),
            []);
    }

    private static async Task AssertUnavailable(
        SymbolContextRegistry registry,
        SymbolPrincipal principal,
        ISymbolTraceGenerationReference trace,
        string symbolContextId)
    {
        var exception = await Assert.ThrowsAsync<SymbolContextException>(async () =>
            await registry.AcquireAsync(
                principal,
                symbolContextId,
                trace.GenerationIdentity));
        Assert.Equal(SymbolContextFailure.Retired, exception.Failure);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected rollback was not observed.");
            await Task.Delay(10);
        }
    }
}
