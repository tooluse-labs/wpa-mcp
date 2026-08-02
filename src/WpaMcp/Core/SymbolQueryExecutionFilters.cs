using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpaMcp.Core;

/// <summary>
/// Pre-binding enforcement for the reviewed symbolContextId schema overlay. It
/// validates and leases only that synthetic argument, strips it before the SDK's
/// CLR binder, and leaves every unrecognized argument for normal SDK rejection.
/// </summary>
internal sealed class SymbolQueryExecutionFilters
{
    private readonly HashSet<string> _stackTools;
    private readonly SymbolContextRegistry _contexts;
    private readonly SymbolPrincipal _principal;
    private readonly Func<string?> _currentTraceGenerationIdentity;

    internal SymbolQueryExecutionFilters(
        IReadOnlyList<Tool> activeTools,
        SymbolContextRegistry contexts,
        StdioSessionPrincipal principal,
        Func<string?> currentTraceGenerationIdentity)
    {
        ArgumentNullException.ThrowIfNull(activeTools);
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        ArgumentNullException.ThrowIfNull(principal);
        _principal = new SymbolPrincipal(principal.RegistryKey);
        _currentTraceGenerationIdentity = currentTraceGenerationIdentity
            ?? throw new ArgumentNullException(nameof(currentTraceGenerationIdentity));
        _stackTools = activeTools
            .Where(static tool =>
                HasInputProperty(tool, SymbolToolSchemaOverlay.SelectorParameter) &&
                HasInputProperty(tool, SymbolToolSchemaOverlay.PropertyName))
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal IReadOnlySet<string> StackTools => _stackTools;

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
                !TryReadCall(request.Params, out var call) ||
                !_stackTools.Contains(call.Name))
            {
                await next(context, cancellationToken);
                return;
            }

            var arguments = call.Arguments is null
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(call.Arguments, StringComparer.Ordinal);
            var hasContextArgument = arguments.TryGetValue(
                SymbolToolSchemaOverlay.PropertyName,
                out var contextElement);
            var hasBooleanResolve = arguments.TryGetValue(
                    SymbolToolSchemaOverlay.SelectorParameter,
                    out var resolveElement) &&
                resolveElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var resolveSymbols = hasBooleanResolve && resolveElement.GetBoolean();

            // A malformed resolveSymbols value belongs to the SDK binder. Strip only
            // our reviewed synthetic argument so unknown/invalid ordinary inputs retain
            // their normal validation semantics.
            if (!hasBooleanResolve && arguments.ContainsKey(SymbolToolSchemaOverlay.SelectorParameter))
            {
                StripSyntheticArgument(request, call, arguments);
                await next(context, cancellationToken);
                return;
            }

            string? symbolContextId = null;
            if (hasContextArgument && contextElement.ValueKind != JsonValueKind.Null)
            {
                if (contextElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(symbolContextId = contextElement.GetString()))
                {
                    throw InvalidArgument(
                        "symbolContextId must be null or a non-empty immutable context identifier.");
                }
            }

            if (resolveSymbols && symbolContextId is null)
            {
                throw InvalidArgument(
                    "resolveSymbols=true requires symbolContextId from prepare_symbols for this trace generation.");
            }
            if (!resolveSymbols && symbolContextId is not null)
            {
                throw InvalidArgument(
                    "symbolContextId is valid only when resolveSymbols=true; this build never attaches an unused context identifier to an unsymbolized result.");
            }

            StripSyntheticArgument(request, call, arguments);
            if (symbolContextId is null)
            {
                await next(context, cancellationToken);
                return;
            }

            var generationIdentity = _currentTraceGenerationIdentity();
            if (string.IsNullOrWhiteSpace(generationIdentity))
            {
                throw new SymbolToolContractException(
                    "analysis_failed",
                    "symbol_trace_binding_unavailable",
                    "The symbol context could not be bound to the active trace generation.");
            }

            SymbolContextLease lease;
            try
            {
                lease = await _contexts.AcquireAsync(
                    _principal,
                    symbolContextId,
                    generationIdentity,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SymbolContextException exception)
            {
                var projected = SymbolContextPublicErrorProjection.Project(exception);
                throw new SymbolToolContractException(
                    projected.Code,
                    projected.DetailCode,
                    projected.Message,
                    exception);
            }

            await using (lease.ConfigureAwait(false))
            {
                using var symbolScope = SymbolQueryExecutionContext.Begin(symbolContextId);
                if (resolveSymbols)
                {
                    // No context-bound TraceEvent adapter exists yet. Refuse explicitly;
                    // invoking the legacy analyzer here would reopen hidden fallback I/O
                    // or misrepresent an unsymbolized result as a resolved query.
                    throw new SymbolToolContractException(
                        "symbol_resolution_unavailable",
                        "context_bound_frame_resolution_unavailable",
                        "This build can validate immutable symbol readiness, but context-bound frame-name resolution is not available.",
                        symbolContextId: symbolContextId);
                }

                await next(context, cancellationToken);
            }
        };

    private static SymbolToolContractException InvalidArgument(string message) =>
        new("invalid_argument", "symbol_context_argument_invalid", message);

    private static void StripSyntheticArgument(
        JsonRpcRequest request,
        CallToolRequestParams call,
        Dictionary<string, JsonElement> arguments)
    {
        arguments.Remove(SymbolToolSchemaOverlay.PropertyName);
        call.Arguments = arguments;
        request.Params = JsonSerializer.SerializeToNode(
            call,
            McpJsonUtilities.DefaultOptions);
    }

    private static bool HasInputProperty(Tool tool, string propertyName) =>
        tool.InputSchema.ValueKind == JsonValueKind.Object &&
        tool.InputSchema.TryGetProperty("properties", out var properties) &&
        properties.ValueKind == JsonValueKind.Object &&
        properties.TryGetProperty(propertyName, out _);

    private static bool TryReadCall(object? value, out CallToolRequestParams call)
    {
        try
        {
            call = value switch
            {
                CallToolRequestParams typed => typed,
                JsonElement element => element.Deserialize<CallToolRequestParams>(
                    McpJsonUtilities.DefaultOptions)!,
                _ => JsonSerializer.Deserialize<CallToolRequestParams>(
                    JsonSerializer.SerializeToUtf8Bytes(value, McpJsonUtilities.DefaultOptions),
                    McpJsonUtilities.DefaultOptions)!,
            };
            return !string.IsNullOrWhiteSpace(call?.Name);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {
            call = null!;
            return false;
        }
    }
}

internal static class SymbolQueryExecutionContext
{
    private static readonly AsyncLocal<string?> CurrentId = new();

    internal static string? CurrentSymbolContextId => CurrentId.Value;

    internal static IDisposable Begin(string symbolContextId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolContextId);
        var prior = CurrentId.Value;
        CurrentId.Value = symbolContextId;
        return new Scope(prior);
    }

    private sealed class Scope(string? prior) : IDisposable
    {
        private string? _prior = prior;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                CurrentId.Value = Interlocked.Exchange(ref _prior, null);
        }
    }
}
