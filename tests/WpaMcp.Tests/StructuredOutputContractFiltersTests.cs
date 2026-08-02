using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class StructuredOutputContractFiltersTests
{
    [Fact]
    public void CompletesRequiredNullablePropertiesAcrossArraysDictionariesAndLocalRefs()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
                "properties": {
                  "rootNullable": { "type": ["integer", "null"] },
                  "referencedNullable": { "$ref": "#/$defs/nullableText" },
                  "optionalNullable": { "type": ["string", "null"], "default": null },
                "items": {
                  "type": "array",
                  "items": { "$ref": "#/$defs/item" }
                },
                "lookup": {
                  "type": "object",
                  "additionalProperties": {
                    "$ref": "#/$defs/item"
                  }
                }
              },
              "$defs": {
                "nullableText": { "type": ["string", "null"] },
                "item": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string" },
                    "detail": { "type": ["string", "null"] }
                  },
                  "required": ["name", "detail"]
                }
              },
              "required": ["rootNullable", "referencedNullable", "items", "lookup"]
            }
            """);
        var value = JsonNode.Parse("""
            {
              "items": [{ "name": "first" }],
              "lookup": { "second": { "name": "second" } }
            }
            """);

        var unresolved = StructuredOutputContractFilters
            .CompleteRequiredNullableProperties(value, schema);

        Assert.Empty(unresolved);
        Assert.True(value!.AsObject().ContainsKey("rootNullable"));
        Assert.True(value!["rootNullable"] is null);
        Assert.True(value.AsObject().ContainsKey("referencedNullable"));
        Assert.True(value["referencedNullable"] is null);
        Assert.False(value.AsObject().ContainsKey("optionalNullable"));
        Assert.True(value["items"]![0]!["detail"] is null);
        Assert.True(value["lookup"]!["second"]!["detail"] is null);
    }

    [Fact]
    public void InvalidReferenceGraph_FailsBeforeNullableCompletionMutatesTheValue()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": {
                "requiredNullable": { "type": ["string", "null"] },
                "bad": { "$ref": "#/$defs/missing" }
              },
              "required": ["requiredNullable", "bad"]
            }
            """);
        var value = new JsonObject();

        var unresolved = StructuredOutputContractFilters
            .CompleteRequiredNullableProperties(value, schema);

        Assert.Contains(unresolved, failure =>
            failure.Contains("dangling_reference", StringComparison.Ordinal));
        Assert.False(value.ContainsKey("requiredNullable"));
    }

    [Fact]
    public void NonNullableAnyOf_DoesNotReceiveAFabricatedNull()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": {
                "choice": {
                  "anyOf": [
                    { "type": "string" },
                    { "type": "integer" }
                  ]
                }
              },
              "required": ["choice"]
            }
            """);
        var value = new JsonObject();

        var unresolved = StructuredOutputContractFilters
            .CompleteRequiredNullableProperties(value, schema);

        Assert.Equal(new[] { "$.choice" }, unresolved);
        Assert.False(value.ContainsKey("choice"));
    }

    [Fact]
    public void DoesNotFabricateMissingRequiredNonNullableValues()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": {
                "requiredText": { "type": "string" },
                "requiredNullable": { "type": ["string", "null"] }
              },
              "required": ["requiredText", "requiredNullable"]
            }
            """);
        var value = new JsonObject();

        var unresolved = StructuredOutputContractFilters
            .CompleteRequiredNullableProperties(value, schema);

        Assert.Equal(new[] { "$.requiredText" }, unresolved);
        Assert.False(value.ContainsKey("requiredText"));
        Assert.True(value.ContainsKey("requiredNullable"));
        Assert.Null(value["requiredNullable"]);
    }

    [Fact]
    public async Task IncomingFilter_RetainsCorrelationUntilLaterOutgoingResponse()
    {
        var filters = new StructuredOutputContractFilters();
        var incoming = filters.CreateIncomingFilter()(
            static (_, _) => Task.CompletedTask);
        var outgoing = filters.CreateOutgoingFilter()(
            static (_, _) => Task.CompletedTask);
        var server = Mock.Of<McpServer>();
        var request = new JsonRpcRequest
        {
            Id = new RequestId(7),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(
                new CallToolRequestParams
                {
                    Name = "inspect_trace",
                    Arguments = new Dictionary<string, JsonElement>(),
                },
                McpJsonUtilities.DefaultOptions),
        };

        await incoming(
            new MessageContext(server, request),
            CancellationToken.None);

        Assert.Equal(1, filters.PendingCallCount);

        await outgoing(
            new MessageContext(server, new JsonRpcError
            {
                Id = new RequestId(7),
                Error = new JsonRpcErrorDetail
                {
                    Code = (int)McpErrorCode.InternalError,
                    Message = "synthetic failure",
                },
            }),
            CancellationToken.None);

        Assert.Equal(0, filters.PendingCallCount);
    }

    [Fact]
    public async Task IncomingFilter_CleansPendingCallWhenHandlerThrows()
    {
        var filters = new StructuredOutputContractFilters();
        var incoming = filters.CreateIncomingFilter()(
            static (_, _) => throw new InvalidOperationException("boom"));
        var request = new JsonRpcRequest
        {
            Id = new RequestId(8),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(
                new CallToolRequestParams
                {
                    Name = "inspect_trace",
                    Arguments = new Dictionary<string, JsonElement>(),
                },
                McpJsonUtilities.DefaultOptions),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => incoming(
            new MessageContext(Mock.Of<McpServer>(), request),
            CancellationToken.None));

        Assert.Equal(0, filters.PendingCallCount);
    }

    [Fact]
    public async Task IncomingFilter_LeavesMalformedParamsForSdkValidation()
    {
        var filters = new StructuredOutputContractFilters();
        var delegated = false;
        var incoming = filters.CreateIncomingFilter()((_, _) =>
        {
            delegated = true;
            return Task.CompletedTask;
        });
        var request = new JsonRpcRequest
        {
            Id = new RequestId(81),
            Method = RequestMethods.ToolsCall,
            Params = JsonValue.Create(42),
        };

        await incoming(
            new MessageContext(Mock.Of<McpServer>(), request),
            CancellationToken.None);

        Assert.True(delegated);
        Assert.Equal(0, filters.PendingCallCount);
    }

    [Fact]
    public async Task UnstructuredToolResponse_RemainsOnLegacyTextPath()
    {
        var filters = new StructuredOutputContractFilters();
        var incoming = filters.CreateIncomingFilter()(
            static (_, _) => Task.CompletedTask);
        var outgoing = filters.CreateOutgoingFilter()(
            static (_, _) => Task.CompletedTask);
        var server = Mock.Of<McpServer>();
        var request = new JsonRpcRequest
        {
            Id = new RequestId(9),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(
                new CallToolRequestParams
                {
                    Name = "load_trace",
                    Arguments = new Dictionary<string, JsonElement>(),
                },
                McpJsonUtilities.DefaultOptions),
        };
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "{\"nullableEvidence\":null}",
                },
            },
        };
        var response = new JsonRpcResponse
        {
            Id = new RequestId(9),
            Result = result,
        };

        await incoming(new MessageContext(server, request), CancellationToken.None);
        await outgoing(new MessageContext(server, response), CancellationToken.None);

        Assert.Same(result, response.Result);
        Assert.Equal(
            "{\"nullableEvidence\":null}",
            result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(0, filters.PendingCallCount);
    }
}
