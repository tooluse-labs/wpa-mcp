using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class ToolsListPaginationTests
{
    [Fact]
    public void ResponseCap_DefaultIsPreservedAndOnlyValidatedBoundsAreConfigurable()
    {
        Assert.Equal(100_000, ToolsListPaginationOptions.Default.MaxResponseFrameBytes);
        Assert.Equal(100_000, ToolsListPaginationOptions.HardMaxResponseFrameBytes);
        Assert.Equal(
            22_268,
            ToolsListPaginationOptions.FromEnvironment(name =>
                name == ToolsListPaginationOptions.MaxResponseFrameBytesEnvironmentVariable
                    ? "22268"
                    : null).MaxResponseFrameBytes);
        Assert.Equal(99_999, ToolsListPaginationOptions.FromEnvironment(_ => "99999").MaxResponseFrameBytes);
        Assert.Equal(100_000, ToolsListPaginationOptions.FromEnvironment(_ => "100000").MaxResponseFrameBytes);
        Assert.Throws<ToolsListStartupValidationException>(() =>
            ToolsListPaginationOptions.FromEnvironment(_ => "100001"));
        Assert.Throws<ToolsListStartupValidationException>(() =>
            ToolsListPaginationOptions.FromEnvironment(_ => "not-an-integer"));
    }

    [Fact]
    public void Constructor_RejectsEveryNonV2ContractIdentity()
    {
        var tools = ActiveTools(out var catalog);

        foreach (var mode in new[] { "", "legacy", "2", "2.0 " })
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ToolsListPaginationFilters(
                    tools,
                    catalog.CatalogVersion,
                    contractMode: mode));
            Assert.Equal("contractMode", error.ParamName);
        }

        _ = new ToolsListPaginationFilters(
            tools,
            catalog.CatalogVersion,
            contractMode: ToolContractVersions.V2);
    }

    [Fact]
    public void ActiveCatalog_PreflightMeasuresCompleteJsonRpcFramesAndAggregateCatalog()
    {
        var tools = ActiveTools(out var catalog);
        var filters = new ToolsListPaginationFilters(
            tools,
            catalog.CatalogVersion,
            ToolsListPaginationOptions.Default);

        Assert.Equal(catalog.Tools.Count, tools.Count);
        Assert.Equal(
            ["list_capabilities", "inspect_trace", "list_processes"],
            tools.Take(3).Select(tool => tool.Name));
        Assert.InRange(
            filters.Preflight.MinimumViableFrameBytes,
            ToolsListPaginationOptions.MinimumConfiguredFrameBytes,
            filters.Preflight.MaxResponseFrameBytes);
        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(
                new ListToolsResult { Tools = tools.ToArray() },
                McpJsonUtilities.DefaultOptions).Length,
            filters.Preflight.AggregateCatalogResultBytes);
        Assert.Contains(
            tools,
            tool => tool.Name == filters.Preflight.LargestSingleToolName);
    }

    [Fact]
    public void Fitter_AcceptsExactCapAndMovesIndivisibleToolAtCapPlusOne()
    {
        var tools = ActiveTools(out _).Take(2).ToArray();
        var id = new RequestId("cap-boundary");
        var complete = new ListToolsResult { Tools = tools };
        var exactCap = ToolsListPageFitter.MeasureFrame(id, complete);

        var exact = ToolsListPageFitter.Fit(tools, 0, id, exactCap);
        var plusOne = ToolsListPageFitter.Fit(tools, 0, id, exactCap - 1);

        Assert.Equal(2, exact.Result.Tools.Count);
        Assert.Equal(exactCap, exact.FrameBytes);
        Assert.Equal(exactCap, (exactCap - 1) + 1);
        Assert.Single(plusOne.Result.Tools);
        Assert.NotNull(plusOne.Result.NextCursor);
        Assert.True(plusOne.FrameBytes <= exactCap - 1);
    }

    [Fact]
    public void UnicodeInputs_AreEscapedAndFrameStillUsesExactUtf8ByteCount()
    {
        var tool = new Tool
        {
            Name = "utf8_probe",
            Description = string.Concat(Enumerable.Repeat("路径-证据边界-", 100)),
            InputSchema = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"object","properties":{}}"""),
        };
        var id = new RequestId("请求-id");
        var result = new ListToolsResult { Tools = [tool] };
        var response = new JsonRpcResponse
        {
            Id = id,
            Result = JsonSerializer.SerializeToNode(result, McpJsonUtilities.DefaultOptions),
        };
        var json = JsonSerializer.Serialize(response, McpJsonUtilities.DefaultOptions);

        var measured = ToolsListPageFitter.MeasureFrame(id, result);

        Assert.Equal(Encoding.UTF8.GetByteCount(json) + 1, measured);
        Assert.Contains("\\u", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            measured,
            ToolsListPageFitter.Fit([tool], 0, id, measured).FrameBytes);
    }

    [Fact]
    public void StartupPreflight_FailsClosedWhenLargestIndivisibleToolMissesCapByOne()
    {
        var tools = ActiveTools(out var catalog);
        var measured = ToolsListPageFitter.Preflight(
            tools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes);
        var tooSmall = measured.LargestSingleToolFrameBytes - 1;

        var error = Assert.Throws<ToolsListStartupValidationException>(() =>
            new ToolsListPaginationFilters(
                tools,
                catalog.CatalogVersion,
                CursorOptions() with { MaxResponseFrameBytes = tooSmall }));

        Assert.Contains("startup preflight failed", error.Message, StringComparison.Ordinal);
        Assert.Contains(measured.LargestSingleToolName, error.Message, StringComparison.Ordinal);
        Assert.Contains(
            measured.LargestSingleToolFrameBytes.ToString(),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProtocolFitter_RequestIdDoesNotChangePageMembership()
    {
        var tools = ActiveTools(out _);
        var minimum = ToolsListPageFitter.Preflight(
            tools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes).MinimumViableFrameBytes;

        var shortId = ToolsListPageFitter.FitProtocolPage(
            tools,
            0,
            new RequestId(1),
            minimum);
        var longId = ToolsListPageFitter.FitProtocolPage(
            tools,
            0,
            new RequestId(new string('x', 126)),
            minimum);

        Assert.Equal(shortId.NextIndex, longId.NextIndex);
        Assert.Equal(
            shortId.Result.Tools.Select(tool => tool.Name),
            longId.Result.Tools.Select(tool => tool.Name));
        Assert.True(shortId.FrameBytes <= minimum);
        Assert.True(longId.FrameBytes <= minimum);
    }

    [Fact]
    public void Cursor_IsOpaqueCanonicalAndRetrySafeWithinIdleAndAbsoluteTtl()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var options = CursorOptions(
            idle: TimeSpan.FromMinutes(1),
            absolute: TimeSpan.FromMinutes(5));
        var registry = new ToolsListCursorRegistry(options, () => now);
        var binding = Binding("server-a", ToolContractVersions.V2);

        var cursor = registry.Issue(binding, 17);

        Assert.True(ToolsListCursorRegistry.HasCanonicalShape(cursor));
        Assert.Matches("^tlc_[0-9a-f]{32}$", cursor);
        Assert.Equal(17, registry.Redeem(cursor, binding));
        now += TimeSpan.FromSeconds(50);
        Assert.Equal(17, registry.Redeem(cursor, binding));
        now += TimeSpan.FromSeconds(50);
        Assert.Equal(17, registry.Redeem(cursor, binding));
        Assert.Equal(1, registry.ActiveCount);
        Assert.Equal(0, registry.TombstoneCount);
    }

    [Fact]
    public void Cursor_ExpiresByIdleOrAbsoluteTtlAndLeavesBoundedTombstone()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var registry = new ToolsListCursorRegistry(
            CursorOptions(
                idle: TimeSpan.FromMinutes(1),
                absolute: TimeSpan.FromMinutes(2)),
            () => now);
        var binding = Binding("server-a", ToolContractVersions.V2);
        var idleCursor = registry.Issue(binding, 1);
        now += TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1);

        var idle = Assert.Throws<ToolsListCursorException>(() =>
            registry.Redeem(idleCursor, binding));

        Assert.Equal(ToolsListCursorFailure.Expired, idle.Failure);
        var absoluteCursor = registry.Issue(binding, 2);
        now += TimeSpan.FromSeconds(50);
        Assert.Equal(2, registry.Redeem(absoluteCursor, binding));
        now += TimeSpan.FromSeconds(50);
        Assert.Equal(2, registry.Redeem(absoluteCursor, binding));
        now += TimeSpan.FromSeconds(20) + TimeSpan.FromTicks(1);
        var absolute = Assert.Throws<ToolsListCursorException>(() =>
            registry.Redeem(absoluteCursor, binding));
        Assert.Equal(ToolsListCursorFailure.Expired, absolute.Failure);
        Assert.Equal(0, registry.ActiveCount);
        Assert.InRange(registry.TombstoneCount, 1, 2);
    }

    [Fact]
    public void WrongBinding_DoesNotInvalidateCursorForOriginalServerCatalogOrMode()
    {
        var registry = new ToolsListCursorRegistry(CursorOptions());
        var original = Binding("server-a", ToolContractVersions.V2);
        var cursor = registry.Issue(original, 9);
        var wrongBindings = new[]
        {
            original with { ServerInstanceId = "server-b" },
            original with { CatalogVersion = "catalog-b" },
            original with { ContractMode = "legacy" },
            original with { DiscoveryOrderHash = "order-b" },
        };

        foreach (var wrong in wrongBindings)
        {
            var error = Assert.Throws<ToolsListCursorException>(() =>
                registry.Redeem(cursor, wrong));
            Assert.Equal(ToolsListCursorFailure.BindingMismatch, error.Failure);
        }

        Assert.Equal(9, registry.Redeem(cursor, original));
        Assert.Equal(1, registry.ActiveCount);
    }

    [Fact]
    public void CursorQuota_FailsClosedWithoutOffsetFallbackOrDestroyingExistingCursor()
    {
        var options = CursorOptions() with { MaxActiveCursors = 1 };
        var registry = new ToolsListCursorRegistry(options);
        var binding = Binding("server-a", ToolContractVersions.V2);
        var cursor = registry.Issue(binding, 4);

        var error = Assert.Throws<ToolsListCursorException>(() =>
            registry.Issue(binding, 5));

        Assert.Equal(ToolsListCursorFailure.QuotaExceeded, error.Failure);
        Assert.Equal(4, registry.Redeem(cursor, binding));
        Assert.True(ToolsListCursorRegistry.HasCanonicalShape(cursor));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("tlc_0000000000000000000000000000000G")]
    [InlineData("tlc_00000000000000000000000000000000")]
    public async Task MalformedTamperedOrUnknownCursor_IsCanonicalInvalidParams(string cursor)
    {
        var filters = ActiveFilters();
        var delegated = false;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            delegated = true;
            return Task.CompletedTask;
        });
        var request = ToolsListRequest(new RequestId(1), cursor);

        var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
            incoming(Context(request), CancellationToken.None));

        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
        Assert.Equal("Invalid tools/list cursor.", error.Message);
        Assert.False(delegated);
        Assert.Equal(0, filters.PendingRequestCount);
    }

    [Fact]
    public async Task ExpiredCursor_IsProtocolInvalidParamsWithoutDelegating()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var tools = ActiveTools(out var catalog);
        var options = CursorOptions(
            idle: TimeSpan.FromMinutes(1),
            absolute: TimeSpan.FromMinutes(5));
        var registry = new ToolsListCursorRegistry(options, () => now);
        var filters = new ToolsListPaginationFilters(
            tools,
            catalog.CatalogVersion,
            options,
            registry: registry,
            serverInstanceId: "server-a");
        var cursor = filters.Issue(1);
        now += TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1);
        var delegated = false;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            delegated = true;
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => incoming(
            Context(ToolsListRequest(new RequestId(20), cursor)),
            CancellationToken.None));

        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
        Assert.Equal("Invalid tools/list cursor.", error.Message);
        Assert.False(delegated);
    }

    [Fact]
    public async Task CursorQuota_IsProtocolInternalErrorAndNeverReturnsAnUnpagedCatalog()
    {
        var tools = ActiveTools(out var catalog);
        var minimum = ToolsListPageFitter.Preflight(
            tools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes).MinimumViableFrameBytes;
        var options = CursorOptions() with
        {
            MaxResponseFrameBytes = minimum,
            MaxActiveCursors = 1,
        };
        var filters = new ToolsListPaginationFilters(
            tools,
            catalog.CatalogVersion,
            options,
            serverInstanceId: "server-a");
        _ = filters.Issue(1);
        var delegated = false;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            delegated = true;
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => incoming(
            Context(ToolsListRequest(new RequestId(21), cursor: null)),
            CancellationToken.None));

        Assert.Equal(McpErrorCode.InternalError, error.ErrorCode);
        Assert.Equal("The tools/list cursor registry is unavailable.", error.Message);
        Assert.False(delegated);
        Assert.Equal(0, filters.PendingRequestCount);
        Assert.Equal(1, filters.ActiveCursorCount);
    }

    [Fact]
    public async Task IncomingCorrelation_SurvivesReturnAndSupportsMultipleRequestIdKinds()
    {
        var filters = ActiveFilters();
        var incoming = filters.CreateIncomingFilter()(static (_, _) => Task.CompletedTask);
        var outgoing = filters.CreateOutgoingFilter()(static (_, _) => Task.CompletedTask);
        var firstRequest = ToolsListRequest(new RequestId(7), cursor: null);
        var secondRequest = ToolsListRequest(new RequestId("请求-二"), cursor: null);

        await incoming(Context(firstRequest), CancellationToken.None);
        await incoming(Context(secondRequest), CancellationToken.None);

        Assert.Equal(2, filters.PendingRequestCount);
        var second = EmptyResponse(secondRequest.Id);
        var first = EmptyResponse(firstRequest.Id);
        await outgoing(Context(second), CancellationToken.None);
        await outgoing(Context(first), CancellationToken.None);
        Assert.NotEmpty(ResultTools(second));
        Assert.NotEmpty(ResultTools(first));
        Assert.Equal(0, filters.PendingRequestCount);
        Assert.Equal(2, filters.EmittedPageCount);
    }

    [Fact]
    public async Task AllPages_CloseExactlyOnceInValidatedOrderAndPreserveWholeToolSchemas()
    {
        var tools = ActiveTools(out var catalog);
        var minimum = ToolsListPageFitter.Preflight(
            tools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes).MinimumViableFrameBytes;
        var filters = new ToolsListPaginationFilters(
            tools,
            catalog.CatalogVersion,
            CursorOptions() with { MaxResponseFrameBytes = minimum });
        var observed = new List<string>();
        var pageFrames = new List<int>();
        JsonObject? firstTool = null;
        JsonObject? lastTool = null;
        string? cursor = null;
        var pageIndex = 0;

        do
        {
            var page = await SendPageAsync(filters, new RequestId($"page-{pageIndex}"), cursor);
            Assert.NotEmpty(page.Tools);
            Assert.True(page.FrameBytes <= minimum);
            pageFrames.Add(page.FrameBytes);
            firstTool ??= (JsonObject)page.Tools[0]!.DeepClone();
            lastTool = (JsonObject)page.Tools[^1]!.DeepClone();
            observed.AddRange(page.Tools.Select(node => node!["name"]!.GetValue<string>()));
            cursor = page.Cursor;
            pageIndex++;
            Assert.InRange(pageIndex, 1, tools.Count);
        }
        while (cursor is not null);

        Assert.True(pageIndex >= 3);
        Assert.Equal(tools.Select(tool => tool.Name), observed);
        Assert.Equal(catalog.Tools.Count, observed.Distinct(StringComparer.Ordinal).Count());
        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(tools[0], McpJsonUtilities.DefaultOptions),
            firstTool));
        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(tools[^1], McpJsonUtilities.DefaultOptions),
            lastTool));
        Assert.Equal(pageIndex, filters.EmittedPageCount);
        Assert.Equal(pageFrames.Sum(value => (long)value), filters.EmittedPageFrameBytes);
        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(
                new ListToolsResult { Tools = tools.ToArray() },
                McpJsonUtilities.DefaultOptions).Length,
            filters.Preflight.AggregateCatalogResultBytes);
    }

    [Fact]
    public async Task ResponseLossRetry_ReplaysSamePageWithoutConsumingParentCursor()
    {
        var filters = ActiveFilters();
        var first = await SendPageAsync(filters, new RequestId("first"), cursor: null);
        Assert.NotNull(first.Cursor);

        var attempt = await SendPageAsync(filters, new RequestId("attempt"), first.Cursor);
        var countAfterAttempt = filters.ActiveCursorCount;
        var retry = await SendPageAsync(filters, new RequestId("retry"), first.Cursor);

        Assert.Equal(
            attempt.Tools.Select(tool => tool!["name"]!.GetValue<string>()),
            retry.Tools.Select(tool => tool!["name"]!.GetValue<string>()));
        Assert.Equal(attempt.Cursor, retry.Cursor);
        Assert.Equal(countAfterAttempt, filters.ActiveCursorCount);
        Assert.True(filters.ActiveCursorCount >= 1);
    }

    [Fact]
    public async Task RepeatedFirstAndMiddlePageRetries_DoNotConsumeCursorQuota()
    {
        var filters = ActiveFilters();
        var first = await SendPageAsync(filters, new RequestId("root-0"), cursor: null);
        Assert.NotNull(first.Cursor);
        var activeAfterFirst = filters.ActiveCursorCount;

        for (var index = 1; index <= 100; index++)
        {
            var retry = await SendPageAsync(
                filters,
                new RequestId($"root-{index}"),
                cursor: null);
            Assert.Equal(first.Cursor, retry.Cursor);
            Assert.Equal(activeAfterFirst, filters.ActiveCursorCount);
        }

        var second = await SendPageAsync(filters, new RequestId("middle-0"), first.Cursor);
        Assert.NotNull(second.Cursor);
        var activeAfterSecond = filters.ActiveCursorCount;
        for (var index = 1; index <= 100; index++)
        {
            var retry = await SendPageAsync(
                filters,
                new RequestId($"middle-{index}"),
                first.Cursor);
            Assert.Equal(second.Cursor, retry.Cursor);
            Assert.Equal(activeAfterSecond, filters.ActiveCursorCount);
        }
    }

    [Fact]
    public async Task IncomingOrOutgoingFailure_CleansCorrelationAndRevokesUnsentChildCursor()
    {
        var incomingFailure = ActiveFilters();
        var throwingIncoming = incomingFailure.CreateIncomingFilter()(
            static (_, _) => throw new InvalidOperationException("handler failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => throwingIncoming(
            Context(ToolsListRequest(new RequestId(30), cursor: null)),
            CancellationToken.None));
        Assert.Equal(0, incomingFailure.PendingRequestCount);
        Assert.Equal(0, incomingFailure.ActiveCursorCount);

        var outgoingFailure = ActiveFilters();
        var incoming = outgoingFailure.CreateIncomingFilter()(static (_, _) => Task.CompletedTask);
        var request = ToolsListRequest(new RequestId(31), cursor: null);
        await incoming(Context(request), CancellationToken.None);
        var response = EmptyResponse(request.Id);
        var outgoing = outgoingFailure.CreateOutgoingFilter()(
            static (_, _) => throw new InvalidOperationException("transport failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            outgoing(Context(response), CancellationToken.None));
        var revokedCursor = ResultObject(response)["nextCursor"]!.GetValue<string>();
        Assert.Equal(0, outgoingFailure.PendingRequestCount);
        Assert.Equal(0, outgoingFailure.ActiveCursorCount);
        var revoked = Assert.Throws<ToolsListCursorException>(() =>
            outgoingFailure.Redeem(revokedCursor));
        Assert.Equal(ToolsListCursorFailure.Revoked, revoked.Failure);
    }

    private static ToolsListPaginationFilters ActiveFilters()
    {
        var tools = ActiveTools(out var catalog);
        var minimum = ToolsListPageFitter.Preflight(
            tools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes).MinimumViableFrameBytes;
        return new ToolsListPaginationFilters(
            tools,
            catalog.CatalogVersion,
            CursorOptions() with { MaxResponseFrameBytes = minimum });
    }

    private static IReadOnlyList<Tool> ActiveTools(out ActiveToolCatalog catalog)
    {
        catalog = ActiveToolCatalog.LoadAndValidate();
        return catalog.CreateProtocolTools(new DeferredCatalogServiceProvider());
    }

    private static ToolsListPaginationOptions CursorOptions(
        TimeSpan? idle = null,
        TimeSpan? absolute = null)
        => ToolsListPaginationOptions.Default with
        {
            CursorIdleTtl = idle ?? TimeSpan.FromMinutes(2),
            CursorAbsoluteTtl = absolute ?? TimeSpan.FromMinutes(15),
            MaxActiveCursors = 1_024,
            MaxTombstones = 64,
        };

    private static ToolsListCursorBinding Binding(string server, string mode)
        => new(server, "catalog-a", mode, "order-a");

    private static JsonRpcRequest ToolsListRequest(RequestId id, string? cursor)
        => new()
        {
            Id = id,
            Method = RequestMethods.ToolsList,
            Params = cursor is null
                ? new JsonObject()
                : new JsonObject { ["cursor"] = cursor },
        };

    private static JsonRpcResponse EmptyResponse(RequestId id)
        => new()
        {
            Id = id,
            Result = new JsonObject(),
        };

    private static MessageContext Context(JsonRpcMessage message)
        => new(Mock.Of<McpServer>(), message);

    private static JsonObject ResultObject(JsonRpcResponse response)
        => Assert.IsType<JsonObject>(response.Result);

    private static JsonArray ResultTools(JsonRpcResponse response)
        => Assert.IsType<JsonArray>(ResultObject(response)["tools"]);

    private static async Task<ObservedPage> SendPageAsync(
        ToolsListPaginationFilters filters,
        RequestId id,
        string? cursor)
    {
        var incoming = filters.CreateIncomingFilter()(static (_, _) => Task.CompletedTask);
        var outgoing = filters.CreateOutgoingFilter()(static (_, _) => Task.CompletedTask);
        var request = ToolsListRequest(id, cursor);
        await incoming(Context(request), CancellationToken.None);
        var response = EmptyResponse(id);
        await outgoing(Context(response), CancellationToken.None);
        var result = ResultObject(response);
        var frameBytes = JsonSerializer.SerializeToUtf8Bytes(
            response,
            McpJsonUtilities.DefaultOptions).Length + 1;
        return new ObservedPage(
            Assert.IsType<JsonArray>(result["tools"]),
            result["nextCursor"]?.GetValue<string>(),
            frameBytes);
    }

    private sealed record ObservedPage(
        JsonArray Tools,
        string? Cursor,
        int FrameBytes);
}
