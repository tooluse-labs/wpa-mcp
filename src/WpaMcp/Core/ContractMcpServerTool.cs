using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal sealed class ContractMcpServerTool : DelegatingMcpServerTool
{
    private readonly ActiveToolDefinition _tool;
    private readonly ReviewedToolOutcomeAdapterRegistry _adapters;
    private readonly CapabilityEvaluatorRegistry _capabilityEvaluators;
    private readonly ToolResponseFrameFitter _fitter;
    private readonly IToolArgumentRewriter _argumentRewriter;
    private readonly Type _dataType;
    private readonly JsonObject _outputSchema;
    private readonly Tool _protocolTool;

    internal ContractMcpServerTool(
        McpServerTool innerTool,
        ActiveToolDefinition tool,
        ReviewedToolOutcomeAdapterRegistry adapters,
        CapabilityEvaluatorRegistry capabilityEvaluators,
        ToolResponseFrameFitter fitter,
        IToolArgumentRewriter argumentRewriter)
        : base(innerTool)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _capabilityEvaluators = capabilityEvaluators ?? throw new ArgumentNullException(nameof(capabilityEvaluators));
        _fitter = fitter ?? throw new ArgumentNullException(nameof(fitter));
        _argumentRewriter = argumentRewriter ?? throw new ArgumentNullException(nameof(argumentRewriter));
        _dataType = tool.OutputDataType;
        _outputSchema = tool.OutputContract.ParseSchema();
        _protocolTool = JsonSerializer.Deserialize<Tool>(
                JsonSerializer.Serialize(innerTool.ProtocolTool, McpJsonUtilities.DefaultOptions),
                McpJsonUtilities.DefaultOptions)
            ?? throw new InvalidOperationException(
                $"The protocol descriptor for '{tool.ToolName}' could not be cloned.");
        var meta = _protocolTool.Meta?.DeepClone() as JsonObject ?? new JsonObject();
        meta[ToolOutputContract.MetadataKey] = tool.OutputContract.ToDiscoveryMetadata();
        _protocolTool.Meta = meta;
        _protocolTool.OutputSchema = null;
    }

    public override Tool ProtocolTool => _protocolTool;

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ToolRequestIdPolicy.Validate(request.JsonRpcRequest.Id);
        cancellationToken.ThrowIfCancellationRequested();

        if (ToolContractMessageFilters.TryGetPreDispatchFailure(request, out var preDispatch))
        {
            if (!string.Equals(preDispatch.Invocation.ToolName, _tool.ToolName, StringComparison.Ordinal))
                throw new InvalidOperationException("The pre-dispatch failure was correlated to a different tool.");
            return FinalizeFailure(
                request.JsonRpcRequest.Id,
                preDispatch.Error,
                preDispatch.Invocation.Arguments);
        }

        ReviewedToolInvocationPlan? plan = null;
        try
        {
            var publicArguments = SnapshotArguments(request.Params?.Arguments) ??
                new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            ToolInputSchemaValidator.Validate(ProtocolTool.InputSchema, publicArguments);
            plan = _adapters.Plan(_tool, publicArguments);
            var aliasRewrite = _argumentRewriter.Rewrite(
                _tool.ToolName,
                ToJsonObject(plan.InnerArguments));
            var boundArguments = ToolExactIntegerInputOverlay.RewriteArguments(
                _tool.Method,
                ToJsonElements(aliasRewrite.Arguments));
            var rewritten = new CallToolRequestParams
            {
                Name = request.Params!.Name,
                Arguments = boundArguments.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Clone(),
                    StringComparer.Ordinal),
                Meta = request.Params.Meta,
            };
            var innerRequest = new RequestContext<CallToolRequestParams>(
                request.Server,
                request.JsonRpcRequest,
                rewritten);

            CallToolResult raw;
            using var failureCapture = ToolFailureCaptureContext.Begin();
            using (ToolOverfetchExecutionContext.Begin())
                raw = await base.InvokeAsync(innerRequest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (raw.IsError == true || !raw.StructuredContent.HasValue)
            {
                return FinalizeFailure(
                    request.JsonRpcRequest.Id,
                    failureCapture.Captured is { } captured
                        ? MapException(captured)
                        : StableError("analysis_failed"),
                    plan.PublicArguments);
            }

            var rawNode = JsonNode.Parse(raw.StructuredContent.Value.GetRawText())
                ?? throw new InvalidOperationException("The typed tool returned JSON null.");
            var fitted = ProjectSuccess(
                request.JsonRpcRequest.Id,
                rawNode,
                plan,
                enforceResponseBudget: true);
            cancellationToken.ThrowIfCancellationRequested();
            return fitted.Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ToolRequestIdTooLargeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FinalizeFailure(
                request.JsonRpcRequest.Id,
                MapException(exception),
                FailureArguments(plan, request.Params?.Arguments));
        }
    }

    internal FittedToolResponse MeasureSuccessfulDataForPreflight(
        RequestId requestId,
        object data,
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (!string.Equals(_tool.ToolName, "get_tool_contract", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Only get_tool_contract has a startup success-frame preflight.");
        }
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!_dataType.IsInstanceOfType(data))
        {
            throw new ArgumentException(
                $"Expected preflight data of type '{_dataType.FullName}'.",
                nameof(data));
        }

        var plan = _adapters.Plan(_tool, arguments);
        var rawNode = JsonSerializer.SerializeToNode(data, McpJsonUtilities.DefaultOptions)
            ?? throw new InvalidOperationException("The preflight tool data serialized to JSON null.");
        return ProjectSuccess(
            requestId,
            rawNode,
            plan,
            enforceResponseBudget: false);
    }

    private FittedToolResponse ProjectSuccess(
        RequestId requestId,
        JsonNode rawNode,
        ReviewedToolInvocationPlan plan,
        bool enforceResponseBudget)
    {
        var reviewed = plan.Adapt(rawNode);
        var data = reviewed.Domain.Deserialize(_dataType, McpJsonUtilities.DefaultOptions)
            ?? throw new InvalidOperationException("The typed tool result could not be materialized.");
        CompositeResultContractValidator.Validate(data);
        var envelope = ToolEnvelopeProjection.Success(
            _tool,
            data,
            reviewed,
            plan.PublicArguments,
            EvaluateCapabilities(reviewed, failed: false));
        var projected = ToolWireJson.ProjectEnvelope(envelope, _dataType);
        return enforceResponseBudget
            ? _fitter.Fit(
                requestId,
                projected,
                _outputSchema,
                _tool,
                plan.PublicArguments)
            : _fitter.MeasureUnboundedSuccess(
                requestId,
                projected,
                _outputSchema,
                _tool);
    }

    private static IReadOnlyDictionary<string, JsonElement>? FailureArguments(
        ReviewedToolInvocationPlan? plan,
        IDictionary<string, JsonElement>? requestArguments) =>
        plan?.PublicArguments ?? SnapshotArguments(requestArguments);

    private static IReadOnlyDictionary<string, JsonElement>? SnapshotArguments(
        IDictionary<string, JsonElement>? requestArguments) =>
        requestArguments?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var result = new JsonObject();
        foreach (var argument in arguments)
            result[argument.Key] = JsonNode.Parse(argument.Value.GetRawText());
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> ToJsonElements(JsonObject arguments) =>
        arguments.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value, McpJsonUtilities.DefaultOptions),
            StringComparer.Ordinal);

    private CallToolResult FinalizeFailure(
        RequestId requestId,
        ToolError error,
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var envelope = ToolEnvelopeProjection.Failure(
            _tool,
            error,
            arguments,
            EvaluateCapabilities(reviewed: null, failed: true));
        var projected = ToolWireJson.ProjectEnvelope(envelope, _dataType);
        return _fitter.Fit(requestId, projected, _outputSchema, _tool, arguments).Result;
    }

    private IReadOnlyList<CapabilityRuntimeAssessment> EvaluateCapabilities(
        ReviewedToolResult? reviewed,
        bool failed)
    {
        TraceFactsSnapshot? facts = TraceQueryExecutionContext.TryGetReadyFacts(out var ready)
            ? ready
            : null;
        return _tool.Capabilities.Select(capability => _capabilityEvaluators.EvaluateTool(
            _tool,
            capability,
            reviewed?.Domain as JsonObject,
            reviewed?.Outcome,
            facts,
            failed)).ToArray();
    }

    internal static ToolError MapException(Exception exception) => exception switch
    {
        TraceReferenceException trace => StableError(trace.Code),
        TraceAccessException trace => ProjectTraceAccess(trace),
        TraceFactsSnapshotException => StableError("budget_exceeded"),
        ReviewedToolTerminalException terminal => StableError(terminal.Code),
        SymbolToolContractException symbol => StableError(SymbolToolErrorProjection.PublicCode(symbol.Code)),
        SymbolContextException symbol => StableError(SymbolContextPublicErrorProjection.Project(symbol).Code),
        CapabilityCursorException cursor => StableError(cursor.Kind switch
        {
            CapabilityCursorFailureKind.Invalid => "invalid_cursor",
            CapabilityCursorFailureKind.RegistryCapacity => "budget_exceeded",
            CapabilityCursorFailureKind.EntropyFailure => "analysis_failed",
            _ => "analysis_failed",
        }),
        QueryResultCursorException cursor => StableError(cursor.Kind switch
        {
            QueryResultCursorFailureKind.Invalid => "invalid_cursor",
            QueryResultCursorFailureKind.RegistryCapacity => "budget_exceeded",
            QueryResultCursorFailureKind.EntropyFailure => "analysis_failed",
            _ => "analysis_failed",
        }),
        ArgumentException => StableError("invalid_argument"),
        OperationCanceledException => StableError("cancelled"),
        _ => StableError("analysis_failed"),
    };

    private static ToolError ProjectTraceAccess(TraceAccessException exception)
    {
        var code = TraceAccessErrorProjection.PublicCode(exception.Code);
        // Policy denials carry an actionable, reviewed detail in the exception
        // message; surface it so users can see which rule rejected the path.
        return code == "trace_access_denied"
            ? new ToolError(code, exception.Message, retryable: false)
            : StableError(code);
    }

    internal static ToolError StableError(string code) => code switch
    {
        "invalid_argument" => new(code, "One or more tool arguments violate the advertised input contract.", false),
        "process_instance_not_found" => new(code, "No process instance matched the requested scope.", false),
        "process_start_required" => new(code, "The PID matched multiple lifetimes; retry with a candidate processStartUs.", false),
        "ambiguous_process_instance" => new(code, "The requested process instance could not be resolved safely.", false),
        "thread_instance_not_found" => new(code, "No thread instance matched the requested scope.", false),
        "ambiguous_thread_instance" => new(code, "The requested thread instance could not be resolved safely.", false),
        "trace_not_loaded" => new(code, "The trace reference is unavailable; load the trace and retry with its TraceId.", true),
        "trace_access_denied" => new(code, "Trace access was denied by the configured policy.", false),
        "trace_conversion_failed" => new(code, "The trace could not be converted into an analyzable generation.", true),
        "symbol_context_expired" => new(code, "The symbol context is unavailable; prepare a new context for this trace and policy.", true),
        "symbol_policy_denied" => new(code, "The requested symbol operation is denied by policy.", false),
        "symbol_resolution_unavailable" => new(code, "Context-bound frame-name resolution is unavailable in this server build; rerun with resolveSymbols=false or use a build that advertises a resolver.", false),
        "invalid_cursor" => new(code, "The continuation cursor is invalid or no longer bound to this query.", false),
        "cancelled" => new(code, "The operation was cancelled before a result was finalized.", true),
        "budget_exceeded" => new(code, "The operation exceeded its configured execution budget.", true),
        "response_too_large" => new(code, "The response cannot fit the configured JSON-RPC frame budget.", true),
        _ => new("analysis_failed", "The analysis could not produce a trustworthy contract result.", false),
    };
}

internal static class TraceAccessErrorProjection
{
    internal static IReadOnlySet<string> KnownInternalCodes { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "trace_access_denied",
            "trace_too_large",
            "trace_conversion_failed",
            "trace_materialization_failed",
            "trace_source_changed",
            "trace_artifact_expired",
            "trace_artifact_changed",
            "trace_artifact_boundary_violation",
            "trace_artifact_invalid",
            "trace_artifact_reparse_denied",
        };

    internal static string PublicCode(string code) => code switch
    {
        "trace_access_denied" => "trace_access_denied",
        "trace_too_large" => "budget_exceeded",
        "trace_conversion_failed" or "trace_materialization_failed" => "trace_conversion_failed",
        "trace_source_changed" or "trace_artifact_expired" or "trace_artifact_changed" or
        "trace_artifact_boundary_violation" or "trace_artifact_invalid" or
        "trace_artifact_reparse_denied" => "analysis_failed",
        _ => "analysis_failed",
    };

    internal static void RequireReviewed(string code)
    {
        if (!KnownInternalCodes.Contains(code))
            throw new InvalidOperationException($"Unreviewed TraceAccessException code '{code}'.");
    }
}

internal static class SymbolToolErrorProjection
{
    internal static IReadOnlySet<string> KnownPublicCodes { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "invalid_argument",
            "symbol_context_expired",
            "budget_exceeded",
            "symbol_policy_denied",
            "symbol_resolution_unavailable",
            "analysis_failed",
        };

    internal static string PublicCode(string code) =>
        KnownPublicCodes.Contains(code) ? code : "analysis_failed";
}
