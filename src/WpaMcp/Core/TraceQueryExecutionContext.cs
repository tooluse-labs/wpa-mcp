using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpaMcp.Core;

internal sealed record TraceQueryReference(
    string TraceId,
    string RefKind,
    bool LoadedFromRawPath,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Binds exactly one incoming tool argument to one already-resolved generation.
/// AsyncLocal keeps concurrent stdio calls isolated; TraceCache consults this
/// context before any path parsing, stat, conversion, or symbol setup.
/// </summary>
internal static class TraceQueryExecutionContext
{
    private static readonly AsyncLocal<State?> CurrentState = new();

    internal static TraceQueryReference? CurrentReference =>
        CurrentState.Value?.Reference;

    internal static long? CurrentCacheGenerationSequence =>
        CurrentState.Value?.RegistryLease.CacheGenerationSequence;

    internal static CancellationToken CurrentCancellationToken =>
        CurrentState.Value?.CancellationToken ?? CancellationToken.None;

    internal static bool TryGetReadyFacts(out TraceFactsSnapshot snapshot)
    {
        var state = CurrentState.Value;
        if (state is not null &&
            state.RegistryLease.TryGetReadyFacts(out snapshot))
        {
            return true;
        }

        snapshot = null!;
        return false;
    }

    internal static IDisposable Begin(
        TraceCache cache,
        string originalArgument,
        ResolvedTraceReference resolved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalArgument);
        ArgumentNullException.ThrowIfNull(resolved);

        var prior = CurrentState.Value;
        CurrentState.Value = new State(
            cache,
            originalArgument,
            resolved.Lease,
            cancellationToken,
            new TraceQueryReference(
                resolved.Descriptor.TraceId,
                resolved.Descriptor.Persistence == TraceHandlePersistence.Persistent
                    ? "canonical"
                    : "ephemeral",
                resolved.Descriptor.LoadedFromRawPath,
                resolved.Warnings));
        return new Scope(prior);
    }

    internal static bool TryAcquireBound(
        TraceCache cache,
        string exactArgument,
        out TraceLease lease)
    {
        var state = CurrentState.Value;
        if (state is null ||
            !ReferenceEquals(state.Cache, cache) ||
            !string.Equals(state.OriginalArgument, exactArgument, StringComparison.Ordinal))
        {
            lease = null!;
            return false;
        }

        lease = state.RegistryLease.CloneBackendLease();
        return true;
    }

    private sealed record State(
        TraceCache Cache,
        string OriginalArgument,
        TraceHandleLease RegistryLease,
        CancellationToken CancellationToken,
        TraceQueryReference Reference);

    private sealed class Scope(State? prior) : IDisposable
    {
        private State? _prior = prior;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            CurrentState.Value = Interlocked.Exchange(ref _prior, null);
        }
    }
}

internal sealed class TraceQueryExecutionFilters
{
    private readonly HashSet<string> _analysisTools;
    private readonly TraceReferenceResolver _resolver;
    private readonly TraceCache _cache;
    private readonly StdioSessionPrincipal _principal;
    private readonly TraceAccessMode _mode;
    private readonly ConcurrentDictionary<string, PendingCompatibilityHandle> _pending = [];

    internal TraceQueryExecutionFilters(
        IReadOnlyList<Tool> activeTools,
        TraceReferenceResolver resolver,
        TraceCache cache,
        StdioSessionPrincipal principal,
        TraceAccessMode mode)
    {
        ArgumentNullException.ThrowIfNull(activeTools);
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _mode = mode;
        _analysisTools = activeTools
            .Where(tool => tool.Name is not ("load_trace" or "unload_trace" or "prepare_symbols") &&
                HasInputProperty(tool, "traceId"))
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal IReadOnlySet<string> AnalysisTools => _analysisTools;
    internal int PendingCompatibilityHandleCount => _pending.Count;

    public McpMessageFilter CreateIncomingFilter()
        => next => async (context, cancellationToken) =>
        {
            if (ToolContractMessageFilters.HasPreDispatchFailure(context))
            {
                await next(context, cancellationToken);
                return;
            }
            if (context.JsonRpcMessage is not JsonRpcRequest request ||
                !string.Equals(request.Method, RequestMethods.ToolsCall, StringComparison.Ordinal) ||
                !TryReadCall(request.Params, out var toolName, out var arguments) ||
                !_analysisTools.Contains(toolName))
            {
                await next(context, cancellationToken);
                return;
            }

            if (arguments["traceId"] is not JsonValue traceIdValue ||
                !traceIdValue.TryGetValue<string>(out var traceReference) ||
                string.IsNullOrEmpty(traceReference))
            {
                // SDK argument validation owns missing/wrong-type values. The filter
                // must not reinterpret a malformed argument as a path or trace ID.
                await next(context, cancellationToken);
                return;
            }

            ResolvedTraceReference? resolved = null;
            string? pendingKey = null;
            try
            {
                resolved = _resolver.ResolveQuery(
                    _principal.RegistryKey,
                    traceReference,
                    _mode,
                    cancellationToken);
                if (resolved.Descriptor.CanonicalHandleCreated)
                {
                    var key = MessageIdKey(request);
                    pendingKey = key;
                    var pending = new PendingCompatibilityHandle(
                        resolved.Descriptor.TraceId,
                        cancellationToken,
                        () => Rollback(key));
                    if (!_pending.TryAdd(key, pending))
                    {
                        pending.Dispose();
                        RollbackTraceId(resolved.Descriptor.TraceId);
                        throw new InvalidOperationException(
                            "A trace query request with this id is already pending.");
                    }

                    // Cancellation can run synchronously during Register before the
                    // pending entry becomes visible. Recheck after publication so an
                    // undeliverable compatibility handle can never remain persistent.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Rollback(key);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }

                using (resolved)
                using (TraceQueryExecutionContext.Begin(
                           _cache,
                           traceReference,
                           resolved,
                           cancellationToken))
                {
                    await next(context, cancellationToken);
                }
            }
            catch
            {
                if (resolved?.Descriptor.CanonicalHandleCreated == true)
                {
                    if (pendingKey is not null)
                        Rollback(pendingKey);
                    else
                        RollbackTraceId(resolved.Descriptor.TraceId);
                }
                throw;
            }
        };

    public McpMessageFilter CreateOutgoingFilter()
        => next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is not JsonRpcMessageWithId message ||
                !_pending.TryRemove(MessageIdKey(message), out var pending))
            {
                await next(context, cancellationToken);
                return;
            }

            var delivered = false;
            try
            {
                if (!IsSuccessfulToolResponse(context.JsonRpcMessage))
                {
                    await next(context, cancellationToken);
                    return;
                }

                await next(context, cancellationToken);
                // A later schema/fitting filter may replace an initially successful
                // result with isError=true. Commit only the final response that the
                // downstream transport accepted.
                delivered = IsSuccessfulToolResponse(context.JsonRpcMessage);
            }
            finally
            {
                pending.Dispose();
                if (!delivered)
                    RollbackTraceId(pending.TraceId);
            }
        };

    private void Rollback(string key)
    {
        if (_pending.TryRemove(key, out var pending))
        {
            pending.Dispose();
            RollbackTraceId(pending.TraceId);
        }
    }

    private void RollbackTraceId(string traceId) =>
        _resolver.RollbackUndeliveredCompatibilityHandle(
            _principal.RegistryKey,
            traceId);

    private static bool IsSuccessfulToolResponse(JsonRpcMessage message)
    {
        if (message is not JsonRpcResponse response)
            return false;
        var result = JsonSerializer.SerializeToElement(
            response.Result,
            McpJsonUtilities.DefaultOptions);
        return result.ValueKind != JsonValueKind.Object ||
               !result.TryGetProperty("isError", out var isError) ||
               isError.ValueKind != JsonValueKind.True;
    }

    private static bool HasInputProperty(Tool tool, string propertyName)
    {
        var schema = JsonSerializer.SerializeToElement(
            tool.InputSchema,
            McpJsonUtilities.DefaultOptions);
        return schema.ValueKind == JsonValueKind.Object &&
               schema.TryGetProperty("properties", out var properties) &&
               properties.ValueKind == JsonValueKind.Object &&
               properties.TryGetProperty(propertyName, out _);
    }

    private static bool TryReadCall(
        object? value,
        out string toolName,
        out JsonObject arguments)
    {
        try
        {
            var call = value switch
            {
                CallToolRequestParams typed => typed,
                JsonElement element => element.Deserialize<CallToolRequestParams>(
                    McpJsonUtilities.DefaultOptions),
                _ => JsonSerializer.Deserialize<CallToolRequestParams>(
                    JsonSerializer.SerializeToUtf8Bytes(
                        value,
                        McpJsonUtilities.DefaultOptions),
                    McpJsonUtilities.DefaultOptions),
            };
            if (string.IsNullOrEmpty(call?.Name))
                throw new JsonException("Tool name is missing.");

            arguments = JsonSerializer.SerializeToNode(
                call.Arguments,
                McpJsonUtilities.DefaultOptions) as JsonObject ?? new JsonObject();
            toolName = call.Name;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            toolName = string.Empty;
            arguments = new JsonObject();
            return false;
        }
    }

    private static string MessageIdKey(JsonRpcMessageWithId message) =>
        JsonSerializer.Serialize(message.Id, McpJsonUtilities.DefaultOptions);

    private sealed class PendingCompatibilityHandle : IDisposable
    {
        private readonly CancellationTokenRegistration _cancellation;
        private readonly Timer _expiry;
        private int _disposed;

        internal PendingCompatibilityHandle(
            string traceId,
            CancellationToken cancellationToken,
            Action rollback)
        {
            TraceId = traceId;
            _cancellation = cancellationToken.Register(rollback);
            _expiry = new Timer(
                static state => ((Action)state!).Invoke(),
                rollback,
                TimeSpan.FromMinutes(2),
                Timeout.InfiniteTimeSpan);
        }

        internal string TraceId { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _cancellation.Dispose();
            _expiry.Dispose();
        }
    }
}
