using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal sealed class ToolsListStartupValidationException(string message)
    : InvalidOperationException(message);

internal sealed record ToolsListPaginationOptions(
    int MaxResponseFrameBytes,
    TimeSpan CursorIdleTtl,
    TimeSpan CursorAbsoluteTtl,
    int MaxActiveCursors,
    int MaxTombstones)
{
    public const int DefaultMaxResponseFrameBytes = ToolResponseBudgetOptions.DefaultMaxResponseFrameBytes;
    public const int HardMaxResponseFrameBytes = ToolResponseBudgetOptions.HardMaxResponseFrameBytes;
    public const int MinimumConfiguredFrameBytes = 4_096;
    public const string MaxResponseFrameBytesEnvironmentVariable =
        "WPAMCP_MAX_JSON_RPC_RESPONSE_BYTES";

    public static ToolsListPaginationOptions Default { get; } = new(
        DefaultMaxResponseFrameBytes,
        CursorIdleTtl: TimeSpan.FromMinutes(2),
        CursorAbsoluteTtl: TimeSpan.FromMinutes(15),
        MaxActiveCursors: 1_024,
        MaxTombstones: 4_096);

    public static ToolsListPaginationOptions FromEnvironment(
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var raw = getEnvironmentVariable(MaxResponseFrameBytesEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return Default;

        if (!int.TryParse(raw, out var parsed)
            || parsed < MinimumConfiguredFrameBytes
            || parsed > HardMaxResponseFrameBytes)
        {
            throw new ToolsListStartupValidationException(
                $"{MaxResponseFrameBytesEnvironmentVariable} must be an integer from " +
                $"{MinimumConfiguredFrameBytes} through {HardMaxResponseFrameBytes}.");
        }

        return Default with { MaxResponseFrameBytes = parsed };
    }

    public void Validate()
    {
        if (MaxResponseFrameBytes < MinimumConfiguredFrameBytes
            || MaxResponseFrameBytes > HardMaxResponseFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResponseFrameBytes),
                $"Response frame cap must be from {MinimumConfiguredFrameBytes} through " +
                $"{HardMaxResponseFrameBytes} bytes.");
        }
        if (CursorIdleTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(CursorIdleTtl));
        if (CursorAbsoluteTtl < CursorIdleTtl)
            throw new ArgumentOutOfRangeException(nameof(CursorAbsoluteTtl));
        if (MaxActiveCursors <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxActiveCursors));
        if (MaxTombstones <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTombstones));
    }
}

internal readonly record struct ToolsListCursorBinding(
    string ServerInstanceId,
    string CatalogVersion,
    string ContractMode,
    string DiscoveryOrderHash,
    string CapabilityPolicyIdentity = "full");

internal enum ToolsListCursorFailure
{
    Malformed,
    Unknown,
    Expired,
    Revoked,
    BindingMismatch,
    QuotaExceeded,
}

internal sealed class ToolsListCursorException(
    ToolsListCursorFailure failure,
    string message) : InvalidOperationException(message)
{
    public ToolsListCursorFailure Failure { get; } = failure;
}

/// <summary>
/// Stores opaque, retry-safe tools/list cursors. Tokens contain only a CSPRNG locator;
/// catalog, mode, server instance, ordering, and next-index state remain server-side.
/// </summary>
internal sealed class ToolsListCursorRegistry
{
    internal const string Prefix = "tlc_";
    internal const int LocatorHexLength = 32;
    internal const int TokenLength = 36;

    private readonly object _gate = new();
    private readonly Dictionary<string, CursorEntry> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<ToolsListCursorBinding, string> _rootContinuations = [];
    private readonly Dictionary<string, Tombstone> _tombstones = new(StringComparer.Ordinal);
    private readonly Queue<string> _tombstoneOrder = new();
    private readonly ToolsListPaginationOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;

    public ToolsListCursorRegistry(
        ToolsListPaginationOptions options,
        Func<DateTimeOffset>? utcNow = null)
    {
        options.Validate();
        _options = options;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    internal int ActiveCount
    {
        get
        {
            lock (_gate)
                return _active.Count;
        }
    }

    internal int TombstoneCount
    {
        get
        {
            lock (_gate)
                return _tombstones.Count;
        }
    }

    public string Issue(ToolsListCursorBinding binding, int nextIndex)
    {
        if (nextIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(nextIndex));

        lock (_gate)
        {
            var now = _utcNow();
            Prune(now);
            return IssueLocked(binding, nextIndex, now);
        }
    }

    /// <summary>
    /// Returns the stable child continuation for one logical page. Retrying a parent
    /// cursor after response loss therefore cannot mint unbounded orphan cursors and
    /// exhaust the registry. A null parent denotes the deterministic first page.
    /// </summary>
    public ToolsListContinuation GetOrIssueContinuation(
        ToolsListCursorBinding binding,
        string? parentToken,
        int nextIndex)
    {
        if (nextIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(nextIndex));

        lock (_gate)
        {
            var now = _utcNow();
            Prune(now);

            string? existingToken;
            if (parentToken is null)
            {
                _rootContinuations.TryGetValue(binding, out existingToken);
            }
            else
            {
                if (!_active.TryGetValue(parentToken, out var parent)
                    || parent.Binding != binding)
                {
                    throw new ToolsListCursorException(
                        ToolsListCursorFailure.BindingMismatch,
                        "The tools/list cursor binding is invalid.");
                }
                existingToken = parent.ContinuationToken;
            }

            if (existingToken is not null
                && _active.TryGetValue(existingToken, out var existing)
                && existing.Binding == binding
                && existing.NextIndex == nextIndex)
            {
                _active[existingToken] = existing with { LastAccessUtc = now };
                return new ToolsListContinuation(existingToken, Created: false);
            }

            var token = IssueLocked(binding, nextIndex, now);
            if (parentToken is null)
            {
                _rootContinuations[binding] = token;
            }
            else
            {
                var parent = _active[parentToken];
                _active[parentToken] = parent with { ContinuationToken = token };
            }
            return new ToolsListContinuation(token, Created: true);
        }
    }

    public int Redeem(string token, ToolsListCursorBinding expectedBinding)
    {
        if (!HasCanonicalShape(token))
        {
            throw new ToolsListCursorException(
                ToolsListCursorFailure.Malformed,
                "The tools/list cursor is malformed.");
        }

        lock (_gate)
        {
            var now = _utcNow();
            Prune(now);
            if (_tombstones.TryGetValue(token, out var tombstone))
            {
                throw new ToolsListCursorException(
                    tombstone.Failure,
                    "The tools/list cursor is no longer valid.");
            }
            if (!_active.TryGetValue(token, out var entry))
            {
                throw new ToolsListCursorException(
                    ToolsListCursorFailure.Unknown,
                    "The tools/list cursor is unknown.");
            }

            if (now - entry.LastAccessUtc > _options.CursorIdleTtl
                || now - entry.IssuedUtc > _options.CursorAbsoluteTtl)
            {
                AddTombstone(token, ToolsListCursorFailure.Expired, now);
                throw new ToolsListCursorException(
                    ToolsListCursorFailure.Expired,
                    "The tools/list cursor has expired.");
            }
            if (entry.Binding != expectedBinding)
            {
                throw new ToolsListCursorException(
                    ToolsListCursorFailure.BindingMismatch,
                    "The tools/list cursor binding is invalid.");
            }

            _active[token] = entry with { LastAccessUtc = now };
            return entry.NextIndex;
        }
    }

    public void Revoke(string? token)
    {
        if (token is null)
            return;

        lock (_gate)
        {
            var now = _utcNow();
            if (_active.Remove(token))
                AddTombstone(token, ToolsListCursorFailure.Revoked, now);
            Prune(now);
        }
    }

    internal static bool HasCanonicalShape(string? token)
    {
        if (token is null
            || token.Length != TokenLength
            || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = Prefix.Length; index < token.Length; index++)
        {
            var character = token[index];
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _active.ToArray())
        {
            if (now - pair.Value.LastAccessUtc <= _options.CursorIdleTtl
                && now - pair.Value.IssuedUtc <= _options.CursorAbsoluteTtl)
            {
                continue;
            }

            _active.Remove(pair.Key);
            AddTombstone(pair.Key, ToolsListCursorFailure.Expired, now);
        }

        while (_tombstoneOrder.Count > 0)
        {
            var token = _tombstoneOrder.Peek();
            if (!_tombstones.TryGetValue(token, out var tombstone))
            {
                _tombstoneOrder.Dequeue();
                continue;
            }
            if (_tombstones.Count <= _options.MaxTombstones
                && now - tombstone.CreatedUtc <= _options.CursorAbsoluteTtl)
            {
                break;
            }

            _tombstoneOrder.Dequeue();
            _tombstones.Remove(token);
        }
    }

    private string IssueLocked(
        ToolsListCursorBinding binding,
        int nextIndex,
        DateTimeOffset now)
    {
        if (_active.Count >= _options.MaxActiveCursors)
        {
            throw new ToolsListCursorException(
                ToolsListCursorFailure.QuotaExceeded,
                "The tools/list cursor registry is at capacity.");
        }

        for (var attempt = 0; attempt < 32; attempt++)
        {
            var token = Prefix + Convert.ToHexString(
                RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            if (_active.ContainsKey(token) || _tombstones.ContainsKey(token))
                continue;

            _active.Add(token, new CursorEntry(binding, nextIndex, now, now, null));
            return token;
        }

        throw new ToolsListCursorException(
            ToolsListCursorFailure.QuotaExceeded,
            "The tools/list cursor registry could not mint a unique locator.");
    }

    private void AddTombstone(
        string token,
        ToolsListCursorFailure failure,
        DateTimeOffset now)
    {
        if (_tombstones.TryAdd(token, new Tombstone(failure, now)))
            _tombstoneOrder.Enqueue(token);

        while (_tombstones.Count > _options.MaxTombstones
               && _tombstoneOrder.TryDequeue(out var evicted))
        {
            _tombstones.Remove(evicted);
        }
    }

    private sealed record CursorEntry(
        ToolsListCursorBinding Binding,
        int NextIndex,
        DateTimeOffset IssuedUtc,
        DateTimeOffset LastAccessUtc,
        string? ContinuationToken);

    private sealed record Tombstone(
        ToolsListCursorFailure Failure,
        DateTimeOffset CreatedUtc);
}

internal readonly record struct ToolsListContinuation(string Token, bool Created);

internal sealed record ToolsListPage(
    ListToolsResult Result,
    int StartIndex,
    int NextIndex,
    int FrameBytes);

internal sealed record ToolsListPagingPreflight(
    int MaxResponseFrameBytes,
    int MinimumSuccessFrameBytes,
    int LargestSingleToolFrameBytes,
    string LargestSingleToolName,
    int MinimumViableFrameBytes,
    int AggregateCatalogResultBytes);

internal static class ToolsListPageFitter
{
    // The stdio transport terminates each compact JSON-RPC message with one LF byte.
    private const int StdioFramingBytes = 1;
    // Two JSON quotes plus 126 ASCII content bytes is the exact 128-byte
    // serialized request-id ceiling.
    private static readonly RequestId PreflightRequestId = new(new string('r', 126));
    private static readonly string CursorPlaceholder =
        ToolsListCursorRegistry.Prefix + new string('0', ToolsListCursorRegistry.LocatorHexLength);

    public static ToolsListPagingPreflight Preflight(
        IReadOnlyList<Tool> tools,
        int maxResponseFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count == 0)
            throw new ToolsListStartupValidationException(
                "The active tools catalog must not be empty.");

        var minimumSuccess = MeasureFrame(
            PreflightRequestId,
            new ListToolsResult { Tools = [] });
        var largestBytes = 0;
        var largestName = string.Empty;
        foreach (var tool in tools)
        {
            var bytes = MeasureFrame(
                PreflightRequestId,
                new ListToolsResult
                {
                    Tools = [tool],
                    NextCursor = CursorPlaceholder,
                });
            if (bytes > largestBytes)
            {
                largestBytes = bytes;
                largestName = tool.Name;
            }
        }

        var minimumViable = Math.Max(minimumSuccess, largestBytes);
        if (minimumViable > maxResponseFrameBytes)
        {
            throw new ToolsListStartupValidationException(
                $"tools/list startup preflight failed: response cap {maxResponseFrameBytes} bytes " +
                $"is below the measured minimum {minimumViable} bytes; largest indivisible " +
                $"tool is '{largestName}' at {largestBytes} bytes.");
        }

        var aggregateBytes = JsonSerializer.SerializeToUtf8Bytes(
            new ListToolsResult { Tools = tools.ToArray() },
            McpJsonUtilities.DefaultOptions).Length;
        return new ToolsListPagingPreflight(
            maxResponseFrameBytes,
            minimumSuccess,
            largestBytes,
            largestName,
            minimumViable,
            aggregateBytes);
    }

    public static ToolsListPage Fit(
        IReadOnlyList<Tool> tools,
        int startIndex,
        RequestId requestId,
        int maxResponseFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (startIndex < 0 || startIndex >= tools.Count)
            throw new InvalidOperationException("The tools/list cursor next index is outside the catalog.");

        ListToolsResult? fitted = null;
        var fittedEnd = startIndex;
        var fittedBytes = 0;
        for (var end = startIndex + 1; end <= tools.Count; end++)
        {
            var candidate = new ListToolsResult
            {
                Tools = tools.Skip(startIndex).Take(end - startIndex).ToArray(),
                NextCursor = end < tools.Count ? CursorPlaceholder : null,
            };
            var candidateBytes = MeasureFrame(requestId, candidate);
            if (candidateBytes > maxResponseFrameBytes)
                break;

            fitted = candidate;
            fittedEnd = end;
            fittedBytes = candidateBytes;
        }

        if (fitted is null)
        {
            throw new InvalidOperationException(
                $"tools/list could not fit the indivisible tool at index {startIndex} " +
                $"within {maxResponseFrameBytes} bytes after successful startup preflight.");
        }

        return new ToolsListPage(fitted, startIndex, fittedEnd, fittedBytes);
    }

    /// <summary>
    /// Selects page membership with the conservative startup request-id sentinel so
    /// JSON-RPC correlation values cannot change discovery boundaries. The actual id
    /// is used only for the final frame measurement.
    /// </summary>
    public static ToolsListPage FitProtocolPage(
        IReadOnlyList<Tool> tools,
        int startIndex,
        RequestId actualRequestId,
        int maxResponseFrameBytes)
    {
        var page = Fit(
            tools,
            startIndex,
            PreflightRequestId,
            maxResponseFrameBytes);
        var actualBytes = MeasureFrame(actualRequestId, page.Result);
        if (actualBytes > maxResponseFrameBytes)
        {
            throw new InvalidOperationException(
                "The JSON-RPC request id exceeds the tools/list response budget boundary.");
        }

        return page with { FrameBytes = actualBytes };
    }

    public static int MeasureFrame(RequestId requestId, ListToolsResult result)
        => JsonSerializer.SerializeToUtf8Bytes(
               new JsonRpcResponse
               {
                   Id = requestId,
                   Result = JsonSerializer.SerializeToNode(
                       result,
                       McpJsonUtilities.DefaultOptions),
               },
               McpJsonUtilities.DefaultOptions).Length
           + StdioFramingBytes;
}

/// <summary>
/// Replaces the SDK's complete ToolCollection list response with an exact-frame-fitted
/// page from the same registered tool objects. Incoming work must remain correlated after
/// its delegate returns because SDK outgoing filters run later.
/// </summary>
internal sealed class ToolsListPaginationFilters
{
    private readonly IReadOnlyList<Tool> _tools;
    private readonly ToolsListPaginationOptions _options;
    private readonly ToolsListCursorBinding _binding;
    private readonly ToolsListCursorRegistry _registry;
    private readonly ToolTelemetry? _telemetry;
    private readonly ConcurrentDictionary<string, PendingPage> _pending = new();
    private long _pageCount;
    private long _pageFrameBytes;

    public ToolsListPaginationFilters(
        IReadOnlyList<Tool> tools,
        string catalogVersion,
        ToolsListPaginationOptions? options = null,
        string contractMode = ToolContractVersions.V2,
        ToolTelemetry? telemetry = null,
        ToolsListCursorRegistry? registry = null,
        string? serverInstanceId = null,
        string capabilityPolicyIdentity = "full")
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (string.IsNullOrWhiteSpace(catalogVersion))
            throw new ArgumentException("Catalog version is required.", nameof(catalogVersion));
        if (string.IsNullOrWhiteSpace(capabilityPolicyIdentity))
        {
            throw new ArgumentException(
                "Capability policy identity is required.",
                nameof(capabilityPolicyIdentity));
        }
        if (!string.Equals(contractMode, ToolContractVersions.V2, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractMode),
                contractMode,
                $"Only active contract mode '{ToolContractVersions.V2}' is supported.");
        }

        _tools = tools.ToArray();
        _options = options ?? ToolsListPaginationOptions.Default;
        _options.Validate();
        _telemetry = telemetry;
        _registry = registry ?? new ToolsListCursorRegistry(_options);
        _binding = new ToolsListCursorBinding(
            serverInstanceId ?? RandomHex128(),
            catalogVersion,
            contractMode,
            ComputeDiscoveryOrderHash(_tools),
            capabilityPolicyIdentity);
        Preflight = ToolsListPageFitter.Preflight(_tools, _options.MaxResponseFrameBytes);
    }

    public ToolsListPagingPreflight Preflight { get; }
    internal IReadOnlyList<Tool> ActiveTools => _tools;
    internal int PendingRequestCount => _pending.Count;
    internal long EmittedPageCount => Interlocked.Read(ref _pageCount);
    internal long EmittedPageFrameBytes => Interlocked.Read(ref _pageFrameBytes);
    internal int ActiveCursorCount => _registry.ActiveCount;
    internal ToolsListCursorBinding Binding => _binding;

    public McpMessageFilter CreateIncomingFilter()
        => next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is not JsonRpcRequest request
                || !string.Equals(request.Method, RequestMethods.ToolsList, StringComparison.Ordinal))
            {
                await next(context, cancellationToken);
                return;
            }

            var key = MessageIdKey(request);
            var issuedCursor = default(string);
            var issuedCursorWasCreated = false;
            try
            {
                var cursor = ReadCursor(request.Params);
                var startIndex = cursor is null ? 0 : Redeem(cursor);
                var page = ToolsListPageFitter.FitProtocolPage(
                    _tools,
                    startIndex,
                    request.Id,
                    _options.MaxResponseFrameBytes);

                if (page.NextIndex < _tools.Count)
                {
                    var continuation = _registry.GetOrIssueContinuation(
                        _binding,
                        cursor,
                        page.NextIndex);
                    issuedCursor = continuation.Token;
                    issuedCursorWasCreated = continuation.Created;
                    page.Result.NextCursor = issuedCursor;
                    var exactBytes = ToolsListPageFitter.MeasureFrame(request.Id, page.Result);
                    page = page with { FrameBytes = exactBytes };
                }

                if (!_pending.TryAdd(key, new PendingPage(
                        page,
                        issuedCursor,
                        issuedCursorWasCreated)))
                {
                    throw new McpProtocolException(
                        "A tools/list request with this id is already pending.",
                        McpErrorCode.InvalidRequest);
                }

                try
                {
                    await next(context, cancellationToken);
                }
                catch
                {
                    if (_pending.TryRemove(key, out var pending)
                        && pending.IssuedCursorWasCreated)
                        _registry.Revoke(pending.IssuedCursor);
                    throw;
                }
            }
            catch (ToolsListCursorException ex)
            {
                if (issuedCursorWasCreated)
                    _registry.Revoke(issuedCursor);
                throw CursorProtocolException(ex);
            }
            catch
            {
                if (issuedCursorWasCreated)
                    _registry.Revoke(issuedCursor);
                throw;
            }
        };

    public McpMessageFilter CreateOutgoingFilter()
        => next => async (context, cancellationToken) =>
        {
            PendingPage? emitted = null;
            var actualFrameBytes = 0;
            if (context.JsonRpcMessage is JsonRpcMessageWithId messageWithId
                && _pending.TryRemove(MessageIdKey(messageWithId), out var pending))
            {
                if (context.JsonRpcMessage is JsonRpcResponse response)
                {
                    response.Result = JsonSerializer.SerializeToNode(
                        pending.Page.Result,
                        McpJsonUtilities.DefaultOptions);
                    actualFrameBytes = ToolsListPageFitter.MeasureFrame(
                        response.Id,
                        pending.Page.Result);
                    if (actualFrameBytes > _options.MaxResponseFrameBytes)
                    {
                        if (pending.IssuedCursorWasCreated)
                            _registry.Revoke(pending.IssuedCursor);
                        throw new InvalidOperationException(
                            "The fitted tools/list response exceeded its validated frame cap.");
                    }

                    emitted = pending;
                }
                else
                {
                    // The client never received the newly minted continuation.
                    if (pending.IssuedCursorWasCreated)
                        _registry.Revoke(pending.IssuedCursor);
                }
            }

            try
            {
                await next(context, cancellationToken);
            }
            catch
            {
                if (emitted?.IssuedCursorWasCreated == true)
                    _registry.Revoke(emitted.IssuedCursor);
                throw;
            }

            if (emitted is not null)
            {
                Interlocked.Increment(ref _pageCount);
                Interlocked.Add(ref _pageFrameBytes, actualFrameBytes);
                _telemetry?.RecordToolsListPage(
                    actualFrameBytes,
                    emitted.Page.Result.Tools.Count,
                    emitted.Page.Result.NextCursor is not null,
                    Preflight.AggregateCatalogResultBytes,
                    _options.MaxResponseFrameBytes);
            }
        };

    internal string Issue(int nextIndex) => _registry.Issue(_binding, nextIndex);
    internal int Redeem(string cursor) => _registry.Redeem(cursor, _binding);

    private static string? ReadCursor(object? value)
    {
        if (value is null)
            return null;

        try
        {
            var parameters = value switch
            {
                ListToolsRequestParams typed => typed,
                JsonElement element => element.Deserialize<ListToolsRequestParams>(
                    McpJsonUtilities.DefaultOptions),
                _ => JsonSerializer.Deserialize<ListToolsRequestParams>(
                    JsonSerializer.SerializeToUtf8Bytes(value, McpJsonUtilities.DefaultOptions),
                    McpJsonUtilities.DefaultOptions),
            };
            return parameters?.Cursor;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new McpProtocolException(
                "Invalid tools/list parameters.",
                ex,
                McpErrorCode.InvalidParams);
        }
    }

    private static McpProtocolException CursorProtocolException(
        ToolsListCursorException exception)
        => exception.Failure == ToolsListCursorFailure.QuotaExceeded
            ? new McpProtocolException(
                "The tools/list cursor registry is unavailable.",
                McpErrorCode.InternalError)
            : new McpProtocolException(
                "Invalid tools/list cursor.",
                McpErrorCode.InvalidParams);

    private static string MessageIdKey(JsonRpcMessageWithId message)
        => JsonSerializer.Serialize(message.Id, McpJsonUtilities.DefaultOptions);

    private static string ComputeDiscoveryOrderHash(IReadOnlyList<Tool> tools)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\n', tools.Select(tool => tool.Name)));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static string RandomHex128()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private sealed record PendingPage(
        ToolsListPage Page,
        string? IssuedCursor,
        bool IssuedCursorWasCreated);
}

/// <summary>
/// Allows programmatically-created tools to retain a root-service fallback without building
/// a second application service provider. Normal invocations resolve from request.Server.Services.
/// </summary>
internal sealed class DeferredCatalogServiceProvider : IServiceProvider
{
    private IServiceProvider? _services;

    public object? GetService(Type serviceType)
        => Volatile.Read(ref _services)?.GetService(serviceType);

    public void Bind(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (Interlocked.CompareExchange(ref _services, services, null) is not null)
            throw new InvalidOperationException("Catalog service provider is already bound.");
    }
}
