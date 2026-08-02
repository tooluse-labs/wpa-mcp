using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class ToolOutcomeAdapterRegistryTests
{
    [Fact]
    public void Register_RejectsDuplicateOrdinalToolName()
    {
        var registry = new ToolOutcomeAdapterRegistry();
        registry.Register<Source, Projected>("tool_a", source => Success(source.Value));

        var error = Assert.Throws<InvalidOperationException>(() =>
            registry.Register<Source, Projected>("tool_a", source => Success(source.Value)));

        Assert.Contains("already registered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapt_ProjectsTypedSourceToTypedOutcomeWithoutWireSwitch()
    {
        var registry = new ToolOutcomeAdapterRegistry();
        registry.Register<Source, Projected>("tool_a", source => Success(source.Value));

        var outcome = registry.Adapt<Source, Projected>("tool_a", new Source(42));
        var envelope = outcome.ToEnvelope(new ToolReference("tool_a", new[] { "cap.test" }));

        Assert.Equal(42, envelope.Data!.Value);
        Assert.Equal(ToolCompletionStatus.Succeeded, envelope.Status);
        Assert.False(envelope.IsError);
        Assert.True(registry.Contains("tool_a"));
    }

    [Fact]
    public void Adapt_RejectsMissingOrMismatchedTypedRegistration()
    {
        var registry = new ToolOutcomeAdapterRegistry();
        registry.Register<Source, Projected>("tool_a", source => Success(source.Value));

        Assert.Throws<KeyNotFoundException>(() =>
            registry.Adapt<Source, Projected>("missing", new Source(1)));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Adapt<OtherSource, Projected>("tool_a", new OtherSource(1)));
    }

    private static ToolOutcome<Projected> Success(int value)
    {
        var page = new ToolSectionPage(
            "rows",
            ToolSectionMode.None,
            null,
            1,
            1,
            ToolSectionTotalState.Exact,
            false,
            "value",
            ToolSortDirection.Descending,
            new[] { "value_identity_asc" },
            null,
            null,
            null,
            ToolSectionRole.DomainData,
            new[] { "evidence.test" });
        return ToolOutcome<Projected>.Succeeded(
            new Projected(value),
            traceRef: null,
            new ToolScope(
                ToolScopeStatus.NotApplicable,
                ToolScopeMode.NotApplicable,
                new ToolScopeSelector(null, null, null, null, null, null, null),
                null,
                Array.Empty<ToolScopeIdentity>(),
                Array.Empty<ToolScopeIdentity>(),
                false,
                false),
            new[]
            {
                new ToolCapabilityEvidence(
                    "cap.test",
                    ToolCapabilityStatus.NotApplicable,
                    ToolCapabilityStatus.NotApplicable,
                    null,
                    null,
                    ToolCaptureIntegrityStatus.NotApplicable,
                    new[] { "evidence.test" }),
            },
            new ToolCompleteness(ToolCompletenessStatus.Complete, 1, 1, 0, false),
            new ToolEvidenceBoundary(new[]
            {
                new ToolEvidenceBoundaryItem(
                    "evidence.test",
                    "rows",
                    MeasurementBasis.Direct,
                    Relationship.Descriptive,
                    ConclusionStatus.Observed,
                    Array.Empty<string>(),
                    new ToolEvidenceProvenance(
                        "adapter_source", "typed_projection", "test", null,
                        ToolCaptureIntegrityStatus.NotApplicable)),
            }),
            new ToolPrecision(
                ToolIdentifierPrecision.NotApplicable,
                ToolMetricPrecision.Exact,
                null,
                "checked_int32",
                null),
            new[] { page });
    }

    private sealed record Source(int Value);
    private sealed record OtherSource(int Value);
    private sealed record Projected(int Value);
}
