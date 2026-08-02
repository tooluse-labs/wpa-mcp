using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;
using Xunit;

namespace WpaMcp.Tests;

public class TelemetryTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void TelemetryOptions_DefaultsToDisabled()
    {
        var options = ToolTelemetryOptions.FromEnvironment(_ => null);

        Assert.False(options.Enabled);
    }

    [Fact]
    public void TelemetryOptions_ReadsWpaEnvironmentNames()
    {
        var values = new Dictionary<string, string>
        {
            ["WPAMCP_TELEMETRY"] = "1",
            ["WPAMCP_TELEMETRY_DEST"] = "stderr",
            ["WPAMCP_TELEMETRY_FILE"] = "telemetry.jsonl",
        };

        var options = ToolTelemetryOptions.FromEnvironment(values.GetValueOrDefault);

        Assert.True(options.Enabled);
        Assert.Equal(ToolTelemetryDestination.Stderr, options.Destination);
        Assert.Equal("telemetry.jsonl", options.FilePath);
    }

    [Fact]
    public void DefaultLogPath_UsesWpaMcpAppDataDirectory()
    {
        var path = ToolTelemetryOptions.DefaultLogPath();
        var expectedDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WpaMcp",
            "Logs");

        Assert.Equal(expectedDirectory, Path.GetDirectoryName(path));
    }

    [Fact]
    public void DisabledTelemetry_WritesNothing()
    {
        using var writer = new StringWriter();
        using var telemetry = new ToolTelemetry(ToolTelemetryOptions.Disabled, new byte[32], writer);

        telemetry.RecordToolCall(
            "inspect_trace",
            new { path = @"C:\private\trace.etl" },
            TimeSpan.FromMilliseconds(12),
            responseBytes: 100,
            error: false,
            TraceCacheCallSnapshot.Empty);

        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public void ToolCallTelemetry_HashesArgumentsAndOmitsRawValues()
    {
        using var writer = new StringWriter();
        using var telemetry = new ToolTelemetry(
            new ToolTelemetryOptions(true, ToolTelemetryDestination.Stderr, null),
            Enumerable.Repeat((byte)1, 32).ToArray(),
            writer);

        telemetry.RecordToolCall(
            "inspect_trace",
            new { path = @"C:\private\trace.etl" },
            TimeSpan.FromMilliseconds(12.3456),
            responseBytes: 100,
            error: false,
            new TraceCacheCallSnapshot(1, 0));

        var line = writer.ToString();
        Assert.DoesNotContain("private", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trace.etl", line, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal("tool_call", root.GetProperty("event_type").GetString());
        Assert.Equal("inspect_trace", root.GetProperty("tool_name").GetString());
        Assert.Equal(64, root.GetProperty("argument_hash").GetString()!.Length);
        Assert.True(root.GetProperty("cache_hit").GetBoolean());
        Assert.Equal(1, root.GetProperty("cache_hits").GetInt32());
        Assert.Equal(0, root.GetProperty("cache_misses").GetInt32());
    }

    [Fact]
    public void ArgumentHash_UsesSessionSalt()
    {
        using var first = new ToolTelemetry(
            new ToolTelemetryOptions(true, ToolTelemetryDestination.Stderr, null),
            Enumerable.Repeat((byte)1, 32).ToArray(),
            new StringWriter());
        using var second = new ToolTelemetry(
            new ToolTelemetryOptions(true, ToolTelemetryDestination.Stderr, null),
            Enumerable.Repeat((byte)2, 32).ToArray(),
            new StringWriter());

        var arguments = new { path = @"C:\private\trace.etl" };

        Assert.NotEqual(first.HashArguments(arguments), second.HashArguments(arguments));
    }

    [Fact]
    public void TraceCacheCallContext_RecordsHitsAndMissesWithinScope()
    {
        var cache = new TraceCache(capacity: 2);

        using var scope = TraceCacheCallContext.Begin();
        cache.Get(FixturePath);
        cache.Get(FixturePath);

        var snapshot = TraceCacheCallContext.Snapshot;
        Assert.Equal(1, snapshot.Misses);
        Assert.Equal(1, snapshot.Hits);
        Assert.False(snapshot.CacheHit);
    }

    [Fact]
    public void ToolListPayload_ReportsAggregateCostSeparatelyFromFittedPageLimit()
    {
        var stats = ToolListPayload.MeasureCurrentAssembly();
        var tools = ToolListPayload.MeasureCurrentTools();
        var preflight = ToolsListPageFitter.Preflight(
            tools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes);
        var largest = string.Join(
            ", ",
            ToolListPayload.MeasureCurrentToolPayloads()
                .Take(8)
                .Select(tool =>
                    $"{tool.ToolName}={tool.PayloadBytes}" +
                    (tool.HasOutputSchema
                        ? $"(schema={tool.OutputSchemaBytes})"
                        : string.Empty)));

        Assert.True(stats.ToolCount >= 50);
        Assert.True(stats.PayloadBytes > 0);
        Assert.True(
            stats.PayloadBytes > preflight.MaxResponseFrameBytes,
            $"Aggregate tools/list cost was unexpectedly conflated with one fitted page: {stats.PayloadBytes} bytes; largest tools: {largest}.");
        Assert.Equal(stats.PayloadBytes, preflight.AggregateCatalogResultBytes);
        Assert.True(preflight.MinimumViableFrameBytes <= preflight.MaxResponseFrameBytes);
        Assert.True(stats.ExceedsLimit);
    }

    [Fact]
    public async Task IncomingFilter_DoesNotRecordSuccessAsErrorWithoutOutgoingResponse()
    {
        using var writer = new StringWriter();
        using var telemetry = EnabledTelemetry(writer);
        var filters = new McpTelemetryFilters(telemetry);
        var incoming = filters.CreateIncomingFilter()(static async (_, _) => await Task.CompletedTask);

        await incoming(CreateMessageContext(ToolCallRequest(1)), CancellationToken.None);

        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public async Task TelemetryFilters_RecordSuccessFromOutgoingResponse()
    {
        using var writer = new StringWriter();
        using var telemetry = EnabledTelemetry(writer);
        var filters = new McpTelemetryFilters(telemetry);
        var outgoing = filters.CreateOutgoingFilter()(static async (_, _) => await Task.CompletedTask);
        var incoming = filters.CreateIncomingFilter()(async (_, cancellationToken) =>
        {
            await outgoing(CreateMessageContext(ToolCallResponse(1, isError: false)), cancellationToken);
        });

        await incoming(CreateMessageContext(ToolCallRequest(1)), CancellationToken.None);

        var root = ParseSingleTelemetryLine(writer);
        Assert.Equal("tool_call", root.GetProperty("event_type").GetString());
        Assert.Equal("inspect_trace", root.GetProperty("tool_name").GetString());
        Assert.False(root.GetProperty("error").GetBoolean());
        Assert.True(root.GetProperty("response_bytes").GetInt32() > 0);
    }

    [Fact]
    public async Task IncomingFilter_RecordsErrorWhenHandlerThrows()
    {
        using var writer = new StringWriter();
        using var telemetry = EnabledTelemetry(writer);
        var filters = new McpTelemetryFilters(telemetry);
        var incoming = filters.CreateIncomingFilter()(static async (_, _) =>
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("boom");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await incoming(CreateMessageContext(ToolCallRequest(1)), CancellationToken.None));

        var root = ParseSingleTelemetryLine(writer);
        Assert.Equal("tool_call", root.GetProperty("event_type").GetString());
        Assert.Equal("inspect_trace", root.GetProperty("tool_name").GetString());
        Assert.True(root.GetProperty("error").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("response_bytes").ValueKind);
    }

    [Fact]
    public void ToolsListPayloadTelemetry_RecordsStartupMetricShape()
    {
        using var writer = new StringWriter();
        using var telemetry = new ToolTelemetry(
            new ToolTelemetryOptions(true, ToolTelemetryDestination.Stderr, null),
            Enumerable.Repeat((byte)1, 32).ToArray(),
            writer);

        telemetry.RecordToolsListPayload(new ToolListPayloadStats(55, 12345, 200000));

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal("tools_list_payload", root.GetProperty("event_type").GetString());
        Assert.Equal(55, root.GetProperty("tool_count").GetInt32());
        Assert.Equal(12345, root.GetProperty("payload_bytes").GetInt32());
        Assert.Equal(200000, root.GetProperty("max_payload_bytes").GetInt32());
    }

    [Fact]
    public void ToolsListPageTelemetry_SeparatesPageFrameFromAggregateCatalogBytes()
    {
        using var writer = new StringWriter();
        using var telemetry = new ToolTelemetry(
            new ToolTelemetryOptions(true, ToolTelemetryDestination.Stderr, null),
            Enumerable.Repeat((byte)1, 32).ToArray(),
            writer);

        telemetry.RecordToolsListPage(
            frameBytes: 22_141,
            returnedTools: 7,
            hasMore: true,
            aggregateCatalogResultBytes: 182_198,
            maxResponseFrameBytes: 22_268);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal("tools_list_page", root.GetProperty("event_type").GetString());
        Assert.Equal(22_141, root.GetProperty("frame_bytes").GetInt32());
        Assert.Equal(7, root.GetProperty("returned_tools").GetInt32());
        Assert.True(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(182_198, root.GetProperty("aggregate_catalog_result_bytes").GetInt32());
        Assert.Equal(22_268, root.GetProperty("max_response_frame_bytes").GetInt32());
    }

    [Fact]
    public void FileTelemetry_AllowsFilenameOnlyPath()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"wpamcp-telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            Directory.SetCurrentDirectory(tempDirectory);

            using var telemetry = new ToolTelemetry(
                new ToolTelemetryOptions(true, ToolTelemetryDestination.File, "telemetry.jsonl"),
                Enumerable.Repeat((byte)1, 32).ToArray());

            telemetry.RecordToolsListPayload(new ToolListPayloadStats(55, 12345, 200000));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }

        var logPath = Path.Combine(tempDirectory, "telemetry.jsonl");
        Assert.True(File.Exists(logPath));

        Directory.Delete(tempDirectory, recursive: true);
    }

    private static ToolTelemetry EnabledTelemetry(TextWriter writer)
        => new(
            new ToolTelemetryOptions(true, ToolTelemetryDestination.Stderr, null),
            Enumerable.Repeat((byte)1, 32).ToArray(),
            writer);

    private static MessageContext CreateMessageContext(JsonRpcMessage message)
        => new(Mock.Of<McpServer>(), message);

    private static JsonRpcRequest ToolCallRequest(long id)
        => new()
        {
            Id = new RequestId(id),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(new CallToolRequestParams
            {
                Name = "inspect_trace",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(FixturePath),
                },
            }, McpJsonUtilities.DefaultOptions),
        };

    private static JsonRpcResponse ToolCallResponse(long id, bool isError)
        => new()
        {
            Id = new RequestId(id),
            Result = JsonSerializer.SerializeToNode(
                new CallToolResult { IsError = isError },
                McpJsonUtilities.DefaultOptions),
        };

    private static JsonElement ParseSingleTelemetryLine(StringWriter writer)
    {
        var line = writer.ToString();
        Assert.False(string.IsNullOrWhiteSpace(line));
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }
}
