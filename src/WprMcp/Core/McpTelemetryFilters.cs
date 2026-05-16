using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WprMcp.Core;

internal sealed class McpTelemetryFilters(ToolTelemetry telemetry)
{
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls = new();

    public McpMessageFilter CreateIncomingFilter()
        => next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is not JsonRpcRequest request)
            {
                await next(context, cancellationToken);
                return;
            }

            if (string.Equals(request.Method, RequestMethods.PromptsGet, StringComparison.Ordinal)
                || string.Equals(request.Method, RequestMethods.PromptsList, StringComparison.Ordinal))
            {
                telemetry.RecordPromptInvocation(request.Method);
            }

            if (!string.Equals(request.Method, RequestMethods.ToolsCall, StringComparison.Ordinal))
            {
                await next(context, cancellationToken);
                return;
            }

            var call = ConvertValue<CallToolRequestParams>(request.Params);
            var pending = new PendingToolCall(
                Id: MessageIdKey(request),
                ToolName: call?.Name ?? "<unknown>",
                Arguments: call?.Arguments,
                Started: Stopwatch.GetTimestamp());

            _pendingToolCalls[pending.Id] = pending;

            using var cacheScope = TraceCacheCallContext.Begin();
            try
            {
                await next(context, cancellationToken);
            }
            catch
            {
                if (_pendingToolCalls.TryRemove(pending.Id, out _))
                {
                    telemetry.RecordToolCall(
                        pending.ToolName,
                        pending.Arguments,
                        Stopwatch.GetElapsedTime(pending.Started),
                        responseBytes: null,
                        error: true,
                        TraceCacheCallContext.Snapshot);
                }

                throw;
            }
        };

    public McpMessageFilter CreateOutgoingFilter()
        => next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcMessageWithId messageWithId
                && _pendingToolCalls.TryRemove(MessageIdKey(messageWithId), out var pending))
            {
                telemetry.RecordToolCall(
                    pending.ToolName,
                    pending.Arguments,
                    Stopwatch.GetElapsedTime(pending.Started),
                    JsonSerializer.SerializeToUtf8Bytes(context.JsonRpcMessage, McpJsonUtilities.DefaultOptions).Length,
                    IsErrorResponse(context.JsonRpcMessage),
                    TraceCacheCallContext.Snapshot);
            }

            await next(context, cancellationToken);
        };

    private static bool IsErrorResponse(JsonRpcMessage message)
    {
        if (message is JsonRpcError)
            return true;

        if (message is JsonRpcResponse response)
        {
            var result = ConvertValue<CallToolResult>(response.Result);
            return result?.IsError == true;
        }

        return false;
    }

    private static T? ConvertValue<T>(object? value)
    {
        if (value is null)
            return default;

        if (value is T typed)
            return typed;

        if (value is JsonElement element)
            return element.Deserialize<T>(McpJsonUtilities.DefaultOptions);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, McpJsonUtilities.DefaultOptions);
        return JsonSerializer.Deserialize<T>(bytes, McpJsonUtilities.DefaultOptions);
    }

    private static string MessageIdKey(JsonRpcMessageWithId message)
        => JsonSerializer.Serialize(message.Id, McpJsonUtilities.DefaultOptions);

    private sealed record PendingToolCall(
        string Id,
        string ToolName,
        object? Arguments,
        long Started);
}
