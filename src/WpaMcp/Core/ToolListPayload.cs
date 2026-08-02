using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Core;

internal static class ToolListPayload
{
    public const int DefaultMaxPayloadBytes = 200_000;
    // Immutable Phase 1 legacy-transition observation. Contract 2.0 intentionally
    // exceeds this aggregate threshold because every tool exposes its closed output
    // schema; tools/list page fitting is the transport bound and does not reduce the
    // aggregate prompt cost reported by this measurement.
    public const int BaselineGuardPayloadBytes = 185_000;

    public static ToolListPayloadStats MeasureCurrentAssembly(
        int maxPayloadBytes = DefaultMaxPayloadBytes)
        => Measure(ActiveToolCatalog.LoadAndValidate(), maxPayloadBytes);

    internal static ToolListPayloadStats Measure(
        ActiveToolCatalog catalog,
        int maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        var tools = CurrentTools(catalog);
        return Measure(tools, maxPayloadBytes);
    }

    internal static ToolListPayloadStats Measure(
        IReadOnlyList<Tool> tools,
        int maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        var result = new ListToolsResult { Tools = tools.ToArray() };
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).Length;
        return new ToolListPayloadStats(tools.Count, payloadBytes, maxPayloadBytes);
    }

    internal static IReadOnlyList<string> MeasureCurrentToolNames()
        => CurrentTools(ActiveToolCatalog.LoadAndValidate())
            .Select(tool => tool.Name)
            .ToList();

    internal static IReadOnlyList<Tool> MeasureCurrentTools()
        => CurrentTools(ActiveToolCatalog.LoadAndValidate());

    internal static IReadOnlyList<Tool> MeasureCurrentTools(ActiveToolCatalog catalog)
        => CurrentTools(catalog);

    internal static IReadOnlyList<ToolPayloadStats> MeasureCurrentToolPayloads()
        => CurrentTools(ActiveToolCatalog.LoadAndValidate())
            .Select(tool => new ToolPayloadStats(
                tool.Name,
                JsonSerializer.SerializeToUtf8Bytes(
                    tool, McpJsonUtilities.DefaultOptions).Length,
                tool.OutputSchema is null
                    ? 0
                    : JsonSerializer.SerializeToUtf8Bytes(
                        tool.OutputSchema, McpJsonUtilities.DefaultOptions).Length))
            .OrderByDescending(stats => stats.PayloadBytes)
            .ThenBy(stats => stats.ToolName, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<Tool> CurrentTools(ActiveToolCatalog catalog)
        => catalog.CreateProtocolTools(new DeferredCatalogServiceProvider());
}

internal sealed record ToolListPayloadStats(int ToolCount, int PayloadBytes, int MaxPayloadBytes)
{
    public bool ExceedsLimit => PayloadBytes > MaxPayloadBytes;
}

internal sealed record ToolPayloadStats(
    string ToolName,
    int PayloadBytes,
    int OutputSchemaBytes)
{
    public bool HasOutputSchema => OutputSchemaBytes > 0;
}

internal sealed class ToolListPayloadHostedService(
    ToolTelemetry telemetry,
    ILogger<ToolListPayloadHostedService> logger,
    ToolsListPaginationFilters pagination,
    ToolExecutionBudgetOptions budgets) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var stats = ToolListPayload.Measure(
            pagination.ActiveTools,
            budgets.ResponseWarningBytes);
        telemetry.RecordToolsListPayload(stats);

        if (stats.ExceedsLimit)
        {
            logger.LogWarning(
                "MCP tools/list payload is {PayloadBytes} bytes for {ToolCount} tools; limit is {MaxPayloadBytes} bytes.",
                stats.PayloadBytes,
                stats.ToolCount,
                stats.MaxPayloadBytes);
        }
        else
        {
            logger.LogInformation(
                "MCP tools/list payload is {PayloadBytes} bytes for {ToolCount} tools; limit is {MaxPayloadBytes} bytes.",
                stats.PayloadBytes,
                stats.ToolCount,
                stats.MaxPayloadBytes);
        }

        logger.LogInformation(
            "MCP tools/list paging preflight: cap={MaxResponseFrameBytes} bytes, " +
            "minimum={MinimumViableFrameBytes} bytes, largestTool={LargestSingleToolName} " +
            "({LargestSingleToolFrameBytes} bytes), aggregateResult={AggregateCatalogResultBytes} bytes.",
            pagination.Preflight.MaxResponseFrameBytes,
            pagination.Preflight.MinimumViableFrameBytes,
            pagination.Preflight.LargestSingleToolName,
            pagination.Preflight.LargestSingleToolFrameBytes,
            pagination.Preflight.AggregateCatalogResultBytes);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
