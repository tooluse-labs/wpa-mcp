using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpaMcp.Core;

/// <summary>
/// Keeps successful structured tool results valid against their advertised output schema.
/// The MCP SDK omits null POCO properties by default even when its generated schema marks
/// a nullable constructor property as required. Only those required nullable properties are
/// restored here; non-structured tool text remains on the SDK's legacy serializer path.
/// </summary>
internal sealed class StructuredOutputContractFilters
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, string> _pendingToolCalls = new();
    private readonly Lazy<IReadOnlyDictionary<string, JsonElement>> _outputSchemas;

    public StructuredOutputContractFilters(IReadOnlyList<Tool>? tools = null)
    {
        tools ??= ToolListPayload.MeasureCurrentTools();
        var activeTools = tools.ToArray();
        _outputSchemas = new Lazy<IReadOnlyDictionary<string, JsonElement>>(
            () => BuildStructuredOutputSchemas(activeTools),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal int PendingCallCount => _pendingToolCalls.Count;

    public McpMessageFilter CreateIncomingFilter()
        => next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcRequest request
                && string.Equals(request.Method, RequestMethods.ToolsCall, StringComparison.Ordinal)
                && TryGetToolName(request.Params, out var toolName))
            {
                // Program hosts one stateful stdio session, so the JSON-RPC request id is the
                // session-local correlation key. A future multi-session transport must scope
                // this filter per session or add a transport/session identity to the key.
                var key = MessageIdKey(request);
                _pendingToolCalls[key] = toolName;
                try
                {
                    await next(context, cancellationToken);
                }
                catch
                {
                    // Outgoing filters run after this incoming pipeline returns, so a normal
                    // return must retain correlation. Exceptions have no outgoing response.
                    _pendingToolCalls.TryRemove(key, out _);
                    throw;
                }
                return;
            }

            await next(context, cancellationToken);
        };

    public McpMessageFilter CreateOutgoingFilter()
        => next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcMessageWithId responseWithId
                && _pendingToolCalls.TryRemove(MessageIdKey(responseWithId), out var toolName)
                && context.JsonRpcMessage is JsonRpcResponse response
                && _outputSchemas.Value.TryGetValue(toolName, out var schema))
            {
                NormalizeStructuredSuccess(response, schema);
            }

            await next(context, cancellationToken);
        };

    internal static IReadOnlyList<string> CompleteRequiredNullableProperties(
        JsonNode? value,
        JsonElement schema)
    {
        var unresolved = new List<string>();
        var schemaNode = JsonNode.Parse(schema.GetRawText()) as JsonObject;
        if (schemaNode is null)
            return ["$: output schema root is not an object"];
        var referenceViolations = ToolOutputSchemaReferences.Validate(schemaNode);
        if (referenceViolations.Count != 0)
        {
            return referenceViolations
                .Select(violation => $"{violation.Path}: {violation.Code}")
                .ToArray();
        }
        CompleteRequiredNullableProperties(value, schemaNode, schemaNode, "$", unresolved);
        return unresolved;
    }

    private static void NormalizeStructuredSuccess(JsonRpcResponse response, JsonElement schema)
    {
        var result = JsonSerializer.SerializeToNode(response.Result, McpJsonUtilities.DefaultOptions)
            as JsonObject;
        if (result is null
            || result["isError"]?.GetValue<bool>() == true
            || result["structuredContent"] is not JsonObject structured)
        {
            return;
        }

        var textItem = FindMatchingJsonTextItem(result, structured);
        if (textItem is null)
            return;

        var unresolved = CompleteRequiredNullableProperties(structured, schema);
        if (unresolved.Count != 0)
        {
            // Nullable omission is the only SDK mismatch this filter is authorized to repair.
            // Leave any different defect observable so the conformance gate fails rather than
            // fabricating a non-null value that the analyzer did not produce.
            return;
        }

        textItem["text"] = structured.ToJsonString(CompactJson);

        response.Result = result;
    }

    private static JsonObject? FindMatchingJsonTextItem(
        JsonObject result,
        JsonObject structured)
    {
        if (result["content"] is not JsonArray content)
            return null;

        JsonObject? match = null;
        foreach (var item in content.OfType<JsonObject>())
        {
            if (item["type"]?.GetValue<string>() != "text"
                || item["text"]?.GetValue<string>() is not { } text)
            {
                continue;
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                continue;
            }

            if (!JsonNode.DeepEquals(parsed, structured))
                continue;
            if (match is not null)
                return null;
            match = item;
        }

        return match;
    }

    private static void CompleteRequiredNullableProperties(
        JsonNode? value,
        JsonObject schema,
        JsonObject rootSchema,
        string path,
        List<string> unresolved)
    {
        if (!ToolOutputSchemaReferences.TryResolve(
                rootSchema,
                schema,
                out schema,
                out var referenceError))
        {
            unresolved.Add(path + ": " + referenceError);
            return;
        }

        if (schema["anyOf"] is JsonArray alternatives)
        {
            if (value is null && alternatives.OfType<JsonObject>().Any(candidate =>
                    SchemaAllowsType(candidate, rootSchema, "null")))
            {
                return;
            }
            var nonNull = alternatives.OfType<JsonObject>()
                .Where(candidate => !SchemaAllowsType(candidate, rootSchema, "null"))
                .ToArray();
            if (nonNull.Length != 1)
            {
                unresolved.Add(path + ": nullable schema is not closed");
                return;
            }
            CompleteRequiredNullableProperties(
                value,
                nonNull[0],
                rootSchema,
                path,
                unresolved);
            return;
        }

        if (value is JsonObject obj && SchemaAllowsType(schema, rootSchema, "object"))
        {
            if (schema["properties"] is JsonObject properties)
            {
                if (schema["required"] is JsonArray required)
                {
                    foreach (var requiredNameNode in required)
                    {
                        var requiredName = requiredNameNode?.GetValue<string>()
                            ?? throw new JsonException("Output schema required entry was not a string.");
                        if (obj.ContainsKey(requiredName))
                            continue;

                        if (properties[requiredName] is not JsonObject propertySchema ||
                            !SchemaAllowsType(propertySchema, rootSchema, "null"))
                        {
                            unresolved.Add($"{path}.{requiredName}");
                            continue;
                        }

                        obj[requiredName] = null;
                    }
                }

                foreach (var property in obj)
                {
                    if (properties[property.Key] is JsonObject propertySchema)
                    {
                        CompleteRequiredNullableProperties(
                            property.Value,
                            propertySchema,
                            rootSchema,
                            $"{path}.{property.Key}",
                            unresolved);
                    }
                    else if (schema["additionalProperties"] is JsonObject additionalSchema)
                    {
                        CompleteRequiredNullableProperties(
                            property.Value,
                            additionalSchema,
                            rootSchema,
                            $"{path}.{property.Key}",
                        unresolved);
                    }
                }
            }
            else if (schema["additionalProperties"] is JsonObject additionalSchema)
            {
                foreach (var property in obj)
                {
                    CompleteRequiredNullableProperties(
                        property.Value,
                        additionalSchema,
                        rootSchema,
                        $"{path}.{property.Key}",
                        unresolved);
                }
            }

            return;
        }

        if (value is JsonArray array
            && SchemaAllowsType(schema, rootSchema, "array")
            && schema["items"] is JsonObject itemSchema)
        {
            for (var index = 0; index < array.Count; index++)
            {
                CompleteRequiredNullableProperties(
                    array[index],
                    itemSchema,
                    rootSchema,
                    $"{path}[{index}]",
                    unresolved);
            }
        }
    }

    private static bool SchemaAllowsType(
        JsonObject schema,
        JsonObject rootSchema,
        string expected)
    {
        if (!ToolOutputSchemaReferences.TryResolve(
                rootSchema,
                schema,
                out schema,
                out _))
        {
            return false;
        }
        if (schema["anyOf"] is JsonArray alternatives)
        {
            return alternatives.OfType<JsonObject>().Any(candidate =>
                SchemaAllowsType(candidate, rootSchema, expected));
        }
        if (schema["type"] is not JsonNode type)
            return false;

        if (type is JsonValue scalar && scalar.TryGetValue<string>(out var declaredType))
            return string.Equals(declaredType, expected, StringComparison.Ordinal);

        return type is JsonArray types && types.Any(candidate =>
            candidate is JsonValue candidateValue &&
            candidateValue.TryGetValue<string>(out var candidateType) &&
            string.Equals(candidateType, expected, StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, JsonElement> BuildStructuredOutputSchemas(
        IReadOnlyList<Tool> tools)
        => tools
            .Where(tool => tool.OutputSchema is not null)
            .ToDictionary(
                tool => tool.Name,
                tool => JsonSerializer.SerializeToElement(
                    tool.OutputSchema,
                    McpJsonUtilities.DefaultOptions).Clone(),
                StringComparer.Ordinal);

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

    private static bool TryGetToolName(object? value, out string toolName)
    {
        try
        {
            var call = ConvertValue<CallToolRequestParams>(value);
            if (!string.IsNullOrEmpty(call?.Name))
            {
                toolName = call.Name;
                return true;
            }
        }
        catch (JsonException)
        {
            // The SDK owns malformed request validation and its JSON-RPC error contract.
        }
        catch (NotSupportedException)
        {
            // Likewise, do not replace the SDK error path for an unsupported params shape.
        }

        toolName = string.Empty;
        return false;
    }

    private static string MessageIdKey(JsonRpcMessageWithId message)
        => JsonSerializer.Serialize(message.Id, McpJsonUtilities.DefaultOptions);

}
