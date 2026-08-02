using System.Globalization;
using WpaMcp.Output;

namespace WpaMcp.Core;

/// <summary>
/// Internal semantic result. It deliberately has no MCP/JSON-RPC dependencies; a later
/// integration layer supplies the catalog-owned ToolReference and serializes one envelope.
/// </summary>
internal sealed class ToolOutcome<TData> where TData : class
{
    private ToolOutcome(
        ToolCompletionStatus status,
        TData? data,
        ToolError? error,
        IReadOnlyList<ToolSectionFailure>? failedSections,
        IReadOnlyList<ToolSectionPage>? sections,
        IReadOnlyList<string>? warnings,
        ToolTraceReference? traceRef,
        ToolScope scope,
        IReadOnlyList<ToolCapabilityEvidence> capabilityEvidence,
        ToolCompleteness completeness,
        ToolEvidenceBoundary evidenceBoundary,
        ToolNoData? noData,
        ToolPrecision precision)
    {
        Status = status;
        Data = data;
        Error = error;
        FailedSections = failedSections ?? Array.Empty<ToolSectionFailure>();
        Sections = sections ?? Array.Empty<ToolSectionPage>();
        Warnings = warnings ?? Array.Empty<string>();
        TraceRef = traceRef;
        Scope = scope;
        CapabilityEvidence = capabilityEvidence;
        Completeness = completeness;
        EvidenceBoundary = evidenceBoundary;
        NoData = noData;
        Precision = precision;
    }

    internal ToolCompletionStatus Status { get; }
    internal TData? Data { get; }
    internal ToolError? Error { get; }
    internal IReadOnlyList<ToolSectionFailure> FailedSections { get; }
    internal IReadOnlyList<ToolSectionPage> Sections { get; }
    internal IReadOnlyList<string> Warnings { get; }
    internal ToolTraceReference? TraceRef { get; }
    internal ToolScope Scope { get; }
    internal IReadOnlyList<ToolCapabilityEvidence> CapabilityEvidence { get; }
    internal ToolCompleteness Completeness { get; }
    internal ToolEvidenceBoundary EvidenceBoundary { get; }
    internal ToolNoData? NoData { get; }
    internal ToolPrecision Precision { get; }

    internal static ToolOutcome<TData> Succeeded(
        TData data,
        ToolTraceReference? traceRef,
        ToolScope scope,
        IReadOnlyList<ToolCapabilityEvidence> capabilityEvidence,
        ToolCompleteness completeness,
        ToolEvidenceBoundary evidenceBoundary,
        ToolPrecision precision,
        IReadOnlyList<ToolSectionPage>? sections = null,
        IReadOnlyList<string>? warnings = null,
        ToolNoData? noData = null) =>
        new(
            ToolCompletionStatus.Succeeded,
            data,
            error: null,
            failedSections: null,
            sections,
            warnings,
            traceRef,
            scope,
            capabilityEvidence,
            completeness,
            evidenceBoundary,
            noData,
            precision);

    internal static ToolOutcome<TData> Partial(
        TData data,
        IReadOnlyList<ToolSectionFailure> failedSections,
        ToolTraceReference? traceRef,
        ToolScope scope,
        IReadOnlyList<ToolCapabilityEvidence> capabilityEvidence,
        ToolCompleteness completeness,
        ToolEvidenceBoundary evidenceBoundary,
        ToolPrecision precision,
        IReadOnlyList<ToolSectionPage>? sections = null,
        IReadOnlyList<string>? warnings = null) =>
        new(
            ToolCompletionStatus.Partial,
            data,
            error: null,
            failedSections,
            sections,
            warnings,
            traceRef,
            scope,
            capabilityEvidence,
            completeness,
            evidenceBoundary,
            noData: null,
            precision);

    internal static ToolOutcome<TData> Failed(
        ToolError error,
        ToolTraceReference? traceRef,
        ToolScope scope,
        IReadOnlyList<ToolCapabilityEvidence> capabilityEvidence,
        ToolCompleteness completeness,
        ToolEvidenceBoundary evidenceBoundary,
        ToolPrecision precision,
        IReadOnlyList<ToolSectionFailure>? failedSections = null,
        IReadOnlyList<ToolSectionPage>? sections = null,
        IReadOnlyList<string>? warnings = null) =>
        new(
            ToolCompletionStatus.Failed,
            data: null,
            error,
            failedSections,
            sections,
            warnings,
            traceRef,
            scope,
            capabilityEvidence,
            completeness,
            evidenceBoundary,
            noData: null,
            precision);

    internal ToolEnvelope<TData> ToEnvelope(ToolReference toolRef) =>
        new(
            ToolContractVersions.V2,
            Status,
            Data,
            Error,
            FailedSections,
            Sections,
            Warnings,
            Sections.Any(section => section.HasMore),
            toolRef,
            TraceRef,
            Scope,
            CapabilityEvidence,
            Completeness,
            EvidenceBoundary,
            NoData,
            Precision);
}

/// <summary>
/// Startup-owned typed adapter registry. Registrations are exact and duplicate tool names
/// fail closed. This core intentionally does not replace the production tool wire surface.
/// </summary>
internal sealed class ToolOutcomeAdapterRegistry
{
    private readonly Dictionary<string, IAdapter> _adapters = new(StringComparer.Ordinal);

    internal void Register<TSource, TData>(
        string toolName,
        Func<TSource, ToolOutcome<TData>> projection)
        where TSource : class
        where TData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(projection);

        if (!_adapters.TryAdd(toolName, new Adapter<TSource, TData>(projection)))
            throw new InvalidOperationException($"An outcome adapter for '{toolName}' is already registered.");
    }

    internal ToolOutcome<TData> Adapt<TSource, TData>(string toolName, TSource source)
        where TSource : class
        where TData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(source);

        if (!_adapters.TryGetValue(toolName, out var adapter))
            throw new KeyNotFoundException($"No outcome adapter is registered for '{toolName}'.");
        if (adapter is not Adapter<TSource, TData> typed)
        {
            throw new InvalidOperationException(
                $"Outcome adapter '{toolName}' is registered for {adapter.SourceType.FullName} -> " +
                $"{adapter.DataType.FullName}, not {typeof(TSource).FullName} -> {typeof(TData).FullName}.");
        }

        return typed.Project(source);
    }

    internal bool Contains(string toolName) => _adapters.ContainsKey(toolName);

    private interface IAdapter
    {
        Type SourceType { get; }
        Type DataType { get; }
    }

    private sealed class Adapter<TSource, TData>(Func<TSource, ToolOutcome<TData>> projection) : IAdapter
        where TSource : class
        where TData : class
    {
        public Type SourceType => typeof(TSource);
        public Type DataType => typeof(TData);

        internal ToolOutcome<TData> Project(TSource source) =>
            projection(source) ?? throw new InvalidOperationException("An outcome adapter returned null.");
    }
}

internal static class PublicIdentifierFormatter
{
    internal const ulong JavaScriptMaxSafeInteger = 9_007_199_254_740_991;

    internal static string UnsignedDecimal(ulong value) =>
        value.ToString(CultureInfo.InvariantCulture);

    internal static string Pointer(ulong value) =>
        $"0x{value:x16}";

    internal static long? DeprecatedSafeNumericProjection(ulong value) =>
        value <= JavaScriptMaxSafeInteger ? checked((long)value) : null;
}
