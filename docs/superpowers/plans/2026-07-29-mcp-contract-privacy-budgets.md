# MCP Contract, Privacy, and Budget Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every MCP tool a versioned, schema-valid success/partial/failure contract; make batch, capability, stack, symbol, thread-quality, input, privacy, pagination, and full-wire response behavior truthful and deterministic.

**Architecture:** Keep analyzers and the existing attributed tool methods returning typed domain records so `CliRunner` and direct unit tests retain their internal contract. Replace `WithToolsFromAssembly()` with a programmatic `McpToolCatalog`: it creates the SDK typed handler with structured content enabled, wraps that `McpServerTool` in `ContractMcpServerTool`, and lets one `IToolCallExecutor` convert the SDK result into legacy text or a v2 `ToolEnvelope<TData>` with exact `IsError`. The catalog advertises no output schema in legacy mode and the generated envelope schema in v2 mode, and paginates the real `tools/list` surface. Before opening stdin, startup measures a catalog-specific `MinimumViableResponseBytes` from both the fixed failure and every active tool represented as an indivisible one-tool `tools/list` page with worst-case legal request-ID/cursor overhead; an unservable configured cap fails startup. A request filter validates arguments before trace access; after invocation the wrapper redacts both text and structured data, truncates only manifest-declared row/section arrays, measures the exact JSON-RPC response (including request ID, both content forms, and the stdio newline), and returns a fixed `response_too_large` failure when no legal envelope fits. Privacy is process-scoped startup policy, not a tool argument.

**Tech Stack:** C#; the exact TFM, SDK, `ModelContextProtocol`, and NuGet graph selected by Child 11A; xUnit; `System.Text.Json`; `Microsoft.Extensions.AI` JSON-schema utilities already supplied transitively by the MCP SDK; Windows stdio MCP.

## Global Constraints

- Preserve all typed tool method return types, names, parameters, defaults, descriptions, and risk annotations. MCP-only wrapping belongs in `McpToolCatalog`; `CliRunner` and analyzer/unit callers must not deserialize MCP envelopes.
- Child 11A's SDK spike must prove a typed `McpServerTool` created with `UseStructuredContent=true` can be delegated and its `StructuredContent` wrapped. If the selected SDK cannot do that, update the 11A ADR with the observed limitation before introducing separate adapter methods; do not silently migrate the 60 typed methods to `CallToolResult`.
- New optional thread selectors are appended after every existing parameter for source/binary compatibility. JSON object property order is descriptive only and has no semantic meaning.
- Privacy redaction happens immediately before response fitting/serialization. Response truncation never observes or emits the unredacted value.
- Public stdin and response fitting use the configured request/response caps, whose defaults and hard ceilings are 100,000 bytes. The static response floor remains 4,096 bytes, but it is not a sufficiency claim: after constructing the active catalog, startup computes `MinimumViableResponseBytes = max(worst-case fixed failure frame, every active tool's indivisible one-tool tools/list frame with maximum legal serialized request ID and cursor)` and rejects a configured cap below `max(4,096, MinimumViableResponseBytes)` before opening stdin. Startup also rejects inconsistent warning/argument caps.
- A JSON-RPC request ID is accepted only when its exact serialized UTF-8 representation is at most 128 bytes. An oversized ID terminates that stdin session with no stdout response before tool binding or trace/file/network access, so the server never promises to echo an ID that cannot fit its minimum bounded response.
- Raw exception messages never cross stdout. Stable failures originate in pre-validation/shared resolver context; unknown exceptions map to the fixed public `analysis_failed` message.
- Consume Child 3's dual-duration/warning fields and Child 4's startup provenance as typed handoffs. Per-tool outcome adapters own their wire projection; a generic classifier is only the fallback for tools without a registered semantic adapter.
- Child 5 adds only nested `ToolExecutionOptions ToolExecution` to startup options. Later children preserve that property while adding their own nested policy, registry, and runtime records; no later child may flatten contract/privacy/budget members back onto `McpServerOptions`.
- Child 5 owns process-local typed alias syntax, bounded storage, and argument rewriting before SDK binding. It exposes the rewrite result to Child 6 but does not call or anticipate Child 6 access-policy implementations; Child 6 revalidates rewritten trace and symbol values exactly like literal values before I/O.
- `Partial` and `HasMore` remain independent. TopN is pagination, not failed work; exact totals or a top+1 probe are required before `HasMore=true`.
- Tests must use the real active catalog for schemas and the real stdio server for final contract proof; reflection-only checks are supplemental.
- Do not update schema snapshots wholesale or widen budgets to make a failing result pass. Inspect and version every public contract change.

**Spec:** `docs/superpowers/specs/2026-07-29-wpa-mcp-production-remediation-design.md` at commit `7ef8ff5`.

**Prerequisites:** Child 11A is complete. Child 1 has landed `ProcessInstanceKey`, `ThreadInstanceKey`, `ThreadSelector`, and the stable process/thread resolution failures. Child 2 owns the selector semantics and filtering of the six CPU/wait tools; this plan owns their MCP envelopes, advertised schemas, error mapping, quality, privacy, and budgets.

---

## Fixed public and internal contracts

Create these exact types before migrating tools:

```csharp
namespace WprMcp.Output;

public enum ToolCompletionStatus { Succeeded, Partial, Failed }

public sealed record ToolSectionFailure(
    string Section, string Code, string Message, bool Retryable);

public sealed record ToolError(
    string Code, string Message, bool Retryable);

public sealed record ToolSectionPage(
    string Section, bool HasMore, long? TotalAvailable, long Returned);

public sealed record ToolEnvelope<T>(
    string ContractVersion,
    ToolCompletionStatus Status,
    T? Data,
    ToolError? Error,
    IReadOnlyList<ToolSectionFailure> FailedSections,
    IReadOnlyList<ToolSectionPage> Sections,
    IReadOnlyList<string> Warnings,
    bool HasMore);

public enum DomainStackCoverageState { NoEvents, NoStacks, Partial, Complete }

public sealed record DomainStackCoverage(
    string Domain,
    bool CaptureCapability,
    long EligibleEventCount,
    long StackedEventCount,
    double? CoverageRatio,
    DomainStackCoverageState State);

public enum ThreadAnalysisQualityState
{
    NoContextSwitch,
    DurationOnlyNoDomainStacks,
    StackAddressesUnresolved,
    ResolvedStacks
}

public sealed record ThreadAnalysisQuality(
    ThreadAnalysisQualityState State,
    bool HasContextSwitch,
    DomainStackCoverage DomainStacks,
    FrameResolutionStats FrameResolution);

public sealed record PdbIdentityCoverage(
    long ModuleCount,
    long CompleteNameGuidAgeCount,
    double? CoverageRatio);

public sealed record FrameResolutionStats(
    long ResolvedFrameCount,
    long UnresolvedFrameCount,
    double? ResolutionRatio,
    IReadOnlyList<UnresolvedModule> TopUnresolvedModules);

```

`ContractVersion` is always `"2.0"`. `ToolCompletionStatus` and all other public enums use checked-in string converters; completion statuses serialize as `"succeeded"`, `"partial"`, and `"failed"`. The stable public code registry (top-level errors plus section failures) is exactly:

```text
invalid_argument
process_instance_not_found
ambiguous_process_instance
thread_instance_not_found
ambiguous_thread_instance
trace_not_loaded
trace_access_denied
trace_conversion_failed
analysis_failed
cancelled
budget_exceeded
response_too_large
symbol_policy_denied
startup_window_truncated
```

Create these exact execution/configuration boundaries:

```csharp
namespace WprMcp.Core;

internal enum OutputContractMode { Legacy, V2 }
internal enum PrivacyMode { Off, Paths, Strict }

internal static class McpWireHardLimits
{
    internal const int DefaultAndMaxRequestBytes = 100_000;
    internal const int DefaultAndMaxResponseBytes = 100_000;
    internal const int MinRequestBytes = 4_096;
    internal const int MinResponseBytes = 4_096;
    internal const int MaxSerializedRequestIdBytes = 128;
}

internal sealed record McpBudgetOptions(
    int MaxToolArgumentBytes = 16 * 1024,
    int MaxStringChars = 4_096,
    int MaxCollectionItems = 128,
    int MaxTop = 1_000,
    int MaxHistogramBuckets = 1_000,
    int MaxJsonRpcRequestBytes = McpWireHardLimits.DefaultAndMaxRequestBytes,
    int ResponseWarningBytes = 40_000,
    int MaxJsonRpcResponseBytes = McpWireHardLimits.DefaultAndMaxResponseBytes);

internal sealed record ToolExecutionOptions(
    OutputContractMode ContractMode,
    PrivacyMode PrivacyMode,
    McpBudgetOptions Budgets);

internal sealed record ToolOutcome<T>(
    T Data,
    bool HasUsableData,
    IReadOnlyList<ToolSectionFailure> FailedSections,
    IReadOnlyList<ToolSectionPage> Sections,
    IReadOnlyList<string> Warnings)
{
    public static ToolOutcome<T> Succeeded(
        T data,
        IReadOnlyList<ToolSectionPage>? sections = null,
        IReadOnlyList<string>? warnings = null);

    public static ToolOutcome<T> Partial(
        T data,
        IReadOnlyList<ToolSectionFailure> failedSections,
        IReadOnlyList<ToolSectionPage>? sections = null,
        IReadOnlyList<string>? warnings = null);
}

internal sealed class ToolContractException(
    string code, string publicMessage, bool retryable = false) : Exception
{
    public string Code { get; } = code;
    public string PublicMessage { get; } = publicMessage;
    public bool Retryable { get; } = retryable;
}

internal interface IToolCallExecutor
{
    ValueTask<CallToolResult> InvokeAsync(
        string toolName,
        Type dataType,
        McpServerTool innerTool,
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken);
}

internal interface IToolFailureContext
{
    IDisposable BeginInvocation();
    void Record(ToolError error);
    void RecordSectionFailure(ToolSectionFailure failure);
    ToolError? RecordedError { get; }
    IReadOnlyList<ToolSectionFailure> RecordedSectionFailures { get; }
}

internal interface IToolOutcomeClassifier
{
    ToolOutcome<JsonNode> ClassifyFallback(string toolName, JsonNode data);
}

internal interface IToolOutcomeAdapter
{
    string ToolName { get; }
    ToolOutcome<JsonNode> Adapt(
        JsonNode data,
        ToolExecutionOptions options,
        IToolFailureContext failures);
}

internal interface IToolOutcomeAdapterRegistry
{
    ToolOutcome<JsonNode> Adapt(
        string toolName,
        JsonNode data,
        ToolExecutionOptions options,
        IToolFailureContext failures);
}

internal interface IToolArgumentPolicy
{
    ToolError? Validate(CallToolRequestParams request);
}

internal interface IToolResultFinalizer
{
    CallToolResult Finalize(RequestId requestId, CallToolResult result);
}
```

Extend `McpServerOptions` to this exact record and startup surface:

```csharp
internal sealed record McpServerOptions(
    string[] HostArgs,
    string? SymbolPath,
    int? CacheSize,
    ToolExecutionOptions ToolExecution);
```

This is an additive Child 5 change: the existing `SymbolPath` and `CacheSize` compatibility fields remain temporarily, and every contract/privacy/budget read goes through `options.ToolExecution`. Child 6 consumes/removes the legacy symbol scalar when it adds `TraceAndSymbolPolicyOptions TracePolicy`; Child 7 consumes/removes the legacy cache scalar when it adds `TraceRegistryOptions Registry`; Child 8 adds `AnalysisRuntimeOptions Analysis`. Each child preserves every nested property owned by an earlier child. The final merged ownership contract is exactly:

```csharp
internal sealed record McpServerOptions(
    string[] HostArgs,
    ToolExecutionOptions ToolExecution,
    TraceAndSymbolPolicyOptions TracePolicy,
    TraceRegistryOptions Registry,
    AnalysisRuntimeOptions Analysis);
```

Flags are `--output-contract legacy|v2` and `--privacy off|paths|strict`. This child deliberately lands the compatibility-stage defaults `legacy` and `off` so existing clients can migrate while both modes are exercised. After Child 9 protocol gates and Child 10 evidence gates pass, Child 11B performs the separately reviewed secure-default event: `v2` becomes the no-flag default, while legacy requires explicit `--output-contract legacy`; privacy remains an independent startup choice. Budget lowering uses `WPRMCP_MAX_TOOL_ARGUMENT_BYTES`, `WPRMCP_MAX_STRING_CHARS`, `WPRMCP_MAX_COLLECTION_ITEMS`, `WPRMCP_MAX_JSONRPC_REQUEST_BYTES`, `WPRMCP_RESPONSE_WARNING_BYTES`, and `WPRMCP_MAX_JSONRPC_RESPONSE_BYTES`. Static option parsing requires request and response caps within their 4,096..100,000 ranges, `MaxToolArgumentBytes <= MaxJsonRpcRequestBytes`, and `0 < ResponseWarningBytes <= MaxJsonRpcResponseBytes`; zero, negative, ceiling-plus-one, cross-field-inconsistent, or statically below-minimum values fail before host construction. After the selected contract mode's active catalog and canonical cursor codec exist, a second preflight measures `MinimumViableResponseBytes` and rejects `MaxJsonRpcResponseBytes < max(McpWireHardLimits.MinResponseBytes, MinimumViableResponseBytes)` before the transport begins reading stdin. If the measured minimum exceeds the unchanged 100,000-byte hard ceiling, startup reports the catalog as unservable instead of silently omitting a tool. No tool parameter can override these values.

The six thread-scoped tools retain Child 2's complete typed method signatures. Their MCP wrappers append the three selector properties after every existing domain parameter, so active v2 schemas expose this method-compatible property order:

```text
wait_analysis(path, top, pid, startUs, endUs, tid, processStartUs, threadStartUs)
wait_top_stacks(path, top, pid, startUs, endUs, whenBuckets, compactStacks,
                summaryOnly, resolveSymbols, tid, processStartUs, threadStartUs)
wait_caller_callee(path, function, top, pid, startUs, endUs, resolveSymbols,
                   tid, processStartUs, threadStartUs)
cpu_precise_analysis(path, top, pid, startUs, endUs, tid, processStartUs, threadStartUs)
cpu_top_functions(path, top, pid, startUs, endUs, excludeEtwSelfOverhead,
                  includeTracePct, resolveSymbols, tid, processStartUs, threadStartUs)
cpu_caller_callee(path, function, top, pid, startUs, endUs,
                  excludeEtwSelfOverhead, resolveSymbols, tid, processStartUs,
                  threadStartUs)
```

This list preserves each method's complete pre-Child-2 argument order and appends `tid`, `processStartUs`, and `threadStartUs`. The emitted input-schema property order may mirror the method order, but clients must treat the schema/arguments as JSON objects.

For all six, `tid` without `pid` returns `Failed/invalid_argument`; no matching lifetime returns `Failed/thread_instance_not_found`; multiple matching lifetimes return `Failed/ambiguous_thread_instance`. Absence of symbols never changes selector success or duration totals.

---

## File structure overview

| File | Action | Purpose |
|---|---|---|
| `src/WprMcp/Output/ToolEnvelope.cs` | Create | Envelope, error, paging, batch, coverage, and quality DTOs/converters |
| `src/WprMcp/Core/ToolExecution.cs` | Create | SDK-result wrapper, outcome classification, stable exception context, legacy/v2 creation |
| `src/WprMcp/Core/ToolOutcomeAdapters.cs` | Create | Child 2/3/4 semantic handoff adapters plus generic fallback registry |
| `src/WprMcp/Core/McpToolCatalog.cs` | Create | Programmatic SDK tool creation and active output schemas |
| `src/WprMcp/Core/ToolContractManifest.cs` | Create | Validated section names and row-array JSON pointers |
| `src/WprMcp/Core/tool-contracts.v2.json` | Create | Complete 60-tool data type/section/paging registry |
| `src/WprMcp/Core/McpBudgetPolicy.cs` | Create | Argument limits, list pagination, exact JSON-RPC frame fitting |
| `src/WprMcp/Core/JsonRpcFrameLimitingStream.cs` | Create | Configured pre-deserialization stdio request cap (100,000-byte default/hard ceiling) |
| `src/WprMcp/Core/PrivacyRedactor.cs` | Create | Taxonomy-based output and diagnostic redaction |
| `src/WprMcp/Core/PrivacyLogSink.cs` | Create | Redacting `TextWriter` and logging/telemetry boundary |
| `src/WprMcp/Core/TypedAliasRegistry.cs` | Create | Typed process-local HMAC aliases with deterministic bounded eviction |
| `src/WprMcp/Core/ToolArgumentRewriter.cs` | Create | Invocation-scoped clone/rewrite seam consumed by later resolvers |
| `src/WprMcp/Core/privacy-field-taxonomy.v1.json` | Create | Off/paths/strict treatment and alias-enabled input fields |
| `src/WprMcp/McpServerOptions.cs` | Modify | Parse startup contract/privacy/budget settings without mutating policy later |
| `src/WprMcp/Program.cs` | Modify | Register catalog, executor, request filters, redacted logging, and telemetry |
| `src/WprMcp/Core/ToolListPayload.cs` | Modify | Measure/paginate the same active catalog used by the real server |
| `src/WprMcp/Core/McpTelemetryFilters.cs` | Modify | Record only finalized/redacted result sizes and hashes |
| `src/WprMcp/Core/ToolTelemetry.cs` | Modify | Route text through privacy sink; never store raw response/error text |
| `src/WprMcp/Core/Validation.cs` | Modify | Throw stable `invalid_argument`; enforce common collection/string limits |
| `src/WprMcp/Core/StackResponseOptions.cs` | Modify | Remove advisory-only response byte constants in favor of enforced policy |
| `src/WprMcp/Output/Records.cs` | Modify | Batch partition, domain coverage, PDB identity, frame resolution, thread quality |
| the 15 tool files with direct `Console.Error` writes | Modify | Inject privacy-aware writer without changing typed returns |
| `tests/WprMcp.Tests/ToolEnvelopeTests.cs` | Create | Envelope/IsError/error-code invariants |
| `tests/WprMcp.Tests/ToolOutcomeAdapterTests.cs` | Create | Duration, startup, PID-reuse, and fallback outcome projection |
| `tests/WprMcp.Tests/BatchContractTests.cs` | Create | Exact requested partition and injected per-PID failures |
| `tests/WprMcp.Tests/CapabilityQualityContractTests.cs` | Create | Domain coverage, PDB identity, symbol and thread-quality separation |
| `tests/WprMcp.Tests/McpBudgetPolicyTests.cs` | Create | Argument and full-frame byte boundaries |
| `tests/WprMcp.Tests/PrivacyRedactorTests.cs` | Create | Taxonomy, aliases, sentinel and logging behavior |
| `tests/WprMcp.Tests/McpServerOptionsTests.cs` | Modify | Exact startup defaults/rejections |
| `tests/WprMcp.Tests/McpSdkSurfaceTests.cs` | Modify | Catalog completeness and SDK programmatic-schema proof |
| `tests/WprMcp.Tests/TelemetryTests.cs` | Modify | Privacy and finalized-byte assertions |
| `tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj` | Create | Windows process-level protocol tests, later expanded by Child 9 |
| `tests/WprMcp.ProtocolTests/StdioMcpClient.cs` | Create | Minimal real initialize/list/call client with deterministic timeouts |
| `tests/WprMcp.ProtocolTests/ToolSchemaContractTests.cs` | Create | Real `tools/list` schema snapshots and response validation |
| `tests/WprMcp.ProtocolTests/Snapshots/tools-list.legacy.json` | Create | Normalized legacy surface |
| `tests/WprMcp.ProtocolTests/Snapshots/tools-list.v2.json` | Create | Normalized v2 surface, including all output schemas |
| `WprMcp.sln` | Modify | Add protocol-test project |

The 15 privacy-edited tool files are exactly `AlpcTools.cs`, `ClrTools.cs`, `CpuTools.cs`, `DiagnoseTools.cs`, `GenericProviderTools.cs`, `HardFaultTools.cs`, `HeapTools.cs`, `ImageLoadTools.cs`, `InterruptTools.cs`, `IoTools.cs`, `NetIoTools.cs`, `ReadyThreadTools.cs`, `RegistryTools.cs`, `VirtualMemoryTools.cs`, and `WaitTools.cs`.

---

### Task 1: Land envelope, error, and status invariants (TDD)

**Files:**
- Create: `src/WprMcp/Output/ToolEnvelope.cs`
- Create: `src/WprMcp/Core/ToolExecution.cs`
- Create: `src/WprMcp/Core/ToolOutcomeAdapters.cs`
- Create: `tests/WprMcp.Tests/ToolEnvelopeTests.cs`
- Create: `tests/WprMcp.Tests/ToolOutcomeAdapterTests.cs`
- Modify: `src/WprMcp/Program.cs`

- [ ] **Step 1: Write the failing tests**

Add tests named:

```text
Succeeded_RequiresDataAndNeverSetsIsError
Partial_RequiresUsableDataAndFailedSectionAndNeverSetsIsError
Failed_RequiresErrorAndSetsIsError
CompletionStatus_SerializesAsStableLowercaseString
StableErrorCodes_AreUniqueAndComplete
RawExceptionMessage_IsNotPresentInFailedResult
OperationLocalCancellationWithoutEvidence_ReturnsFailedCancelled
TransportCancellationBeforeOrDuringInvoke_RethrowsAndEmitsNoTerminalFrame
TransportCancellationAfterInnerResultBeforeFinalize_EmitsNoLateFrame
LegacyDurationHandoff_EmitsTimeSemanticsV2WarningAndLegacyAliases
V2DurationHandoff_ExposesFullAndAccountedFieldsWithoutLegacyWarning
StartupWindowTruncated_MapsToConcreteFailedSectionAndPartial
PidOnlyAmbiguousProcessInstance_RemainsSucceededWarning
ExplicitProcessInstanceAmbiguity_RemainsFailed
UnregisteredTool_UsesGenericClassifierFallback
```

Invoke `IToolCallExecutor` with a fake typed `McpServerTool` and assert both the text block and `StructuredContent` deserialize to the same `ToolEnvelope<T>`. Test the exact stable-code list printed above. In `ToolOutcomeAdapterTests`, use the actual Child 3 warning/dual-duration JSON shape, Child 4 `StartupWindowProvenance(Status="Partial",Code="startup_window_truncated")`, and Child 2 `PidReuseObserved`/warning shape rather than inventing parallel DTOs.

- [ ] **Step 2: Run the focused tests and observe RED**

Run:

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~ToolEnvelopeTests|FullyQualifiedName~ToolOutcomeAdapterTests"
```

Expected: compile errors for `ToolEnvelope<>`, `ToolOutcome<>`, `ContractMcpServerTool`, `IToolCallExecutor`, and the semantic adapter registry.

- [ ] **Step 3: Implement the contract and executor**

Implement the fixed contracts above. `ToolCallExecutor.InvokeAsync` opens an `IToolFailureContext` scope, invokes the inner typed SDK tool, projects structured domain data through `IToolOutcomeAdapterRegistry`, and uses this exact state table:

```csharp
using var failureScope = _failureContext.BeginInvocation();
try
{
    var raw = await innerTool.InvokeAsync(request, cancellationToken);
    if (raw.IsError == true)
    {
        var recorded = _failureContext.RecordedError;
        return CreateFailed(dataType, recorded ??
            new ToolError("analysis_failed", "The analysis could not be completed.", false));
    }

    var data = RequireStructuredData(raw, dataType);
    var outcome = _outcomeAdapters.Adapt(
        toolName, data, _options.ToolExecution, _failureContext);
    var failures = MergeFailures(
        outcome.FailedSections, _failureContext.RecordedSectionFailures);

    if (!outcome.HasUsableData)
    {
        return CreateFailed(dataType, _failureContext.RecordedError ??
            new ToolError("analysis_failed", "The analysis could not be completed.", false));
    }

    return failures.Count == 0
        ? CreateSucceeded(outcome.Data, outcome.Sections, outcome.Warnings)
        : CreatePartial(outcome.Data, failures, outcome.Sections, outcome.Warnings);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    throw; // transport cancellation: the SDK/host emits no terminal frame
}
catch (ToolOperationCancelledException)
{
    return CreateFailed(dataType,
        new ToolError("cancelled", "The operation was cancelled.", true));
}
catch (OperationCanceledException ex)
{
    _logger.LogError(ex, "Unexpected cancellation source.");
    return CreateFailed(dataType,
        new ToolError("analysis_failed", "The analysis could not be completed.", false));
}
catch (Exception ex)
{
    _logger.LogError(ex, "Tool execution failed.");
    return CreateFailed(dataType,
        new ToolError("analysis_failed", "The analysis could not be completed.", false));
}
```

The concrete implementation uses a typed `ToolOperationCancelledException` (or equivalent classifier) for an operation-local deadline/cancellation whose transport token is still open; only that case may enter the `Failed/cancelled` branch. It checks the transport token again immediately before finalization/write. Once transport cancellation is observed, it rethrows and neither the executor, finalizer, progress reporter, nor SDK adapter may emit a success/error frame for that request. Tests gate cancellation before invocation, while the inner tool is running, after an inner result but before finalization, and during serialization; all transport-token cases produce zero terminal frames, while an operation-local deadline with an open transport returns the stable cancelled envelope.

`ToolOutcomeAdapterRegistry` rejects duplicate registrations, dispatches exact ordinal tool names, and calls `IToolOutcomeClassifier.ClassifyFallback` only when no adapter is registered. Register these semantic adapters:

- The duration adapter consumes Child 3's existing dual full/accounted fields and `time_semantics_v2:` warning. In legacy mode it keeps legacy duration aliases equal to accounted values and emits exactly one warning; in v2 it preserves both full and accounted fields and removes only that legacy-migration warning.
- The `diagnose_slow_startup` adapter finds each Child 4 provenance item with `Status="Partial"` and `Code="startup_window_truncated"`, preserves the usable candidate/evidence data, and adds one deduplicated `ToolSectionFailure("startupWindow", "startup_window_truncated", "The observed trace ended before the requested startup window.", false)`. That yields `Partial` with `IsError=false`, never a clean success or top-level failure.
- The six CPU/wait adapters preserve Child 2 PID-only aggregate data and its single `ambiguous_process_instance` warning. PID-only reuse must not call `IToolFailureContext.Record`; only an explicit `pid + processStartUs` selection failure records the top-level error and returns `Failed`.

`MergeFailures` deduplicates by `(Section,Code)` in first-observed order. A typed response with no usable batch item sets `HasUsableData=false`; a startup-truncated response and PID-only aggregate always set it true. Generic fallback remains covered so adapters cannot accidentally become mandatory for unrelated tools.

Shared `Validation`, process/thread resolvers, trace access, conversion, and symbol policy record a stable public `ToolError` in the invocation-local context before throwing; the SDK may sanitize the inner exception, but the outer wrapper retains the stable classification without exposing raw text. Legacy mode returns the SDK's original typed text for success and retains the current public JSON shape. V2 serializes one envelope to both a `TextContentBlock` and `StructuredContent`; failures always use a v2 failure envelope so error codes remain machine-readable during migration. Constructor guards reject illegal status/data/error combinations.

- [ ] **Step 4: Run GREEN**

Run the focused command again. Expected: all envelope and adapter tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Output/ToolEnvelope.cs src/WprMcp/Core/ToolExecution.cs src/WprMcp/Core/ToolOutcomeAdapters.cs src/WprMcp/Program.cs tests/WprMcp.Tests/ToolEnvelopeTests.cs tests/WprMcp.Tests/ToolOutcomeAdapterTests.cs
git commit -m "feat(contract): add versioned tool envelope and stable failures"
```

---

### Task 2: Replace reflection-only exposure with the active programmatic catalog (TDD)

**Files:**
- Create: `src/WprMcp/Core/McpToolCatalog.cs`
- Create: `src/WprMcp/Core/ToolContractManifest.cs`
- Create: `src/WprMcp/Core/tool-contracts.v2.json`
- Modify: `src/WprMcp/Program.cs`
- Modify: `src/WprMcp/Core/ToolListPayload.cs`
- Modify: `tests/WprMcp.Tests/McpSdkSurfaceTests.cs`

- [ ] **Step 1: Add failing catalog tests**

Add these exact tests to `McpSdkSurfaceTests`:

```text
ActiveCatalog_ContainsEveryAttributedToolExactlyOnce
V2Catalog_EveryToolAdvertisesEnvelopeOutputSchema
LegacyCatalog_AdvertisesNoOutputSchema
V2Schemas_DisallowAdditionalPropertiesAndExpressNullableDataAndError
ToolContractManifest_CoversEveryToolAndUsesUniqueStableSectionNames
```

The first test compares `(declaring type, method)` pairs, not only names. The manifest test requires one entry for all 60 currently exposed methods; every pageable JSON pointer must end at an array in the declared response type.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~McpSdkSurfaceTests
```

Expected: failures because only three methods currently request structured output and the active server still calls `WithToolsFromAssembly()`.

- [ ] **Step 3: Implement programmatic creation and migrate tool methods**

`McpToolCatalog.Create` has this exact signature:

```csharp
internal static IReadOnlyList<McpServerTool> Create(
    Assembly assembly,
    ToolExecutionOptions executionOptions,
    JsonSerializerOptions serializerOptions);
```

For each attributed method, infer `TData` from its typed return (`T`, `Task<T>`, or `ValueTask<T>`), and fail startup for `void`, untyped `object`, or `CallToolResult` unless the manifest supplies an explicit adapter. Create its target with:

```csharp
context => ActivatorUtilities.CreateInstance(context.Services, declaringType)
```

Create the SDK inner tool with `UseStructuredContent=true` so its typed domain return is available to the wrapper. Construct `ContractMcpServerTool(inner, toolName, dataType, executor, protocolTool)`. In v2, `protocolTool.OutputSchema` is generated for `ToolEnvelope<TData>` using `AIJsonSchemaTransformOptions` with `DisallowAdditionalProperties=true`, `UseNullableKeyword=true`, and `RequireAllProperties=true`. In legacy, expose the same input/annotations but set `OutputSchema=null`; the wrapper returns the inner legacy text unchanged for successful calls.

Preserve every attributed typed method untouched. The SDK spike test must call one representative typed method through both `inner.InvokeAsync` and `ContractMcpServerTool.InvokeAsync` and prove structured data, error behavior, annotations, input schema, and output schema. If this selected SDK cannot support the wrapper, stop and amend the Child 11A ADR before implementing explicit MCP adapter methods.

Replace `WithToolsFromAssembly()` in `Program.cs` with `.WithTools(McpToolCatalog.Create(...))`. Make `ToolListPayload` accept the same catalog; remove its separate assembly scanner so measurement cannot diverge from production.

- [ ] **Step 4: Run GREEN and surface guard**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~McpSdkSurfaceTests|FullyQualifiedName~ToolEnvelopeTests"
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release
```

Expected: every test passes; the active catalog reports 60 unique tools unless another child intentionally changed the frozen surface, in which case update the manifest and snapshot in the same contract-versioned commit.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Core/McpToolCatalog.cs src/WprMcp/Core/ToolContractManifest.cs src/WprMcp/Core/tool-contracts.v2.json src/WprMcp/Core/ToolListPayload.cs src/WprMcp/Program.cs tests/WprMcp.Tests/McpSdkSurfaceTests.cs
git commit -m "refactor(mcp): register every tool through the active contract catalog"
```

---

### Task 3: Make batch accounting and stable tool errors truthful (TDD)

**Files:**
- Create: `tests/WprMcp.Tests/BatchContractTests.cs`
- Modify: `src/WprMcp/Output/Records.cs`
- Modify: `src/WprMcp/Tools/CpuTools.cs`
- Modify: `src/WprMcp/Analyzers/CpuAnalysis.cs`
- Modify: `src/WprMcp/Core/Validation.cs`
- Modify: `src/WprMcp/Core/ToolExecution.cs`
- Modify: `src/WprMcp/Core/ToolOutcomeAdapters.cs`
- Modify: `src/WprMcp/Analyzers/ProcessInstanceResolver.cs`
- Modify: `src/WprMcp/Analyzers/ThreadInstanceCatalog.cs`
- Modify: `src/WprMcp/Analyzers/ThreadAnalysisScope.cs`

- [ ] **Step 1: Write failing tests**

Add:

```text
CpuBatch_InjectedPidFailure_ReturnsPartialNotCleanEmptySuccess
CpuBatch_DeduplicatedRequestedSet_IsExactDisjointUnion
CpuBatch_TimeBudget_RecordsRemainingItemsAsSkipped
CpuBatch_AllItemsFail_ReturnsFailedAnalysisFailed
CpuRawSources_InjectedPerPidAnalyzerFailure_IsRecordedNotSwallowed
SixThreadTools_TidWithoutPid_ReturnFailedInvalidArgumentBeforeTraceAccess
SixThreadTools_MissingLifetime_ReturnThreadInstanceNotFound
SixThreadTools_ReusedLifetime_ReturnAmbiguousThreadInstance
SixThreadTools_PidOnlyReuse_ReturnsAggregateWithAmbiguousProcessWarning
```

Use an injected `ICpuPidAnalyzer` fake to fail one PID deterministically. Use a spy trace registry to prove `tid` without `pid` performs zero acquisitions.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~BatchContractTests
```

Expected: the current blanket `catch (Exception ex)` returns empty data, leaks `ex.Message`, and leaves `Partial=false` when no PID was added to `SkippedPids`.

- [ ] **Step 3: Implement exact partitioning**

Preserve the existing typed `CpuTopFunctionsBatchResponse` shape exactly so `CliRunner`, `CpuAnalysisTests`, and direct callers remain source-compatible:

```csharp
public sealed record CpuTopFunctionsBatchResponse(
    IReadOnlyDictionary<int, CpuTopFunctionsResponse> PerPid,
    IReadOnlyList<string> Warnings,
    bool Partial = false,
    IReadOnlyList<int>? SkippedPids = null,
    int RequestedPidCount = 0,
    int CompletedPidCount = 0);
```

Deduplicate requested PIDs in first-occurrence order. Each requested PID enters exactly one of successful `PerPid` keys, recorded `perPid:<pid>` section failures, or `SkippedPids`. In `CpuAnalysis.BuildTopFunctionsResponsesForRawSources`, remove the broad per-PID `catch (Exception ex)` that turns analyzer failure into a warning/empty success. Route each PID through injected `ICpuPidAnalyzer`; let its typed per-PID failure reach `CpuTools`, which records one fixed-message `ToolSectionFailure` in the invocation's `IToolFailureContext`. It never changes the typed response shape. Budget exhaustion skips only not-started PIDs. The injected regression proves the exact requested set equals the disjoint union of successful, failed, and skipped PIDs and that raw `ex.Message` appears nowhere. The registered CPU-batch outcome adapter merges recorded section failures with the SDK-serialized typed response; `IToolOutcomeClassifier` remains only the fallback for tools with no adapter. If at least one item succeeded, any failure/skip produces a v2 `Partial`; if none succeeded, the context records the first stable top-level error, the adapter sets `HasUsableData=false`, and the v2 wrapper returns `Failed` with no fake data. The legacy typed/CLI response retains its existing properties and receives only fixed non-sensitive warnings. Never copy `Exception.Message` to either contract.

Make `ProcessInstanceResolver`, `ThreadInstanceCatalog`, and `ThreadAnalysisScope` record `ToolError` with their exact stable codes in `IToolFailureContext` only for exact selectors that fail and then throw their internal exception. A PID-only aggregate with reused lifetimes is successful domain data: it sets Child 2's `PidReuseObserved`, appends exactly one `ambiguous_process_instance` warning, and never records a top-level error. Add the shared validation rule `tid is not null && pid is null -> invalid_argument` before trace resolution for all six tools. Their typed return values and CLI behavior remain unchanged; only MCP wrapping consumes the recorded classification.

- [ ] **Step 4: Run GREEN**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~BatchContractTests|FullyQualifiedName~ToolEnvelopeTests|FullyQualifiedName~ToolOutcomeAdapterTests"
```

Expected: all batch/status/error tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Output/Records.cs src/WprMcp/Tools/CpuTools.cs src/WprMcp/Analyzers/CpuAnalysis.cs src/WprMcp/Core/Validation.cs src/WprMcp/Core/ToolExecution.cs src/WprMcp/Core/ToolOutcomeAdapters.cs src/WprMcp/Analyzers/ProcessInstanceResolver.cs src/WprMcp/Analyzers/ThreadInstanceCatalog.cs src/WprMcp/Analyzers/ThreadAnalysisScope.cs tests/WprMcp.Tests/BatchContractTests.cs tests/WprMcp.Tests/ToolOutcomeAdapterTests.cs
git commit -m "fix(contract): partition batch work and preserve stable tool errors"
```

---

### Task 4: Separate capture capability, observed stack coverage, PDB identity, frame resolution, and thread quality (TDD)

**Files:**
- Create: `tests/WprMcp.Tests/CapabilityQualityContractTests.cs`
- Create: `tests/WprMcp.Tests/ThreadQualityScopeFixture.cs`
- Modify: `src/WprMcp/Output/Records.cs`
- Modify: `src/WprMcp/Analyzers/TraceCapabilitiesDetector.cs`
- Modify: `src/WprMcp/Analyzers/StackProbeAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/StackSourceTopN.cs`
- Modify: `src/WprMcp/Tools/MetaTools.cs`
- Modify: `src/WprMcp/Tools/SymbolTools.cs`
- Modify: `src/WprMcp/Tools/CpuTools.cs`
- Modify: `src/WprMcp/Tools/WaitTools.cs`

- [ ] **Step 1: Write failing quality tests**

Add:

```text
CpuOnlyStacks_DoNotClaimFileClrRegistryOrNetworkCoverage
Coverage_NoEligibleEvents_IsNoEventsWithNullRatio
Coverage_EligibleWithoutStacks_IsNoStacksWithZeroRatio
Coverage_SomeStacks_IsPartialWithExactRatio
Coverage_AllStacks_IsCompleteWithOneRatio
PdbIdentity_RequiresNameGuidAndAge
FrameResolution_IsIndependentFromPdbIdentityCoverage
ThreadQuality_NoCSwitch_HasNoReliableDuration
ThreadQuality_CSwitchWithoutDomainStacks_PreservesDurations
ThreadQuality_UnresolvedStackAddresses_PreservesSelectorAndTotals
ThreadQuality_ResolvedStacks_IsComplete
ThreadQuality_OtherPidHasOnlyStack_TargetThreadIsDurationOnlyNoDomainStacks
ThreadQuality_OtherTidGenerationHasOnlyStack_TargetGenerationIsDurationOnlyNoDomainStacks
ThreadQuality_FilterRunsBeforeTopNAndSymbolization
```

The thread tests use the same resolved `ThreadAnalysisScope` and exact half-open window for summary, top stacks, and caller/callee. `ThreadQualityScopeFixture` provides a target thread with eligible unstacked events plus (a) a stacked event owned by another PID/TID and (b) a stacked event owned by a different generation of the same reused TID. Assert the target remains `DurationOnlyNoDomainStacks`, its `DomainStacks.State` remains `NoStacks`, eligible/stacked/frame counts exclude both foreign events, process/thread instance keys are identical, and CPU/blocked totals do not change when symbol resolution is toggled. Spies assert neither TopN nor symbolization receives an out-of-scope event.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~CapabilityQualityContractTests
```

Expected: current `HasStackWalks` and `ResolvedModuleCount` fields conflate unrelated domains and PDB presence with actual resolved frames.

- [ ] **Step 3: Implement exact quality construction**

Add one `DomainStackCoverage` per supported event domain to `InspectTraceResponse`; compute eligible and stacked counts from that domain's own event stream. `CaptureCapability` is provider/profile capability and never inferred from another domain's stack. State mapping is exact: eligible `0` -> `NoEvents`; eligible `>0` and stacked `0` -> `NoStacks`; `0<stacked<eligible` -> `Partial`; equality -> `Complete`.

Replace module-resolution-rate claims in v2 with `PdbIdentityCoverage` and `FrameResolutionStats`. Complete PDB identity means non-empty PDB name, parseable GUID/signature, and age present; a module path or PDB name alone does not count. Frame resolution counts observed emitted frames and accepts addresses or `module!?` as unresolved output.

For each of the six thread tools, attach `ThreadAnalysisQuality` to its v2 data. Compute `EligibleEventCount`, `StackedEventCount`, resolved/unresolved frame counts, and `ThreadAnalysisQualityState` only after applying the exact resolved `ThreadAnalysisScope.Matches(processInstance,threadInstance,timestamp)` and the same half-open `TimeWindow` used for durations. Do not reuse trace-wide, PID-wide, raw-TID, or event-domain coverage for a selected thread. Filter before stack construction, TopN, and symbolization; only the filtered stream may contribute rows or quality counters. `NoContextSwitch` is the only state that invalidates reliable scheduler durations. `DurationOnlyNoDomainStacks` leaves CPU/off-CPU totals valid even when another PID/TID or another generation of the same TID has complete stacks. `StackAddressesUnresolved` leaves selector/totals valid and exposes degraded stack rows. TopN occurs after the scope/window filter, so a selected below-TopN thread remains present.

- [ ] **Step 4: Run GREEN**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~CapabilityQualityContractTests|FullyQualifiedName~WaitBoundFixtureTests"
```

Expected: all quality tests and the real wait-bound fixture tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Output/Records.cs src/WprMcp/Analyzers/TraceCapabilitiesDetector.cs src/WprMcp/Analyzers/StackProbeAnalysis.cs src/WprMcp/Analyzers/StackSourceTopN.cs src/WprMcp/Tools/MetaTools.cs src/WprMcp/Tools/SymbolTools.cs src/WprMcp/Tools/CpuTools.cs src/WprMcp/Tools/WaitTools.cs tests/WprMcp.Tests/CapabilityQualityContractTests.cs tests/WprMcp.Tests/ThreadQualityScopeFixture.cs
git commit -m "fix(quality): separate domain coverage symbols and thread evidence"
```

---

### Task 5: Enforce startup configuration and input budgets before trace access (TDD)

**Files:**
- Modify: `src/WprMcp/McpServerOptions.cs`
- Modify: `src/WprMcp/Core/Validation.cs`
- Modify: `src/WprMcp/Program.cs`
- Create: `src/WprMcp/Core/McpBudgetPolicy.cs`
- Create: `src/WprMcp/Core/JsonRpcFrameLimitingStream.cs`
- Create: `tests/WprMcp.Tests/McpBudgetPolicyTests.cs`
- Modify: `tests/WprMcp.Tests/McpServerOptionsTests.cs`

- [ ] **Step 1: Write boundary tests**

Add exact test names:

```text
Parse_DefaultsToLegacyAndPrivacyOff
Parse_AcceptsV2PathsAndStrictModes
Parse_RejectsUnknownContractOrPrivacyMode
Parse_RejectsBudgetAboveHardCeiling
Parse_RejectsRequestOrResponseBelowStaticMinimumAndCrossFieldInconsistency
Parse_OwnsContractPrivacyAndBudgetsOnlyUnderToolExecution
RawRequestFrame_99999And100000Utf8BytesPass100001ThrowsBeforeDeserializer
RawRequestFrame_ConfiguredLowerCapMinusOneAndExactPassPlusOneFails
RawRequestFrame_MultibyteUtf8CountsBytesAndResetsOnlyAtNewline
RawRequestFrame_OversizeNeverInvokesSdkDeserializerOrTraceResolver
RequestId_Serialized127And128BytesPass129TerminatesWithoutSideEffects
RequestId_NumericInt64MinZeroAndMaxRoundTripExactly
Arguments_Utf8Bytes_LimitMinusOneAndExactPassPlusOneFails
Arguments_MultibyteUtf8_UsesBytesNotCharacters
StringCharacters_4095And4096Pass4097Fails
CollectionItems_127And128Pass129Fails
TopAndHistogram_999And1000Pass1001Fails
TidWithoutPid_FailsBeforeTraceResolver
```

Use a `CountingTraceRegistry` to assert zero calls for every rejected request.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~McpBudgetPolicyTests|FullyQualifiedName~McpServerOptionsTests"
```

Expected: current options know only symbol path/cache size; raw stdin reaches SDK deserialization without a hard frame limit; validation does not enforce serialized argument, string, or collection ceilings.

- [ ] **Step 3: Implement input policy**

Implement the fixed `McpServerOptions`, nested `ToolExecutionOptions`, and `McpBudgetOptions` contracts. All consumers read `options.ToolExecution.ContractMode`, `.PrivacyMode`, or `.Budgets`; a reflection assertion rejects sibling `ContractMode`, `PrivacyMode`, or `Budgets` properties on `McpServerOptions`.

`JsonRpcFrameLimitingStream` wraps `Console.OpenStandardInput()` and receives `options.ToolExecution.Budgets.MaxJsonRpcRequestBytes`; it counts raw UTF-8 bytes from the first byte through the terminating newline, permits exactly that configured cap, and throws `JsonRpcFrameTooLargeException` on cap plus one before returning that byte to a downstream reader. Default behavior is 100,000/100,001, while a lowered-cap test proves the same boundary without changing code. It retains only the bounded current-frame buffer, resets only after `\n`, treats EOF with a partial frame as normal downstream parse failure, and implements both synchronous and asynchronous reads with identical accounting. Configure the pinned SDK with `.WithStreamServerTransport(limitedInput, Console.OpenStandardOutput())` instead of `.WithStdioServerTransport()`. A raw-frame violation closes that MCP session with process exit code 64 and no stdout response because the request ID was not safely deserialized; it emits at most one fixed, redacted stderr line. This is the public stdio request-frame boundary. Child 8's `MaxIpcFrameBytes` remains a separate parent/worker binary-protocol limit and must not replace or relax it.

At the first bounded parsed-frame seam proven by Child 11A, serialize only the JSON-RPC `id` token with the pinned JSON options. Accept SDK-representable string or numeric IDs only when the representation is at most `MaxSerializedRequestIdBytes`; reject null/object/array or string-ID byte 129 before SDK tool binding, response construction, alias rewriting, or any trace/file/network access. Because an overlong string ID cannot be echoed inside the minimum response guarantee, close that session with exit code 65, no stdout bytes, and one fixed redacted stderr code. Test the 127/128/129-byte boundary with escaped and multibyte string IDs. Test numeric IDs separately at `long.MinValue`, `0`, and `long.MaxValue`, all of which are valid, bounded, and must round-trip exactly; do not invent a 127-byte numeric token outside the selected SDK's `Int64` request-ID domain. Notifications remain ID-less and unaffected. If the selected SDK exposes no such pre-dispatch seam, Child 11A records a blocker rather than moving the check after tool execution.

Register a call-tool request filter before execution:

```csharp
filters.AddCallToolFilter(next => async (request, cancellationToken) =>
{
    var error = request.Services.GetRequiredService<IToolArgumentPolicy>()
        .Validate(request.Params);
    if (error is not null)
        return ToolExecution.CreateFailed<object>(error);
    return await next(request, cancellationToken);
});
```

Serialize only `CallToolRequestParams.Arguments` with `McpJsonUtilities.DefaultOptions` for the independent 16 KiB parsed tool-argument limit. Recursively enforce strings and collections without converting values to lossy strings. Validate `top`, `whenBuckets`, the six thread-selector relationships, and known PID collections before trace acquisition. The stream cap runs before SDK JSON deserialization; this parsed policy runs after deserialization but before alias rewriting, SDK binding, trace acquisition, or any analyzer.

- [ ] **Step 4: Run GREEN**

Run the focused command again. Expected: every boundary test passes.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/McpServerOptions.cs src/WprMcp/Core/McpBudgetPolicy.cs src/WprMcp/Core/JsonRpcFrameLimitingStream.cs src/WprMcp/Core/Validation.cs src/WprMcp/Program.cs tests/WprMcp.Tests/McpBudgetPolicyTests.cs tests/WprMcp.Tests/McpServerOptionsTests.cs
git commit -m "feat(budget): enforce startup modes and pre-trace argument limits"
```

---

### Task 6: Add taxonomy-driven privacy, aliases, logging, and telemetry safety (TDD)

**Files:**
- Create: `src/WprMcp/Core/PrivacyRedactor.cs`
- Create: `src/WprMcp/Core/PrivacyLogSink.cs`
- Create: `src/WprMcp/Core/TypedAliasRegistry.cs`
- Create: `src/WprMcp/Core/ToolArgumentRewriter.cs`
- Create: `src/WprMcp/Core/privacy-field-taxonomy.v1.json`
- Create: `tests/WprMcp.Tests/PrivacyRedactorTests.cs`
- Modify: `src/WprMcp/Program.cs`
- Modify: `src/WprMcp/Core/McpTelemetryFilters.cs`
- Modify: `src/WprMcp/Core/ToolTelemetry.cs`
- Modify: the 15 tool files currently found by `rg -l 'Console\.Error' src/WprMcp/Tools`

- [ ] **Step 1: Write privacy and alias tests**

Use unique sentinels for a user name, machine name, local path, UNC path, registry key/value, symbol cache path, host, IP address, marker payload, warning, exception, stderr line, and telemetry event. Add:

```text
Off_RetainsAnalyticalFieldsButTelemetryNeverContainsRawSentinels
Paths_RedactsPathsUncUserAndMachineAndKeepsApprovedBasenamesOnly
Strict_AlsoRedactsMarkerHostIpRegistryValuesAndSensitiveBasenames
Alias_IsStableWithinProcessAndChangesAcrossProcesses
AliasResolver_RejectsUnknownWrongKindOverlongAndEvictedAliases
AliasResolver_ResolvesOnlyTaxonomyEnabledParameters
AliasRewrite_ClonesArgumentsAndRunsBeforeSdkBinding
AliasRewrite_ExposesTypedResolvedAliasesToDownstreamResolvers
AliasRewrite_DoesNotReferenceOrInvokeChild6Policy
PrivacySentinels_DoNotReachStdoutStderrWarningsErrorsOrTelemetry
Taxonomy_CoversEverySensitiveRecordPropertyAndLogCategory
Tools_DoNotReferenceConsoleErrorDirectly
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~PrivacyRedactorTests|FullyQualifiedName~TelemetryTests"
```

Expected: paths and exception/warning text appear in DTOs or stderr; no bounded inbound alias registry exists.

- [ ] **Step 3: Implement the privacy boundary**

Use these exact interfaces:

```csharp
internal enum SensitiveFieldKind
{
    TracePath, FilePath, RegistryPath, SymbolPath, UncPath,
    UserName, MachineName, Host, IpAddress, RegistryValue, MarkerPayload
}

internal interface IPrivacyRedactor
{
    JsonNode Redact(JsonNode value);
    string RedactText(string value, SensitiveFieldKind kind);
}

internal interface IPrivacyLogSink
{
    TextWriter Writer { get; }
    string RedactMessage(string message);
}

internal interface ITypedAliasRegistry
{
    string Issue(SensitiveFieldKind kind, string value);
    bool TryResolve(SensitiveFieldKind kind, string alias, out string value);
}

internal sealed record ResolvedAliasArgument(
    string ParameterName,
    SensitiveFieldKind Kind,
    string Alias,
    string Value);

internal sealed record ToolArgumentRewrite(
    JsonObject Arguments,
    IReadOnlyList<ResolvedAliasArgument> ResolvedAliases);

internal interface IToolArgumentRewriter
{
    ToolArgumentRewrite Rewrite(string toolName, JsonObject arguments);
}
```

The taxonomy file lists every sensitive JSON property and behavior for `off`, `paths`, and `strict`, plus exact `(toolName,parameterName,SensitiveFieldKind)` alias-enabled inputs. Alias format is `alias_<kind>_<22 base64url HMAC characters>`, with checked-in lower-case kind tokens and the exact regex generated from the enum-to-token table. Generate a random 32-byte HMAC key per server process. Reissuing the same `(kind,value)` returns the same alias until eviction; retain at most 4,096 distinct mappings and evict the oldest distinct mapping without refreshing it on reads. Detect a truncated-HMAC collision and return a fixed startup/runtime failure rather than binding it to two values. Reject inbound alias strings longer than 128 characters before regex/HMAC lookup; unknown, wrong-kind, malformed, overlong, and evicted aliases all return fixed `invalid_argument`. Never persist keys or mappings.

`ToolArgumentRewriter` clones the request `JsonObject`, consults only the taxonomy and `ITypedAliasRegistry`, replaces enabled alias values in the clone, and returns the typed in-memory `ResolvedAliases` list. `ContractMcpServerTool` performs the parsed-argument budget check first, calls this rewriter exactly once per invocation, and gives only `ToolArgumentRewrite.Arguments` to the SDK inner tool for binding. The returned object is invocation-local; its aliases/values are never serialized, logged, retained in telemetry, or cached. Literal non-alias values pass through unchanged. Child 5 performs no filesystem, network, trace, or symbol-policy call in this stage. Child 6's trace-reference and symbol resolvers consume the rewritten bound values (and may consume `ResolvedAliases` only for typed provenance), then apply exactly the same access validation used for literal input before any I/O.

The finalizer converts `CallToolResult.StructuredContent` to `JsonNode`, applies taxonomy redaction, regenerates the text block from that same node, and discards pre-redaction text. Replace every tool's `Console.Error` argument with the injected `IPrivacyLogSink.Writer`. Configure console logging through a redacting formatter and make telemetry accept only tool name, HMAC argument fingerprint, timing, counters, and finalized byte count.

- [ ] **Step 4: Run GREEN and scan**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~PrivacyRedactorTests|FullyQualifiedName~TelemetryTests"
rg -n "Console\.Error" src/WprMcp/Tools
```

Expected: tests pass and `rg` returns no matches.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Core/PrivacyRedactor.cs src/WprMcp/Core/PrivacyLogSink.cs src/WprMcp/Core/TypedAliasRegistry.cs src/WprMcp/Core/ToolArgumentRewriter.cs src/WprMcp/Core/privacy-field-taxonomy.v1.json src/WprMcp/Core/McpTelemetryFilters.cs src/WprMcp/Core/ToolTelemetry.cs src/WprMcp/Program.cs src/WprMcp/Tools/AlpcTools.cs src/WprMcp/Tools/ClrTools.cs src/WprMcp/Tools/CpuTools.cs src/WprMcp/Tools/DiagnoseTools.cs src/WprMcp/Tools/GenericProviderTools.cs src/WprMcp/Tools/HardFaultTools.cs src/WprMcp/Tools/HeapTools.cs src/WprMcp/Tools/ImageLoadTools.cs src/WprMcp/Tools/InterruptTools.cs src/WprMcp/Tools/IoTools.cs src/WprMcp/Tools/NetIoTools.cs src/WprMcp/Tools/ReadyThreadTools.cs src/WprMcp/Tools/RegistryTools.cs src/WprMcp/Tools/VirtualMemoryTools.cs src/WprMcp/Tools/WaitTools.cs tests/WprMcp.Tests/PrivacyRedactorTests.cs tests/WprMcp.Tests/TelemetryTests.cs
git commit -m "feat(privacy): redact final results logs aliases and telemetry"
```

---

### Task 7: Enforce exact full-frame response budgets and deterministic section paging (TDD)

**Files:**
- Modify: `src/WprMcp/Core/McpBudgetPolicy.cs`
- Modify: `src/WprMcp/Core/ToolContractManifest.cs`
- Modify: `src/WprMcp/Core/tool-contracts.v2.json`
- Modify: `src/WprMcp/Core/ToolExecution.cs`
- Modify: `src/WprMcp/Program.cs`
- Modify: `src/WprMcp/Core/StackResponseOptions.cs`
- Modify: `tests/WprMcp.Tests/McpBudgetPolicyTests.cs`
- Modify: `tests/WprMcp.Tests/StackResponseOptionsTests.cs`

- [ ] **Step 1: Add exact wire-size tests**

Add:

```text
ResponseFrame_99999And100000BytesPass100001IsTrimmed
ResponseFrame_ConfiguredLowerCapMinusOneAndExactPassPlusOneIsTrimmed
ResponseFrame_MultibyteUtf8MeasuresSerializedBytes
ResponseFrame_CountsBoundedStringAndNumericRequestIdTextAndStructuredContentTogether
ResponseFloor_4095IsAlwaysRejectedBut4096StillRequiresCatalogPreflight
ResponsePreflight_MinimumIsMaxOfFixedFailureAndEveryIndivisibleSingleToolPage
ResponsePreflight_UsesMaximumLegalStringOrNumericIdCursorAndFullJsonRpcFrame
ResponsePreflight_CapOneBelowMeasuredMinimumFailsBeforeTransportReads
ResponsePreflight_CapAtMeasuredMinimumStartsAndAllActiveToolsAreReachable
ResponsePreflight_MinimumAboveHardCeilingMarksCatalogUnservable
Truncation_HappensAfterPrivacyAndOnlyAtDeclaredRowBoundaries
Truncation_PreservesTotalsStatusOrderingAndSchemaValidity
HasMore_RequiresExactTotalOrTopPlusOneProbe
TopLevelHasMore_EqualsAnySectionHasMore
RowsEqualTopWithoutProbe_DoesNotClaimHasMore
MinimumEnvelopeTooLarge_ReturnsFixedResponseTooLargeFailure
ToolsList_NeverSplitsAToolAndPaginatesEveryActiveToolWithinConfiguredCap
ToolsList_DoesNotReturnEmptyPageWithUnchangedCursor
```

Construct string request IDs at the serialized 127/128-byte boundary to prove ID length is counted; a 129-byte string ID remains covered by Task 5's bounded no-response path. Separately measure `long.MinValue`, `0`, and `long.MaxValue` numeric IDs and require exact correlation; their legal `Int64` representations are shorter than the 128-byte string and therefore cannot define the worst-case response. Use a synthetic catalog whose largest indivisible single-tool page is above 4,096 bytes to prove the static floor alone does not authorize startup, and use a transport/listener spy to prove rejection happens before the first stdin read. For both contract modes, independently measure the fixed failure and every active tool's single-tool page while including the longest canonical legal `nextCursor`, then assert the reported minimum is their exact maximum. Use a Unicode sentinel whose redacted form is shorter and assert it is redacted before fitting.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~McpBudgetPolicyTests|FullyQualifiedName~StackResponseOptionsTests"
```

Expected: existing tests serialize only domain DTOs and do not count JSON-RPC, text plus structured duplication, ID, or newline.

- [ ] **Step 3: Implement exact fitting**

`McpBudgetPolicy` exposes:

```csharp
internal sealed record ResponseBudgetPreflight(
    int FixedFailureBytes,
    IReadOnlyDictionary<string, int> SingleToolPageBytes,
    int MinimumViableResponseBytes);

internal interface IToolListCursorCodec
{
    string Encode(OutputContractMode mode, int nextIndex);
    int Decode(OutputContractMode mode, string cursor, int activeToolCount);
    string? GetLongestLegalCursor(OutputContractMode mode, int activeToolCount);
}

internal CallToolResult FitCallResult(RequestId requestId, CallToolResult redacted);
internal ListToolsResult FitToolsPage(
    RequestId requestId,
    IReadOnlyList<Tool> orderedTools,
    string? cursor);
internal int MeasureResponseFrame(RequestId requestId, object result);
internal ResponseBudgetPreflight MeasureStartupMinimum(
    IReadOnlyList<Tool> activeTools,
    OutputContractMode contractMode,
    IToolListCursorCodec cursors);
internal void ValidateStartupResponseCap(ResponseBudgetPreflight preflight);
```

`MeasureResponseFrame` first requires an already-validated request ID, serializes the exact `JsonRpcResponse` with `McpJsonUtilities.DefaultOptions`, and adds the single stdio newline byte. `FitCallResult` removes complete rows from the manifest's last pageable section until the frame is at most `options.MaxJsonRpcResponseBytes`; it updates that section's `Returned`, `HasMore`, and exact `TotalAvailable`, then recomputes top-level `HasMore`. It never truncates a string, object, frame, or partial row. If the data envelope does not fit, return the constant-message `Failed/response_too_large` envelope with empty warnings/sections. Re-measure the fallback before write and treat any impossible overflow as a bounded session failure with no partial stdout.

`MeasureStartupMinimum` runs after the selected legacy/v2 active catalog is materialized but before host transport construction. It uses the same JSON options and `MeasureResponseFrame` path as runtime and computes exactly:

```text
MinimumViableResponseBytes = max(
  every fixed public failure representation with the maximum legal
    128-byte serialized string request ID,
  for each active tool: one ListToolsResult containing that complete Tool plus the
    longest canonical legal nextCursor, measured with that same worst-case request ID)
```

`IToolListCursorCodec` accepts only canonical cursors produced for the selected contract mode and a continuation index in the active catalog; `GetLongestLegalCursor` enumerates those finite legal continuation indexes and returns the one with the largest serialized UTF-8 representation (or `null` only when the catalog has no continuation position). The single-tool probes therefore include the tool's complete name, descriptions, annotations, input schema, active output schema, the worst legal cursor when one exists, all JSON-RPC result/id/property overhead, and the stdio newline. Tool entries and schemas are indivisible. `ValidateStartupResponseCap` requires `MaxJsonRpcResponseBytes >= max(McpWireHardLimits.MinResponseBytes, preflight.MinimumViableResponseBytes)` and preserves the 100,000-byte hard ceiling. A cap one byte below the measured requirement, or an active catalog whose requirement exceeds the hard ceiling, fails with a stable configuration error before any stdin read, listener start, or initialize response. The 4,096-byte constant is only a static absolute floor; it is never treated as proof that the active catalog is pageable.

`FitToolsPage` orders tools by ordinal name and uses the shared cursor codec's opaque base64url cursor containing contract mode plus next index. It greedily appends complete `Tool` entries while the exact frame fits, never splits or truncates a tool/schema, uses a top+1 probe, advances the cursor after at least one tool, and never emits a response over the same configured frame limit. Startup preflight makes every individual active tool reachable under the configured cap, so paging cannot strand a large tool or loop on an empty page. Remove the advisory-only `StackResponseOptions.WarningResponseBytes`/`MaximumResponseBytes`; warning and hard limits now come from the validated `ToolExecutionOptions.Budgets` instance and are enforced.

Register the post-invocation filter after the input filter:

```csharp
var raw = await next(request, cancellationToken);
var redacted = privacy.Finalize(raw);
return budget.FitCallResult(request.JsonRpcRequest.Id, redacted);
```

- [ ] **Step 4: Run GREEN**

Run the focused command again. Expected: every byte-boundary and paging test passes.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Core/McpBudgetPolicy.cs src/WprMcp/Core/ToolContractManifest.cs src/WprMcp/Core/tool-contracts.v2.json src/WprMcp/Core/ToolExecution.cs src/WprMcp/Core/StackResponseOptions.cs src/WprMcp/Program.cs tests/WprMcp.Tests/McpBudgetPolicyTests.cs tests/WprMcp.Tests/StackResponseOptionsTests.cs
git commit -m "feat(mcp): enforce exact JSON-RPC budgets and section paging"
```

---

### Task 8: Prove active schemas and representative outcomes through real stdio (TDD)

**Files:**
- Create: `tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj`
- Create: `tests/WprMcp.ProtocolTests/StdioMcpClient.cs`
- Create: `tests/WprMcp.ProtocolTests/ToolSchemaContractTests.cs`
- Create: `tests/WprMcp.ProtocolTests/Snapshots/tools-list.legacy.json`
- Create: `tests/WprMcp.ProtocolTests/Snapshots/tools-list.v2.json`
- Modify: `WprMcp.sln`
- Modify: `docs/MCP_SDK_SURFACE_SPIKE.md`

- [ ] **Step 1: Create the protocol project and failing real-server tests**

`StdioMcpClient` starts `dotnet <absolute WprMcp.dll>` and exposes:

```csharp
internal sealed class StdioMcpClient : IAsyncDisposable
{
    public static Task<StdioMcpClient> StartAsync(
        string serverDll,
        IReadOnlyList<string> serverArguments,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan stepTimeout,
        CancellationToken cancellationToken = default);

    public Task<JsonObject> RequestAsync(
        string method, JsonObject? parameters, CancellationToken cancellationToken = default);

    public Task NotifyAsync(
        string method, JsonObject? parameters, CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<JsonObject>> ListAllToolsAsync(
        CancellationToken cancellationToken = default);
}
```

Add exact tests:

```text
LegacyToolsList_MatchesSnapshotAndHasNoOutputSchemas
V2ToolsList_MatchesSnapshotAndEveryToolHasEnvelopeSchema
V2EveryTool_SucceededResultValidatesAgainstAdvertisedSchema
V2RepresentativePartialFailedTruncatedAndRedactedResultsValidate
SixThreadTools_SchemasExposeSharedSelectorsAndForbidTidWithoutPidAtExecution
ToolsListPagination_ReturnsEveryToolExactlyOnce
ResponsePreflight_CapBelowMeasuredMinimumExitsBeforeInitialize
ResponsePreflight_CapAtMeasuredMinimumListsEveryToolExactlyOnce
RawStdioRequestFrame_100001BytesExits64BeforeSdkOrToolExecution
RawStdioRequestFrame_LoweredCapPlusOneExits64BeforeSdkOrToolExecution
RawStdioRequestId_129SerializedBytesExits65WithoutResponseOrSideEffects
```

The six-tool test covers `wait_analysis`, `wait_top_stacks`, `wait_caller_callee`, `cpu_precise_analysis`, `cpu_top_functions`, and `cpu_caller_callee`. Use an intentionally missing trace only after verifying `tid` without `pid`, so the expected code proves validation ran before access. The response-preflight tests construct the production active catalog, obtain its measured minimum through the production `MeasureStartupMinimum` path, then start the real process at minimum-minus-one and exact-minimum caps: minus one must exit before accepting `initialize`, while exact minimum must initialize and page every active tool exactly once without splitting a schema or exceeding the cap. For raw-frame tests, write a default 100,001-byte request and a configured-lower-cap plus-one request directly to stdin without using the client serializer; assert exit code 64, empty stdout, one fixed sentinel-free stderr line, and zero fake tool/trace invocations. Send a valid-size frame with a 129-byte serialized ID and assert the distinct bounded-ID exit 65 with the same no-response/no-side-effect guarantee. Child 9 expands these representative boundaries into its hostile-input matrix.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter FullyQualifiedName~ToolSchemaContractTests
```

Expected: project/snapshot failures until the real server, normalization, and schema validation are wired.

- [ ] **Step 3: Complete the real schema proof**

Load the protocol revision/profile from Child 11A's `eng/SelectedPlatform.props`. For a selected stateful flow, initialize, send `notifications/initialized`, and continue in that session; for a selected stateless-discovery flow, use only the discovery/request-metadata sequence proven by the ADR. In either flow, follow every `nextCursor` and validate actual `tools/call` structured JSON with the output schema returned for that tool. Normalize only description whitespace and cursor tokens in snapshots; preserve names, annotations, input schemas, output schemas, nullability, required sets, `additionalProperties`, and section names.

Update `docs/MCP_SDK_SURFACE_SPIKE.md` with the exact selected SDK version, real commands, and measured results. Record only results produced by the command; do not pre-write a passing count or claim a protocol behavior that the test did not observe.

- [ ] **Step 4: Run GREEN and full acceptance**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter FullyQualifiedName~ToolSchemaContractTests
dotnet test WprMcp.sln -c Release
```

Expected: all schema tests and the full solution pass.

- [ ] **Step 5: Commit**

```powershell
git add tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj tests/WprMcp.ProtocolTests/StdioMcpClient.cs tests/WprMcp.ProtocolTests/ToolSchemaContractTests.cs tests/WprMcp.ProtocolTests/Snapshots/tools-list.legacy.json tests/WprMcp.ProtocolTests/Snapshots/tools-list.v2.json WprMcp.sln docs/MCP_SDK_SURFACE_SPIKE.md
git commit -m "test(mcp): validate active contracts through real tools list and calls"
```

---

## Acceptance commands

Run from `D:\wpa-mcp` after all eight commits:

```powershell
dotnet restore WprMcp.sln --locked-mode
dotnet build WprMcp.sln -c Release --no-restore -warnaserror
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --no-build
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --no-build --filter FullyQualifiedName~ToolSchemaContractTests
rg -n "Console\.Error" src/WprMcp/Tools
rg -n "4\.20\.\*|Version=\"[^\"]*[\*\[,)]" --glob "*.csproj" .
git diff --check
```

Expected: restore/build/tests succeed; both `rg` commands return no matches; `git diff --check` reports nothing.

Also run the exact response boundary suite explicitly:

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~McpBudgetPolicyTests|FullyQualifiedName~PrivacyRedactorTests|FullyQualifiedName~BatchContractTests|FullyQualifiedName~CapabilityQualityContractTests"
```

---

## Dependencies and conflict ownership

- Child 11A must pin the TFM, SDK, MCP SDK, and protocol before Task 2 generates schema snapshots.
- Child 1 owns `ThreadSelector` and instance-resolution semantics. This plan may replace thrown exception types with `ToolContractException` but must not alter matching rules.
- Child 2 owns computations and parameter addition for the six thread tools. Merge Child 2 first; then Task 2 wraps its final signatures. Resolve `CpuTools.cs`, `WaitTools.cs`, and `Records.cs` conflicts in that order.
- Children 3 and 4 may also edit `Records.cs`, `MetaTools.cs`, or `DiagnoseTools.cs`; land their domain changes before the MCP wrapper migration, then regenerate the manifest and real snapshots once.
- Child 6 owns access-policy and trace/symbol alias revalidation. Task 6 calls its public policy; it must not duplicate path-containment or symbol-host checks.
- This plan owns the configured public stdio request-frame cap before SDK deserialization (100,000-byte default/hard ceiling), the 128-byte serialized request-ID boundary, the 16 KiB default parsed tool-argument cap before binding, and configured final public response frames. Child 8 owns separate parent/worker IPC frames, cancellation/deadline propagation, and worker quotas; it must preserve these public transport boundaries.
- Child 9 expands `tests/WprMcp.ProtocolTests/StdioMcpClient.cs`; it must modify, not fork, the harness created here.
- Child 11B owns the post-gate default switch from compatibility-stage legacy output to secure-default v2 output, including a real default-profile schema snapshot and release note. This plan must not switch early or remove the explicit legacy flag.
- Any change to `tool-contracts.v2.json`, public DTO fields, enum strings, stable error codes, or schema snapshots requires an explicit contract-version note. Do not silently regenerate snapshots.

## Final evidence checklist

- All real v2 tools have one schema-valid succeeded call; representative partial, failed, privacy-redacted, and truncated calls also validate.
- The six thread tools advertise the same selector fields and return exact `invalid_argument`, `thread_instance_not_found`, or `ambiguous_thread_instance` codes without conflating absent symbols.
- Batch requested/succeeded/failed/skipped sets are pairwise disjoint and exhaustive.
- CPU-only stack data cannot make FileIO, CLR, registry, or network coverage available.
- No CSwitch, no domain stackwalk, and unresolved symbols are distinct thread-quality states.
- The complete JSON-RPC frame never exceeds the configured response cap (100,000-byte default/hard ceiling), and the configured warning threshold changes no completion status. Before stdin opens, the measured `MinimumViableResponseBytes` is the maximum of the fixed failure and every active tool's indivisible one-tool `tools/list` page with maximum legal 128-byte request ID, longest canonical cursor, full JSON-RPC overhead, and newline; the configured cap is at least `max(4,096, MinimumViableResponseBytes)`, so every active tool is reachable by paging.
- A public stdio request is rejected at configured cap plus one before SDK deserialization; a serialized request ID is rejected at byte 129 before execution; and parsed `arguments` independently reject their configured cap plus one before binding or trace access.
- Privacy sentinels occur nowhere in stdout, stderr, warnings, errors, progress, symbol paths, or telemetry for the applicable mode.
- The implementation log contains only commands actually run and outputs actually observed.
