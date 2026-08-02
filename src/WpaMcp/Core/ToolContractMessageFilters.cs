using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal sealed record ToolInvocationSnapshot(
    string ToolName,
    IReadOnlyDictionary<string, JsonElement> Arguments);

internal sealed record ToolPreDispatchFailure(
    ToolError Error,
    ToolInvocationSnapshot Invocation);

/// <summary>
/// Earliest decoded-message boundary for tool calls. It validates correlation
/// identifiers before any trace/symbol resolver runs and converts reviewed
/// pre-dispatch failures into the same typed wrapper path as analyzer failures.
/// </summary>
internal sealed class ToolContractMessageFilters
{
    internal const string InvocationItemKey = "wpa_mcp.contract.invocation.v2";
    internal const string FailureItemKey = "wpa_mcp.contract.pre_dispatch_failure.v2";
    private readonly IReadOnlyDictionary<string, ActiveToolDefinition> _tools;
    private readonly IReadOnlyDictionary<string, JsonElement> _inputSchemas;
    private readonly ToolExecutionBudgetOptions _budgets;

    internal ToolContractMessageFilters(
        IReadOnlyList<ActiveToolDefinition> tools,
        IReadOnlyList<McpServerTool> serverTools,
        ToolExecutionBudgetOptions budgets)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(serverTools);
        _budgets = budgets ?? throw new ArgumentNullException(nameof(budgets));
        _tools = tools.ToDictionary(tool => tool.ToolName, StringComparer.Ordinal);
        _inputSchemas = serverTools.ToDictionary(
            tool => tool.ProtocolTool.Name,
            tool => tool.ProtocolTool.InputSchema.Clone(),
            StringComparer.Ordinal);
        if (!_tools.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(_inputSchemas.Keys))
            throw new InvalidOperationException("Tool input schemas do not close over the active catalog.");
    }

    public McpMessageFilter CreateIncomingFilter()
        => next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is not JsonRpcRequest request)
            {
                await next(context, cancellationToken);
                return;
            }

            ToolRequestIdPolicy.Validate(request.Id);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(request.Method, RequestMethods.ToolsCall, StringComparison.Ordinal) ||
                !TryReadCall(request.Params, out var invocation))
            {
                await next(context, cancellationToken);
                return;
            }

            context.Items[InvocationItemKey] = invocation;
            if (_tools.TryGetValue(invocation.ToolName, out var tool) &&
                TryValidateArguments(tool, invocation.Arguments, out var argumentError))
            {
                context.Items[FailureItemKey] = new ToolPreDispatchFailure(
                    argumentError,
                    invocation);
                await next(context, cancellationToken);
                return;
            }
            try
            {
                await next(context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsReviewedPreDispatchFailure(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Items[FailureItemKey] = new ToolPreDispatchFailure(
                    ContractMcpServerTool.MapException(exception),
                    invocation);

                // Trace/symbol filters recognize the item and bypass their resolvers
                // on this second pass. The reviewed tool wrapper then emits the only
                // terminal, schema-valid failure response.
                await next(context, cancellationToken);
            }
        };

    internal static bool HasPreDispatchFailure(MessageContext context) =>
        context.Items.TryGetValue(FailureItemKey, out var value) && value is ToolPreDispatchFailure;

    internal static bool TryGetPreDispatchFailure(
        MessageContext context,
        out ToolPreDispatchFailure failure)
    {
        if (context.Items.TryGetValue(FailureItemKey, out var value) &&
            value is ToolPreDispatchFailure typed)
        {
            failure = typed;
            return true;
        }
        failure = null!;
        return false;
    }

    internal static bool TryGetInvocation(
        MessageContext context,
        out ToolInvocationSnapshot invocation)
    {
        if (context.Items.TryGetValue(InvocationItemKey, out var value) &&
            value is ToolInvocationSnapshot typed)
        {
            invocation = typed;
            return true;
        }
        invocation = null!;
        return false;
    }

    private static bool IsReviewedPreDispatchFailure(Exception exception) =>
        exception is TraceReferenceException or TraceAccessException or
            TraceFactsSnapshotException or
            CapabilityCursorException or
            SymbolToolContractException or SymbolContextException;

    private bool TryValidateArguments(
        ActiveToolDefinition tool,
        IReadOnlyDictionary<string, JsonElement> arguments,
        out ToolError error)
    {
        try
        {
            var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(
                arguments,
                McpJsonUtilities.DefaultOptions).Length;
            if (serializedBytes > _budgets.MaxToolArgumentBytes)
                throw new ArgumentException("The parsed tool argument object exceeds the configured byte limit.");
            foreach (var argument in arguments)
                ValidateJsonValue(argument.Value, "$arguments." + argument.Key);
            ToolInputSchemaValidator.Validate(_inputSchemas[tool.ToolName], arguments);
            ValidateDirectStringBindings(tool, arguments);

            // This is validation only. The wrapper performs the identical rewrite
            // immediately before SDK binding after trace/symbol filters complete.
            _ = ToolExactIntegerInputOverlay.RewriteArguments(tool.Method, arguments);
            error = null!;
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or OverflowException)
        {
            error = ContractMcpServerTool.StableError("invalid_argument");
            return true;
        }
    }

    private static void ValidateDirectStringBindings(
        ActiveToolDefinition tool,
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        foreach (var parameter in tool.Method.GetParameters())
        {
            if (parameter.ParameterType != typeof(string) ||
                parameter.Name is not { } name ||
                !arguments.TryGetValue(name, out var value))
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException(
                    $"String argument '$arguments.{name}' must be a JSON string.");
            }
        }
    }

    private void ValidateJsonValue(JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (value.GetString()!.Length > _budgets.MaxStringChars)
                    throw new ArgumentException($"String argument '{path}' exceeds the configured character limit.");
                return;
            case JsonValueKind.Array:
                if (value.GetArrayLength() > _budgets.MaxCollectionItems)
                    throw new ArgumentException($"Collection argument '{path}' exceeds the configured item limit.");
                var index = 0;
                foreach (var item in value.EnumerateArray())
                    ValidateJsonValue(item, $"{path}[{index++}]");
                return;
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                if (properties.Length > _budgets.MaxCollectionItems)
                    throw new ArgumentException($"Object argument '{path}' exceeds the configured property limit.");
                foreach (var property in properties)
                    ValidateJsonValue(property.Value, path + "." + property.Name);
                return;
            default:
                return;
        }
    }

    private static bool TryReadCall(object? value, out ToolInvocationSnapshot invocation)
    {
        try
        {
            var call = value switch
            {
                CallToolRequestParams typed => typed,
                JsonElement element => element.Deserialize<CallToolRequestParams>(
                    McpJsonUtilities.DefaultOptions),
                _ => JsonSerializer.Deserialize<CallToolRequestParams>(
                    JsonSerializer.SerializeToUtf8Bytes(value, McpJsonUtilities.DefaultOptions),
                    McpJsonUtilities.DefaultOptions),
            };
            if (string.IsNullOrWhiteSpace(call?.Name))
            {
                invocation = null!;
                return false;
            }

            var arguments = call.Arguments is null
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                : call.Arguments.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Clone(),
                    StringComparer.Ordinal);
            invocation = new ToolInvocationSnapshot(call.Name, arguments);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            invocation = null!;
            return false;
        }
    }
}
