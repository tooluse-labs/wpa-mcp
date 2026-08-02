using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class QueryResultCursorRegistryTests
{
    private static readonly QueryResultCursorBinding Binding = new(
        "principal-a",
        "trc_0123456789abcdef0123456789abcdef",
        "tgen_0123456789abcdef0123456789abcdef",
        "catalog-v1",
        "2.0",
        SymbolContextId: null,
        "off",
        new string('a', 64),
        QueryResultCursorCoordinator.InspectOrdering);

    [Fact]
    public void Registry_RejectsTamperExpiryAndEveryBindingMismatch()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var registry = new QueryResultCursorRegistry(
            () => now,
            idleTtl: TimeSpan.FromMinutes(2),
            absoluteTtl: TimeSpan.FromMinutes(15));
        var position = new QueryResultCursorPosition("capabilities", 3, "cap.3");
        var token = registry.GetOrIssueContinuation(Binding, null, position);

        Assert.True(QueryResultCursorRegistry.HasCanonicalShape(token));
        Assert.Equal(position, registry.Redeem(token, Binding));
        AssertInvalid(() => registry.Redeem(token[..^1] + "g", Binding));
        foreach (var mismatch in new[]
                 {
                     Binding with { Principal = "principal-b" },
                     Binding with { TraceId = "trc_fedcba9876543210fedcba9876543210" },
                     Binding with { TraceGenerationId = "tgen_fedcba9876543210fedcba9876543210" },
                     Binding with { CatalogOrToolVersion = "catalog-v2" },
                     Binding with { ContractVersion = "2.1" },
                     Binding with { SymbolContextId = "sym_0123456789abcdef0123456789abcdef" },
                     Binding with { PrivacyProfile = "strict" },
                     Binding with { QueryHash = new string('b', 64) },
                     Binding with { Ordering = "other" },
                 })
        {
            AssertInvalid(() => registry.Redeem(token, mismatch));
        }

        now = now.AddMinutes(3);
        AssertInvalid(() => registry.Redeem(token, Binding));

        now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var absolute = new QueryResultCursorRegistry(
            () => now,
            idleTtl: TimeSpan.FromMinutes(2),
            absoluteTtl: TimeSpan.FromMinutes(3));
        var absoluteToken = absolute.GetOrIssueContinuation(Binding, null, position);
        now = now.AddMinutes(2);
        Assert.Equal(position, absolute.Redeem(absoluteToken, Binding));
        now = now.AddMinutes(2);
        AssertInvalid(() => absolute.Redeem(absoluteToken, Binding));
    }

    [Fact]
    public void Registry_CapacityAndEntropyFailuresHaveStableMappings()
    {
        var capacity = new QueryResultCursorRegistry(maxActive: 1);
        capacity.GetOrIssueContinuation(
            Binding,
            null,
            new QueryResultCursorPosition("capabilities", 1, null));
        var capacityError = Assert.Throws<QueryResultCursorException>(() =>
            capacity.GetOrIssueContinuation(
                Binding with { QueryHash = new string('c', 64) },
                null,
                new QueryResultCursorPosition("capabilities", 1, null)));
        Assert.Equal(QueryResultCursorFailureKind.RegistryCapacity, capacityError.Kind);
        Assert.Equal("budget_exceeded", ContractMcpServerTool.MapException(capacityError).Code);

        var entropy = new QueryResultCursorRegistry(
            maxActive: 2,
            entropy: static () => new byte[16]);
        entropy.GetOrIssueContinuation(
            Binding,
            null,
            new QueryResultCursorPosition("capabilities", 1, null));
        var entropyError = Assert.Throws<QueryResultCursorException>(() =>
            entropy.GetOrIssueContinuation(
                Binding with { QueryHash = new string('d', 64) },
                null,
                new QueryResultCursorPosition("capabilities", 1, null)));
        Assert.Equal(QueryResultCursorFailureKind.EntropyFailure, entropyError.Kind);
        Assert.Equal("analysis_failed", ContractMcpServerTool.MapException(entropyError).Code);
    }

    [Fact]
    public void Coordinator_AdvancesCapabilitiesThenWorkflowsExactlyOnce()
    {
        var coordinator = new QueryResultCursorCoordinator(
            "principal-a",
            "off",
            new QueryResultCursorRegistry());
        const string traceId = "trc_0123456789abcdef0123456789abcdef";
        const string generation = "tgen_0123456789abcdef0123456789abcdef";

        Assert.Equal(
            new QueryResultCursorPosition("capabilities", 0, null),
            coordinator.ResolveInspectTrace(traceId, generation, "catalog-v1", null, null, null));
        var capabilityCursor = Assert.IsType<string>(coordinator.FinalizeInspectTrace(
            traceId, generation, "catalog-v1", null, null, null,
            "capabilities", 2, 0, 5, 2, "cap.2"));
        Assert.Equal(
            new QueryResultCursorPosition("capabilities", 2, "cap.2"),
            coordinator.ResolveInspectTrace(
                traceId, generation, "catalog-v1", null, null, capabilityCursor));
        var workflowCursor = Assert.IsType<string>(coordinator.FinalizeInspectTrace(
            traceId, generation, "catalog-v1", null, null, capabilityCursor,
            "capabilities", 3, 0, 5, 2, "cap.5"));
        Assert.Equal(
            new QueryResultCursorPosition("workflows", 0, null),
            coordinator.ResolveInspectTrace(
                traceId, generation, "catalog-v1", null, null, workflowCursor));
        Assert.Null(coordinator.FinalizeInspectTrace(
            traceId, generation, "catalog-v1", null, null, workflowCursor,
            "workflows", 0, 2, 5, 2, "wf.2"));

        var wrongFilter = Assert.Throws<QueryResultCursorException>(() =>
            coordinator.ResolveInspectTrace(
                traceId, generation, "catalog-v1", "cpu", null, capabilityCursor));
        Assert.Equal(QueryResultCursorFailureKind.Invalid, wrongFilter.Kind);
        Assert.Equal("invalid_cursor", ContractMcpServerTool.MapException(wrongFilter).Code);
    }

    private static void AssertInvalid(Action action)
    {
        var exception = Assert.Throws<QueryResultCursorException>(action);
        Assert.Equal(QueryResultCursorFailureKind.Invalid, exception.Kind);
    }
}
