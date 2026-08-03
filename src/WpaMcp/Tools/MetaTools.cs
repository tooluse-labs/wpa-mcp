using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Diagnostics.Tracing.Etlx;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class MetaTools
{
    private const long MinCpuUsForWaitRatioSort = 5_000;
    private const double MinCpuShareForWaitRatioSort = 0.00001;
    private const int InspectCapabilityPageSize = 16;
    private const int InspectWorkflowPageSize = 8;

    private readonly TraceCache _cache;
    private readonly TraceToolRuntime? _traceRuntime;
    private readonly ActiveToolCatalog? _catalog;
    private readonly QueryResultCursorCoordinator _queryResults;
    private static readonly Lazy<ActiveToolCatalog> DirectCatalog = new(
        static () => ActiveToolCatalog.LoadAndValidate(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public MetaTools(TraceCache cache)
    {
        _cache = cache;
        _queryResults = DirectQueryResults();
    }

    public MetaTools(TraceCache cache, TraceToolRuntime traceRuntime)
    {
        _cache = cache;
        _traceRuntime = traceRuntime;
        _queryResults = DirectQueryResults();
    }

    public MetaTools(
        TraceCache cache,
        CapabilityDiscoveryRuntime capabilityDiscovery)
    {
        _cache = cache;
        _catalog = capabilityDiscovery.Catalog;
        _queryResults = capabilityDiscovery.QueryResults;
    }

    public MetaTools(
        TraceCache cache,
        TraceToolRuntime traceRuntime,
        CapabilityDiscoveryRuntime capabilityDiscovery)
    {
        _cache = cache;
        _traceRuntime = traceRuntime;
        _catalog = capabilityDiscovery.Catalog;
        _queryResults = capabilityDiscovery.QueryResults;
    }

    private static QueryResultCursorCoordinator DirectQueryResults() =>
        new($"direct_{Guid.NewGuid():N}", "off");

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Loads an allowed local Windows ETW .etl/.etlx source into the server-owned immutable artifact store and returns a canonical principal-scoped TraceId. " +
        "This is the only raw trace-source entry point. It rejects UNC/device/ADS/reparse paths before artifact writes, snapshots an opened handle, and converts only inside the owned store. " +
        "Repeated loads of the same unchanged generation return the same TraceId. Set forceRefresh=true after a deliberate in-place rewrite that preserved file identity, length, and timestamps. " +
        "Response includes symbol-server recommendations based on the modules referenced by the trace. " +
        "No startUs/endUs: this is whole-trace cache/orientation, not event-window analysis.")]
    public LoadTraceResponse LoadTrace(
        [Description("Absolute local .etl/.etlx path under a configured trace root. Raw paths are accepted only by load_trace.")] string tracePath,
        [Description("Force a new secure source snapshot instead of trusting the cached file-identity/length/timestamp observation. Default false.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (_traceRuntime is not null)
        {
            var loaded = _traceRuntime.Load(tracePath, forceRefresh, cancellationToken);
            try
            {
                using var registryLease = _traceRuntime.Acquire(
                    loaded.Handle.TraceId,
                    cancellationToken);
                var productionFacts = registryLease.GetFacts(cancellationToken);
                return new LoadTraceResponse(
                    BuildTraceMeta(loaded.Handle.TraceId, productionFacts),
                    BuildSymbolStatus(productionFacts.PdbIdentities),
                    productionFacts.Capabilities,
                    TraceId: loaded.Handle.TraceId,
                    TraceRefKind: "canonical",
                    ReusedExisting: loaded.Handle.ReusedExisting,
                    Persistence: "persistent",
                    SourceGenerationAssurance: loaded.SourceValidation switch
                    {
                        TraceSourceValidationEvidence.OpenedHandleSnapshotContentHash =>
                            "opened_handle_snapshot_content_hash_verified",
                        TraceSourceValidationEvidence.CachedFileIdentityLengthAndTimestamps =>
                            "cached_file_identity_length_timestamps",
                        _ => "unavailable",
                    },
                    ForceRefreshApplied: loaded.ForceRefreshApplied,
                    ArtifactRetention: "independent_retention_policy",
                    Warnings: []);
            }
            catch
            {
                // A newly published handle must not survive a load call that never
                // produced its generation facts response. A reused handle belongs
                // to an earlier successful call and must remain valid.
                if (!loaded.Handle.ReusedExisting)
                    _traceRuntime.Unload(loaded.Handle.TraceId);
                throw;
            }
        }

        // Direct analyzer tests retain the old constructor as a non-production seam.
        using var traceLease = _cache.Acquire(tracePath);
        var directFacts = traceLease.GetFacts(cancellationToken);
        return new LoadTraceResponse(
            BuildTraceMeta(tracePath, directFacts),
            BuildSymbolStatus(directFacts.PdbIdentities),
            directFacts.Capabilities);
    }

    [McpServerTool(
        ReadOnly = false,
        Idempotent = true,
        OpenWorld = false,
        Destructive = true,
        UseStructuredContent = true), Description(
        "Retires one canonical TraceId for this stdio session, rejects new acquisitions, and optionally waits for active query leases to drain. " +
        "Repeated calls are idempotent and return already_unloaded. This operation never accepts a raw path and does not delete the immutable ETLX artifact; artifact retention is governed independently. " +
        "No startUs/endUs: lifecycle retirement applies to the complete loaded generation.")]
    public UnloadTraceResponse UnloadTrace(
        [Description("Canonical TraceId returned by load_trace (trc_ plus 32 lowercase hexadecimal digits)")] string traceId,
        [Description("Wait up to 30 seconds for active analysis leases to drain; false returns pending immediately.")] bool waitForDrain = true,
        CancellationToken cancellationToken = default)
    {
        if (_traceRuntime is null)
        {
            throw new InvalidOperationException(
                "unload_trace requires the production TraceId lifecycle runtime.");
        }

        var result = _traceRuntime.Unload(traceId);
        var drainStatus = result.DrainTask.IsCompleted
            ? "drained"
            : "pending";
        if (waitForDrain && !result.DrainTask.IsCompleted)
        {
            try
            {
                result.DrainTask.WaitAsync(
                        TimeSpan.FromSeconds(30),
                        cancellationToken)
                    .GetAwaiter().GetResult();
                drainStatus = "drained";
            }
            catch (TimeoutException)
            {
                drainStatus = "timed_out";
            }
        }

        var lifecycleStatus = result.Status switch
        {
            TraceHandleUnloadStatus.Unloaded => "unloaded",
            TraceHandleUnloadStatus.AlreadyUnloaded => "already_unloaded",
            TraceHandleUnloadStatus.Expired => "expired",
            _ => "unknown",
        };
        return new UnloadTraceResponse(
            traceId,
            lifecycleStatus,
            drainStatus,
            result.ActiveLeases,
            Idempotent: true,
            ArtifactDisposition: "retained_by_independent_policy",
            Warnings:
            [
                "Active queries keep their generation lease until completion.",
                "Trace handle retirement does not prove artifact deletion.",
            ]);
    }

    [McpServerTool(
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true), Description(
        "Inspects a trace once and returns machine-readable orientation: capture capabilities, " +
        "system metadata, provider counts, stackwalk completeness, trace-native PDB identity quality, quality " +
        "warnings, and capability-driven next-tool hints. Use when the capture profile is unknown, " +
        "the analysis goal is unclear, or prior domain tools returned empty/low-confidence results. " +
        "PlannerExecution reports this call's approved trace-facts operation, snapshot reuse mode, physical-pass participation, scan-count basis, phase durations, and budget termination; it never presents generation-history timing as current-call work. " +
        "Stack recommendations require attached stacks in that exact event domain; unrelated global " +
        "stacks never enable them. The AnalysisContract object supplies machine-readable rules for scope, " +
        "counts, empty results, symbols, thread replay, and causal restraint. " +
        "inspect_trace does not read _NT_SYMBOL_PATH, probe local PDB candidates, or run frame lookup; use prepare_symbols " +
        "for startup-policy-approved immutable local readiness. " +
        "Recommendations are capability-driven hints, not goal-specific rankings. " +
        "Analysis reads an already loaded immutable generation by canonical traceId and never converts a caller path. " +
        "domain/goal filters and qrc_ cursor pages are bound to the principal, immutable trace generation, catalog, privacy profile, normalized query, and stable capability-then-workflow ordering. " +
        "Only the first page carries the large orientation blocks; every continuation retains the trace evidence boundaries and declares evidence_continuation explicitly. " +
        "No startUs/endUs: capabilities, metadata, provider counts, and PDB identity/configuration describe the whole trace.")]
    public InspectTraceResponse InspectTrace(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Optional lowercase capability domain filter. Cursor continuations must repeat the same normalized filter.")] string? domain = null,
        [Description("Optional lowercase catalog goal filter. Cursor continuations must repeat the same normalized filter.")] string? goal = null,
        [Description("Opaque qrc_ continuation returned by a prior inspect_trace page. It is bound to the principal and immutable trace generation.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        using var traceLease = _cache.Acquire(traceId);
        var catalog = _catalog ?? DirectCatalog.Value;
        var normalizedDomain = QueryResultCursorCoordinator.NormalizeFilter(domain, nameof(domain));
        var normalizedGoal = QueryResultCursorCoordinator.NormalizeFilter(goal, nameof(goal));
        var traceGenerationId = BuildTraceGenerationId(traceLease.GenerationIdentity);
        var publicTraceId = TraceQueryExecutionContext.CurrentReference?.TraceId ?? traceId;
        var pagePosition = _queryResults.ResolveInspectTrace(
            publicTraceId,
            traceGenerationId,
            catalog.CatalogVersion,
            normalizedDomain,
            normalizedGoal,
            cursor);
        var orientationIncluded = cursor is null;
        var planned = new QueryPlanner(catalog).ExecuteTraceFacts(
            traceLease,
            "inspect_trace",
            facts =>
            {
                var capabilities = facts.Capabilities;
                var metadata = facts.Metadata;
                var symbolQuality = BuildInspectSymbolQuality(facts.PdbIdentities);
                var warnings = BuildTraceQualityWarnings(
                    facts.CaptureIntegrity.ReportedEventsLost,
                    capabilities,
                    symbolQuality);
                var evidenceMap = TraceEvidenceMapBuilder.Build(catalog, facts, symbolQuality);
                var filteredCapabilityIds = catalog.Capabilities
                    .Where(capability =>
                        (normalizedDomain is null || string.Equals(
                            capability.Domain,
                            normalizedDomain,
                            StringComparison.Ordinal)) &&
                        (normalizedGoal is null || capability.GoalIds.Contains(
                            normalizedGoal,
                            StringComparer.Ordinal)))
                    .Select(capability => capability.CapabilityId)
                    .ToHashSet(StringComparer.Ordinal);
                var filteredCapabilities = evidenceMap.Capabilities
                    .Where(capability => filteredCapabilityIds.Contains(capability.CapabilityId))
                    .ToArray();
                var filteredWorkflowIds = catalog.Workflows
                    .Where(workflow => workflow.CapabilityIds.Any(filteredCapabilityIds.Contains))
                    .Select(workflow => workflow.WorkflowId)
                    .ToHashSet(StringComparer.Ordinal);
                var filteredWorkflows = evidenceMap.Workflows
                    .Where(workflow => filteredWorkflowIds.Contains(workflow.WorkflowId))
                    .ToArray();
                var filteredMap = evidenceMap with
                {
                    Filter = new TraceEvidenceMapFilter(normalizedDomain, normalizedGoal),
                    CatalogCapabilityCount = catalog.Capabilities.Count,
                    TotalCapabilities = filteredCapabilities.Length,
                    ReturnedCapabilities = filteredCapabilities.Length,
                    CatalogWorkflowCount = catalog.Workflows.Count,
                    TotalWorkflows = filteredWorkflows.Length,
                    ReturnedWorkflows = filteredWorkflows.Length,
                    Capabilities = filteredCapabilities,
                    Workflows = filteredWorkflows,
                };
                var (pageCapabilities, pageWorkflows, hasMore) = SelectInspectPage(
                    pagePosition,
                    filteredCapabilities,
                    filteredWorkflows);
                var pageMap = filteredMap with
                {
                    ReturnedCapabilities = pageCapabilities.Length,
                    ReturnedWorkflows = pageWorkflows.Length,
                    Capabilities = pageCapabilities,
                    Workflows = pageWorkflows,
                };
                var orientationTools = BuildCatalogOrientationProjection(catalog);
                var capabilitySupportedTools = BuildCatalogToolProjection(catalog, filteredMap);
                var enabledCapabilities = filteredCapabilities
                    .Where(capability => capability.TraceStatus is
                        ToolCapabilityStatus.Available or ToolCapabilityStatus.Partial)
                    .Select(capability => capability.CapabilityId)
                    .ToArray();
                var recommendedFlows = filteredWorkflows
                    .Select(workflow => workflow.WorkflowId)
                    .ToArray();

                return new InspectTraceResponse(
                    BuildTraceMeta(traceId, facts),
                    capabilities,
                    orientationIncluded ? metadata : null,
                    orientationIncluded ? symbolQuality : null,
                    orientationIncluded ? warnings : [],
                    orientationIncluded ? orientationTools : [],
                    orientationIncluded ? capabilitySupportedTools : [],
                    orientationIncluded ? enabledCapabilities : [],
                    orientationIncluded ? recommendedFlows : [],
                    new InspectTracePageContext(
                        pagePosition.Phase,
                        pagePosition.Index,
                        orientationIncluded ? "full_orientation" : "evidence_continuation",
                        orientationIncluded,
                        traceGenerationId,
                        QueryResultCursorCoordinator.InspectOrdering,
                        normalizedDomain,
                        normalizedGoal),
                    hasMore,
                    hasMore ? QueryResultCursorRegistry.PendingDeliveryToken : null,
                    orientationIncluded ? BuildAnalysisContractGuidance() : null,
                    pageMap,
                    LegacyProjectionState: "deprecated_id_only_derived_projection");
            },
            cancellationToken);
        return planned.Value with { PlannerExecution = planned.Telemetry };
    }

    private static (
        TraceCapabilityEvidenceRecord[] Capabilities,
        TraceWorkflowEvidenceRecord[] Workflows,
        bool HasMore) SelectInspectPage(
        QueryResultCursorPosition position,
        IReadOnlyList<TraceCapabilityEvidenceRecord> capabilities,
        IReadOnlyList<TraceWorkflowEvidenceRecord> workflows)
    {
        if (position.Phase == "capabilities")
        {
            if (position.Index < 0 || position.Index >= capabilities.Count && capabilities.Count > 0)
                throw InvalidInspectCursorPosition();
            if (capabilities.Count == 0)
            {
                if (position.Index != 0 || workflows.Count != 0)
                    throw InvalidInspectCursorPosition();
                return ([], [], false);
            }
            var page = capabilities.Skip(position.Index)
                .Take(InspectCapabilityPageSize)
                .ToArray();
            return (
                page,
                [],
                position.Index + page.Length < capabilities.Count || workflows.Count > 0);
        }

        if (position.Phase == "workflows")
        {
            if (position.Index < 0 || position.Index >= workflows.Count)
                throw InvalidInspectCursorPosition();
            var page = workflows.Skip(position.Index)
                .Take(InspectWorkflowPageSize)
                .ToArray();
            return (
                [],
                page,
                position.Index + page.Length < workflows.Count);
        }

        throw InvalidInspectCursorPosition();
    }

    private static QueryResultCursorException InvalidInspectCursorPosition() =>
        new(
            QueryResultCursorFailureKind.Invalid,
            "The inspect_trace cursor position is outside its bound result set.");

    private static string BuildTraceGenerationId(
        TraceCache.GenerationIdentity generation)
    {
        var stamp = generation.Stamp;
        var material = string.Join(
            "\n",
            generation.Sequence.ToString(CultureInfo.InvariantCulture),
            stamp.CanonicalPath,
            stamp.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            stamp.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            stamp.Length.ToString(CultureInfo.InvariantCulture),
            stamp.VolumeSerialNumber?.ToString(CultureInfo.InvariantCulture) ?? "-",
            stamp.FileId?.ToString(CultureInfo.InvariantCulture) ?? "-");
        return "tgen_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32].ToLowerInvariant();
    }

    private static IReadOnlyList<string> BuildCatalogOrientationProjection(
        ActiveToolCatalog catalog) => catalog.Tools
        .Where(tool => tool.DiscoveryPriority == 0 ||
            tool.SideEffects.Contains("symbol_context_preparation", StringComparer.Ordinal))
        .Select(tool => tool.ToolName)
        .ToArray();

    private static IReadOnlyList<string> BuildCatalogToolProjection(
        ActiveToolCatalog catalog,
        TraceEvidenceMapRecord map)
    {
        var enabled = map.Capabilities.Where(capability => capability.TraceStatus is
                ToolCapabilityStatus.Available or ToolCapabilityStatus.Partial)
            .Select(capability => capability.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);
        return catalog.Tools.Where(tool => tool.DiscoveryPriority != 0 &&
                !tool.SideEffects.Contains("symbol_context_preparation", StringComparer.Ordinal) &&
                tool.Capabilities.Any(capability => enabled.Contains(capability.CapabilityId)))
            .Select(tool => tool.ToolName)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildCatalogWorkflowProjection(
        TraceEvidenceMapRecord map) => map.Workflows
        .Select(workflow => workflow.WorkflowId)
        .ToArray();

    private static AnalysisContractGuidance BuildAnalysisContractGuidance() =>
        new(
            ScopeRule:
                "Only ScopeStatus=ok authorizes attribution. single_process is exact; " +
                "pid_aggregate intentionally combines IncludedProcesses while retaining their instance keys; " +
                "rows and totals may aggregate across them according to each tool's accounting contract. " +
                "Exact-only tools return failed error.code=process_start_required for a clean multi-lifetime PID; " +
                "error.code=ambiguous_process_instance is reserved for unsafe lifetime evidence.",
            TraceScopedRule:
                "Trace* is whole-trace diagnostic evidence; Scoped* uses the selected identity and " +
                "requested [startUs,endUs) window. Never use Trace* as a scoped denominator.",
            CountRule:
                "MatchedEventCount is scoped raw source events/endpoints; MatchedIntervalCount is " +
                "completed projected intervals; Rows can be aggregated or top-N truncated.",
            CapabilityRule:
                "not_observed requires absence trace-wide under the tool's documented predicate; " +
                "observed requires attributable scoped evidence; otherwise unknown.",
            StackRule:
                "Use event-domain StackCoverage. Unrelated global stacks do not enable a stack tool, " +
                "and ?!? is synthetic unknown evidence rather than a captured call chain.",
            SymbolRule:
                "PDB identity and verified local readiness are not function-name resolution. " +
                "Only an explicit same-generation SymbolContextId may authorize future frame lookup; this build reports lookup unavailable rather than falling back implicitly.",
            ThreadReplayRule:
                "Replay exact CPU/Wait thread rows with pid + processStartUs + tid + threadStartUs + " +
                "threadGeneration; generation disambiguates equal inferred start times.",
            CausalityRule:
                "Associated/readier stacks and heuristic security matches support hypotheses but do " +
                "not independently prove an unblocker, scanner identity, root cause, or causality.",
            ScopeFailureErrors: new ScopeFailureErrorGuidance(),
            NoDataReasons: new NoDataReasonGuidance());

    private static TraceMeta BuildTraceMeta(
        string traceId,
        TraceFactsSnapshot facts)
    {
        var publicReference = TraceQueryExecutionContext.CurrentReference?.TraceId ?? traceId;
        return new TraceMeta(
            TraceId: publicReference,
            DurationUs: facts.DurationUs,
            EventCount: facts.LogicalEventCount,
            EventsLost: facts.CaptureIntegrity.ReportedEventsLost,
            ProcessCount: facts.Processes.Count,
            EventCountRepresentation: facts.Provenance.EventCountRepresentation);
    }

    private static SymbolStatus BuildSymbolStatus(
        IReadOnlyList<TracePdbIdentityFact> modules)
    {
        var modulesWithPdbName = modules.Count(module =>
            !string.IsNullOrWhiteSpace(Path.GetFileName(module.PdbName)));
        var modulesWithCompletePdbIdentity = modules.Count(module =>
            !string.IsNullOrWhiteSpace(Path.GetFileName(module.PdbName)) &&
            module.PdbSignature != Guid.Empty &&
            module.PdbAge > 0);
        return new SymbolStatus(
            modules.Count,
            modulesWithPdbName,
            modulesWithCompletePdbIdentity,
            modules.Count == 0
                ? null
                : modulesWithCompletePdbIdentity / (double)modules.Count,
            LocalReadinessMeasurementState: "unmeasured",
            FrameResolutionMeasurementState: "unmeasured",
            NextStep: "prepare_symbols",
            EvidenceBoundaries:
            [
                "pdb_identity_is_trace_metadata_not_symbol_resolution",
                "local_readiness_unmeasured_without_symbol_context",
                "frame_resolution_unmeasured",
            ]);
    }

    private static InspectSymbolQuality BuildInspectSymbolQuality(
        IReadOnlyList<TracePdbIdentityFact> modules)
    {
        var modulesMissingPdbName = modules
            .Where(module => string.IsNullOrWhiteSpace(module.PdbName))
            .OrderBy(module => module.ModuleName, StringComparer.OrdinalIgnoreCase)
            .Select(module =>
            {
                var name = string.IsNullOrWhiteSpace(module.ModuleName)
                    ? "<unknown>"
                    : module.ModuleName;
                return new InspectModuleMissingPdbName(
                    name,
                    "Recapture or merge the ETL on the collection machine so the PDB name, GUID, and Age identity is recorded before choosing a symbol server.");
            })
            .Take(20)
            .ToList();

        var modulesWithPdbName = modules.Count(module =>
            !string.IsNullOrWhiteSpace(module.PdbName));
        var modulesWithCompletePdbIdentity = modules.Count(module =>
            !string.IsNullOrWhiteSpace(module.PdbName) &&
            module.PdbSignature != Guid.Empty &&
            module.PdbAge > 0);
        double? pdbNameRate = modules.Count == 0
            ? null
            : modulesWithPdbName / (double)modules.Count;
        double? completeIdentityRate = modules.Count == 0
            ? null
            : modulesWithCompletePdbIdentity / (double)modules.Count;

        return new InspectSymbolQuality(
            ModuleCount: modules.Count,
            ResolvedModuleCount: null,
            ModuleResolutionRate: null,
            TopUnresolvedModules: Array.Empty<InspectUnresolvedModule>(),
            ModulesWithPdbName: modulesWithPdbName,
            ModulesWithPdbNameRate: pdbNameRate,
            ModulesWithCompletePdbIdentity: modulesWithCompletePdbIdentity,
            CompletePdbIdentityRate: completeIdentityRate,
            FrameResolutionMeasurementState: "not_measured",
            FrameResolution: null)
        {
            TopModulesMissingPdbName = modulesMissingPdbName,
        };
    }

    internal static IReadOnlyList<TraceQualityWarning> BuildTraceQualityWarnings(
        long eventsLost,
        TraceCapabilities capabilities,
        InspectSymbolQuality symbolQuality)
    {
        var warnings = new List<TraceQualityWarning>();

        if (eventsLost > 0)
        {
            warnings.Add(new TraceQualityWarning(
                Code: "events_lost",
                Severity: "warn",
                Message: $"{eventsLost} events were lost during capture; analysis may be incomplete.",
                NextStep: "Recapture with larger buffers or a narrower profile/window before drawing final conclusions.",
                AffectedTools: Array.Empty<string>(),
                DegradedTools: Array.Empty<string>()));
        }

        if (symbolQuality.CompletePdbIdentityRate is < 0.8)
        {
            warnings.Add(new TraceQualityWarning(
                Code: "low_module_pdb_identity_coverage",
                Severity: "warn",
                Message: $"{symbolQuality.CompletePdbIdentityRate.Value * 100:F1}% of loaded modules carry a complete PDB name + GUID + Age identity; this is metadata coverage, not measured frame resolution.",
                NextStep: "Recapture or merge the ETL on the collection machine to preserve complete PDB identities; then call prepare_symbols to measure approved immutable local readiness.",
                AffectedTools: StackDependentToolNames,
                DegradedTools: Array.Empty<string>()));
        }

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasCpuSamples,
            "missing_cpu_samples",
            "warn",
            "CPU sample events were not observed.",
            "Recapture with a CPU or CPU.light WPR profile when CPU attribution matters.",
            CpuSampleToolNames,
            CompositeToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasCSwitch,
            "missing_context_switches",
            "warn",
            "Context switch events were not observed.",
            "Recapture with CSwitch enabled before using wait, blocked-time, or ready-thread analysis.",
            ContextSwitchToolNames.Concat(HighWaitCompositeToolNames).ToArray(),
            CompositeToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasStackWalks,
            "missing_stackwalks",
            "warn",
            "Neither explicit StackWalk records nor attached event stacks were observed anywhere in the trace; this aggregate warning does not substitute for per-domain coverage.",
            "Recapture with stack walking enabled for the relevant events.",
            StackDependentToolNames,
            StackWalkDegradedCompositeToolNames);

        AddMissingCapabilityWarning(
            warnings,
            !capabilities.HasCSwitch || capabilities.HasCSwitchStacks,
            "missing_cswitch_stacks",
            "warn",
            "CSwitch events were observed, but they did not carry call stacks.",
            "Recapture with stack walking enabled for CSwitch before using wait stack analysis.",
            WaitStackToolNames,
            HighWaitCompositeToolNames);

        AddMissingCapabilityWarning(
            warnings,
            !capabilities.HasReadyThread || capabilities.HasReadyThreadStacks,
            "missing_ready_thread_stacks",
            "warn",
            "ReadyThread events were observed, but they did not carry call stacks.",
            "Recapture with stack walking enabled for ReadyThread before using ready-thread stack analysis.",
            ReadyThreadStackToolNames,
            HighWaitCompositeToolNames);

        AddMissingCapabilityWarning(
            warnings,
            !capabilities.HasInterrupt || capabilities.HasInterruptStacks,
            "missing_interrupt_stacks",
            "warn",
            "DPC/ISR interrupt events were observed, but they did not carry call stacks.",
            "Recapture with stack walking enabled for PerfInfoDPC and PerfInfoISR before using interrupt stack analysis.",
            InterruptStackToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasFileIo,
            "missing_file_io",
            "info",
            "File IO events were not observed.",
            "Recapture with the FileIO keyword if file activity is part of the investigation.",
            FileIoToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasDiskIo,
            "missing_disk_io",
            "info",
            "Physical disk IO events were not observed.",
            "Recapture with the DiskIO keyword if physical media activity is part of the investigation.",
            DiskIoToolNames);

        AddMissingCapabilityWarning(
            warnings,
            HasAnyClrCapability(capabilities),
            "missing_clr_runtime",
            "info",
            "No supported CLR JIT, GC, allocation, exception, contention, or finalizer source events were recognized; this does not prove that the CLR provider emitted no other events.",
            "If these CLR analyses matter, verify the requested Microsoft-Windows-DotNETRuntime event families and keywords as well as parser support before recapturing. Use tests/WpaMcp.Tests/fixtures/JitOnlyCapture.wprp for minimal JIT-only traces.",
            ClrToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasNetIo,
            "missing_network_io",
            "info",
            "Network send/receive byte events were not observed.",
            "Recapture with NetworkTrace enabled if network byte attribution is part of the investigation.",
            NetworkByteToolNames);

        AddMissingCapabilityWarning(
            warnings,
            HasNetworkConnectionLifecycle(capabilities),
            "missing_network_connections",
            "info",
            "Network connection lifecycle events were not observed.",
            "Recapture with NetworkTrace enabled if TCP connect/accept/disconnect timing is part of the investigation.",
            NetworkConnectionToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasMemoryProcessInfo,
            "missing_memory_process_info",
            "info",
            "Process memory resource snapshots were not observed.",
            "Recapture with MemoryInfoWS enabled, for example tests/WpaMcp.Tests/fixtures/MemoryCapture.wprp.",
            MemoryResourceToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasMemorySystemInfo,
            "missing_memory_system_info",
            "info",
            "System-wide memory resource snapshots were not observed.",
            "Recapture with system memory counters enabled when available/free/commit pressure matters.",
            MemoryResourceToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasHandleEvents,
            "missing_handle_events",
            "info",
            "Handle create/close events were not observed.",
            "Recapture with the Handle keyword enabled when handle-leak analysis matters.",
            MemoryResourceToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasPoolEvents,
            "missing_pool_events",
            "info",
            "Pool allocation/free events were not observed.",
            "Recapture with the Pool keyword enabled when observed paged/nonpaged pool deltas matter.",
            MemoryResourceToolNames);

        return warnings;
    }

    private static void AddMissingCapabilityWarning(
        List<TraceQualityWarning> warnings,
        bool hasCapability,
        string code,
        string severity,
        string message,
        string nextStep,
        IReadOnlyList<string> affectedTools,
        IReadOnlyList<string>? degradedTools = null)
    {
        if (hasCapability) return;
        warnings.Add(new TraceQualityWarning(
            code,
            severity,
            message,
            nextStep,
            affectedTools,
            degradedTools ?? Array.Empty<string>()));
    }

    internal static IReadOnlyList<ToolRecommendation> BuildOrientationTools(InspectSymbolQuality symbolQuality)
    {
        var recommendations = new List<(string ToolName, string Reason, string[] Goals)>
        {
            (
                "list_processes",
                "Orient on process CPU, wall time, wait ratio, and trace residency when the target process or domain is not already known.",
                ["orientation"]),
        };

        if (symbolQuality.ModulesWithCompletePdbIdentity > 0)
        {
            recommendations.Add((
                "prepare_symbols",
                "The trace contains complete PDB identities. Prepare an explicit immutable context to measure startup-policy-approved local readiness; this does not itself measure frame-name resolution.",
                ["symbols", "quality"]));
        }

        return BuildToolRecommendationRecords(recommendations);
    }

    internal static IReadOnlyList<ToolRecommendation> BuildCapabilitySupportedTools(TraceCapabilities capabilities)
    {
        var recommendations = new List<(string ToolName, string Reason, string[] Goals)>();

        if (capabilities.ObservedProcessStartEventCount > 0)
        {
            recommendations.Add((
                "process_create_timing",
                "Observed ProcessStart events are present; inspect child-start timing for one exact parent process lifetime. ImageLoad evidence is optional and may be absent.",
                ["startup", "process_creation"]));
        }

        AddStackRecommendation(
            capabilities.HasCpuSamples,
            "cpu",
            "cpu_top_functions",
            "CPU samples with attached stacks are present; rank hot functions first for CPU-bound investigations.",
            ["cpu"]);

        if (capabilities.HasCSwitch)
        {
            recommendations.Add(("cpu_precise_analysis", capabilities.HasReadyThread
                ? "Context switch and ReadyThread events are present; compute exact on-CPU time, ready latency, and core attribution."
                : "Context switch events are present; compute exact on-CPU time and core attribution. Ready latency remains unavailable without ReadyThread events.", ["cpu", "scheduler"]));
            recommendations.Add(("wait_analysis", "Context switch events are present; identify blocked threads and dominant wait reasons.", ["wait"]));
        }

        AddStackRecommendation(
            capabilities.HasCSwitch,
            "cswitch",
            "wait_top_stacks",
            "Context switches with blocking stacks are present; inspect the switch-out call chains associated with blocked intervals.",
            ["wait", "stacks"],
            legacyHasDomainStacks: capabilities.HasCSwitchStacks);

        AddStackRecommendation(
            capabilities.HasReadyThread,
            "ready_thread",
            "ready_thread_top_stacks",
            "ReadyThread stacks provide associated readier/wakeup evidence; they do not alone prove a fully paired wait-to-wakeup cause.",
            ["wait", "scheduler", "stacks"],
            legacyHasDomainStacks: capabilities.HasReadyThreadStacks);

        if (capabilities.HasImageLoad)
            recommendations.Add(("image_load_top_gaps", "Image load events are present; rank loader gaps for startup and DLL-load investigations.", ["startup", "image_load"]));

        AddStackRecommendation(
            capabilities.HasImageLoad,
            "image_load",
            "image_load_top_stacks",
            "ImageLoad events with attached stacks are present; attribute loads to associated call chains.",
            ["startup", "image_load", "stacks"]);

        if (capabilities.HasFileIo)
            recommendations.Add(("file_io_top_files", "File IO events are present; identify files with the most read/write bytes, optionally narrowed by pid/startUs/endUs.", ["io"]));

        AddStackRecommendation(
            capabilities.HasFileIo,
            "file_io",
            "file_io_top_stacks",
            "File IO events with attached stacks are present; attribute file IO bytes to call stacks.",
            ["io", "stacks"]);

        AddStackRecommendation(
            capabilities.HasDiskIo,
            "disk_io",
            "disk_io_top_stacks",
            "Disk IO events with attached stacks are present; attribute physical media bytes to call stacks.",
            ["io", "disk", "stacks"]);

        if (capabilities.HasHardFaults)
            recommendations.Add(("hard_fault_by_file", "Hard-fault events are present; identify files that caused page-ins, optionally narrowed by pid/startUs/endUs.", ["memory", "hard_faults"]));

        AddStackRecommendation(
            capabilities.HasHardFaults,
            "hard_fault",
            "hard_fault_top_stacks",
            "Hard-fault events with attached stacks are present; attribute page-in bytes to associated call chains.",
            ["memory", "hard_faults", "stacks"]);

        if (capabilities.ClrGcIntervalEndpointEventCount > 0)
            recommendations.Add(("clr_gc_analysis", "CLR GC/pause interval endpoints are present; inspect paired durations and unmatched endpoint evidence.", ["gc", "dotnet"]));

        if (capabilities.ClrGcHeapStatsEventCount > 0)
            recommendations.Add(("clr_gc_heap_stats", "GCHeapStats snapshots are present; inspect observed heap-size points without inferring a continuous trend.", ["gc", "memory", "dotnet"]));

        if (capabilities.ClrFinalizerSourceEventCount > 0)
            recommendations.Add(("clr_finalizer_analysis", "CLR finalizer object or batch-endpoint events are present; keep object counts, endpoint counts, and completed batch pairs distinct.", ["gc", "finalizers", "dotnet"]));

        if (capabilities.ClrJitIntervalEndpointEventCount > 0)
            recommendations.Add(("clr_jit_analysis", capabilities.ClrJitCompletedIntervalCount > 0
                ? "Completed CLR JIT interval evidence is present; rank paired methods by compilation duration while honoring unmatched/boundary counts."
                : "CLR JIT source endpoints are present without a completed interval; inspect unmatched/boundary evidence without inferring compilation duration.", ["jit", "dotnet"]));

        AddStackRecommendation(capabilities.HasClrAlloc, "clr_alloc", "clr_alloc_top_stacks",
            "CLR allocation ticks with attached stacks are present; rank managed allocation sources.", ["memory", "dotnet", "stacks"]);
        AddStackRecommendation(capabilities.HasClrException, "clr_exception", "clr_exception_top_stacks",
            "CLR exception events with attached stacks are present; rank thrown exception sources and top exception types.", ["exceptions", "dotnet", "stacks"]);
        AddStackRecommendation(capabilities.HasClrContention, "clr_contention", "clr_contention_top_stacks",
            "Paired CLR contention intervals with attached start stacks are present; rank managed Monitor contention call stacks.", ["locks", "dotnet", "stacks"]);
        AddStackRecommendation(capabilities.HasNetIo, "net_io", "net_top_stacks",
            "Network byte events with attached stacks are present; attribute TCP/UDP bytes to call stacks.", ["network", "stacks"]);

        if (HasNetworkConnectionLifecycle(capabilities))
            recommendations.Add(("net_connections",
                capabilities.NetworkConnectionCompletedLifecycleCount > 0
                    ? "Completed network lifecycles are present; inspect observed TCP timing while honoring unmatched and bounded lifecycle counts."
                    : "Network lifecycle source endpoints are present without a completed lifecycle; inspect endpoint and boundary evidence without inferring connection duration.",
                ["network", "connections"]));

        if (capabilities.HasMemoryProcessInfo || capabilities.HasMemorySystemInfo ||
            capabilities.HasHandleEvents || capabilities.HasPoolEvents)
            recommendations.Add(("memory_resource_analysis", BuildMemoryResourceRecommendationReason(capabilities), ["memory"]));

        AddStackRecommendation(capabilities.HasAlpc, "alpc", "alpc_top_stacks",
            "ALPC events with attached stacks are present; rank cross-process IPC message call chains.", ["ipc", "stacks"]);
        AddStackRecommendation(capabilities.HasInterrupt, "interrupt", "interrupt_top_stacks",
            "DPC/ISR events with attached stacks are present; rank interrupt time by driver call chain.", ["interrupts", "drivers", "stacks"],
            legacyHasDomainStacks: capabilities.HasInterruptStacks);
        AddStackRecommendation(capabilities.HasVirtualAlloc, "virtual_alloc", "virtual_alloc_top_stacks",
            "Virtual memory operations with attached stacks are present; rank operated bytes by call chain.", ["memory", "virtual_memory", "stacks"]);
        AddStackRecommendation(capabilities.HasRegistry, "registry", "registry_top_stacks",
            "Registry operations with attached stacks are present; rank registry activity by call chain.", ["registry", "stacks"]);
        AddStackRecommendation(capabilities.HasNtHeap, "heap_alloc", "heap_alloc_top_stacks",
            "NT heap allocation events with attached stacks are present; rank allocation bytes by call chain.", ["memory", "heap", "stacks"]);

        void AddStackRecommendation(
            bool hasEvents,
            string domain,
            string toolName,
            string reason,
            string[] goals,
            bool legacyHasDomainStacks = false)
        {
            if (!hasEvents)
                return;

            DomainStackCoverage? coverage = null;
            if (capabilities.StackCoverageByDomain is not null)
                capabilities.StackCoverageByDomain.TryGetValue(domain, out coverage);
            if (coverage is null)
            {
                if (legacyHasDomainStacks)
                    recommendations.Add((toolName, reason, goals));
                return;
            }
            if (coverage.StackedEventCount == 0)
                return;

            if (coverage.CoverageState == "partial")
            {
                reason +=
                    $" [stack_coverage_state=partial;stack_coverage_pct={coverage.StackCoveragePct:0.##};" +
                    $"stacked_event_count={coverage.StackedEventCount};total_event_count={coverage.TotalEventCount}]";
            }
            recommendations.Add((toolName, reason, goals));
        }

        return recommendations
            .Select(recommendation => new ToolRecommendation(
                recommendation.ToolName,
                recommendation.Reason,
                recommendation.Goals))
            .ToList();
    }

    internal static IReadOnlyList<string> BuildEnabledCapabilities(TraceCapabilities capabilities)
    {
        var enabled = new List<string>();
        AddCapability(enabled, capabilities.HasCpuSamples, "cpu_samples");
        AddCapability(enabled, capabilities.HasCSwitch, "context_switches");
        AddCapability(enabled, capabilities.HasFileIo, "file_io");
        AddCapability(enabled, capabilities.HasDiskIo, "disk_io");
        AddCapability(enabled, capabilities.HasImageLoad, "image_load");
        AddCapability(enabled, capabilities.HasHardFaults, "hard_faults");
        AddCapability(enabled, capabilities.HasStackWalks, "stack_walks");
        AddCapability(enabled, capabilities.HasExplicitStackWalkEvents, "explicit_stack_walk_events");
        AddCapability(enabled, capabilities.HasAttachedEventStacks, "attached_event_stacks");
        AddCapability(enabled, capabilities.HasVirtualAlloc, "virtual_alloc");
        AddCapability(enabled, capabilities.HasNetIo, "network_io");
        AddCapability(enabled, HasNetworkConnectionLifecycle(capabilities), "network_connections");
        AddCapability(enabled, capabilities.HasRegistry, "registry");
        AddCapability(enabled, capabilities.HasReadyThread, "ready_thread");
        AddCapability(enabled, capabilities.HasInterrupt, "interrupts");
        AddCapability(enabled, capabilities.HasAlpc, "alpc");
        AddCapability(enabled, capabilities.ThreadLifecycleSourceEventCount > 0, "thread_lifetime_source_events");
        AddCapability(enabled, capabilities.ObservedProcessStartEventCount > 0, "observed_process_starts");
        AddCapability(enabled, capabilities.ClrGcIntervalEndpointEventCount > 0, "clr_gc_intervals");
        AddCapability(enabled, capabilities.ClrGcHeapStatsEventCount > 0, "clr_gc_heap_stats");
        AddCapability(enabled, capabilities.ClrFinalizerSourceEventCount > 0, "clr_finalizer_activity");
        AddCapability(enabled, capabilities.ClrJitIntervalEndpointEventCount > 0, "clr_jit_interval_source_events");
        AddCapability(enabled, capabilities.HasClrAlloc, "clr_alloc");
        AddCapability(enabled, capabilities.HasClrException, "clr_exception");
        AddCapability(enabled, capabilities.HasClrContention, "clr_contention");
        AddCapability(enabled, capabilities.HasNtHeap, "nt_heap");
        AddCapability(enabled, capabilities.HasMemoryProcessInfo, "memory_process_info");
        AddCapability(enabled, capabilities.HasMemorySystemInfo, "memory_system_info");
        AddCapability(enabled, capabilities.HasHandleEvents, "handle_events");
        AddCapability(enabled, capabilities.HasPoolEvents, "pool_events");
        AddCapability(enabled, capabilities.HasCSwitchStacks, "cswitch_stacks");
        AddCapability(enabled, capabilities.HasReadyThreadStacks, "ready_thread_stacks");
        AddCapability(enabled, capabilities.HasInterruptStacks, "interrupt_stacks");
        return enabled;
    }

    internal static IReadOnlyList<DiagnosticFlowRecommendation> BuildRecommendedDiagnosticFlows(TraceCapabilities capabilities)
    {
        var flows = new List<DiagnosticFlowRecommendation>();

        if (capabilities.ObservedProcessStartEventCount > 0 ||
            capabilities.HasImageLoad || capabilities.HasCSwitch || capabilities.HasCpuSamples)
        {
            flows.Add(Flow(
                "slow_startup",
                "Use the startup composite when investigating process launch, child-process gaps, or first-DLL delays; it preserves evidence instead of forcing a manual wait/load/CPU reconstruction.",
                ["list_processes", "diagnose_slow_startup"],
                ["startup", "process_creation"],
                [
                    (capabilities.ObservedProcessStartEventCount > 0, "observed_process_starts"),
                    (capabilities.HasImageLoad, "image_load"),
                    (capabilities.HasCSwitch, "context_switches"),
                    (capabilities.HasCpuSamples, "cpu_samples"),
                    (capabilities.HasHardFaults, "hard_faults"),
                    (capabilities.HasFileIo, "file_io"),
                    (capabilities.HasMemoryProcessInfo, "memory_process_info"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasImageLoad, "No ImageLoad events were observed; first-image-load gap evidence will be absent."),
                    (!capabilities.HasCSwitch, "No context switches were observed; wait evidence will be absent."),
                    (!capabilities.HasCpuSamples, "No CPU samples were observed; startup CPU attribution will be absent."))));
        }

        if (capabilities.HasHardFaults || capabilities.HasFileIo || capabilities.HasCSwitch ||
            capabilities.HasMemoryProcessInfo || capabilities.HasMemorySystemInfo ||
            capabilities.HasHandleEvents || capabilities.HasPoolEvents)
        {
            flows.Add(Flow(
                "window_triage",
                "Use diagnose_window after a timestamp, interaction, or page-in stall is known; it aggregates hard faults, file IO, memory, security scan events, and waits for the same interval.",
                ["diagnose_window"],
                ["window", "triage"],
                [
                    (capabilities.HasHardFaults, "hard_faults"),
                    (capabilities.HasFileIo, "file_io"),
                    (capabilities.HasCSwitch, "context_switches"),
                    (capabilities.HasMemoryProcessInfo, "memory_process_info"),
                    (capabilities.HasMemorySystemInfo, "memory_system_info"),
                    (capabilities.HasHandleEvents, "handle_events"),
                    (capabilities.HasPoolEvents, "pool_events"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasHardFaults, "Hard-fault by-file evidence will be empty without HardFaults events."),
                    (!capabilities.HasCSwitch, "Wait evidence will be empty without context switches."),
                    (!capabilities.HasFileIo, "File IO evidence will be empty without FileIO events."))));
        }

        if (capabilities.HasHardFaults)
        {
            flows.Add(Flow(
                "page_in_stall",
                "Use hard_fault_by_file ordered by max_latency to find cold-page stalls, then zoom around MaxLatencyTimeUs with diagnose_window.",
                ["hard_fault_by_file", "diagnose_window"],
                ["memory", "hard_faults", "window"],
                [(true, "hard_faults")],
                []));
        }

        if (capabilities.HasCSwitch)
        {
            var hasCSwitchStacks = HasDomainStacks(capabilities, "cswitch");
            var hasReadyThreadStacks = HasDomainStacks(capabilities, "ready_thread");
            var tools = new List<string> { "diagnose_high_wait", "wait_analysis" };
            if (hasCSwitchStacks)
                tools.Add("wait_top_stacks");
            if (capabilities.HasReadyThread && hasReadyThreadStacks)
                tools.Add("ready_thread_top_stacks");
            flows.Add(Flow(
                "high_wait",
                "Use diagnose_high_wait for high-wall/low-CPU traces before expanding into detailed wait or ready-thread stacks.",
                tools,
                ["wait", "scheduler"],
                [
                    (capabilities.HasCSwitch, "context_switches"),
                    (hasCSwitchStacks, "cswitch_stacks"),
                    (capabilities.HasReadyThread, "ready_thread"),
                    (hasReadyThreadStacks, "ready_thread_stacks"),
                ],
                BuildDomainStackCaveats(
                    capabilities,
                    (capabilities.HasCSwitch, "cswitch", "wait_top_stacks"),
                    (capabilities.HasReadyThread, "ready_thread", "ready_thread_top_stacks"))));
        }

        if (capabilities.HasCpuSamples || capabilities.HasCSwitch)
        {
            var cpuStackCoverage = GetDomainStackCoverage(capabilities, "cpu");
            var hasCpuStacks = capabilities.HasCpuSamples &&
                               cpuStackCoverage?.StackedEventCount > 0;
            var cpuTools = new List<string>();
            if (hasCpuStacks)
                cpuTools.Add("cpu_top_functions");
            if (capabilities.HasCSwitch)
                cpuTools.Add("cpu_precise_analysis");
            flows.Add(Flow(
                "cpu_hotspot",
                "Use sampled CPU for hot functions and precise CPU when context switches are present; compare sampled hotspots with exact on-CPU time before blaming a process.",
                cpuTools,
                ["cpu", "scheduler"],
                [
                    (capabilities.HasCpuSamples, "cpu_samples"),
                    (hasCpuStacks, "cpu_sample_stacks"),
                    (capabilities.HasCSwitch, "context_switches"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasCpuSamples, "cpu_top_functions will be unavailable without CPU samples."),
                    (capabilities.HasCpuSamples && !hasCpuStacks, $"cpu_top_functions omitted because cpu stack coverage is {cpuStackCoverage?.CoverageState ?? "unknown"}."),
                    (!capabilities.HasCSwitch, "cpu_precise_analysis will be unavailable without context switches."))));
        }

        if (capabilities.HasFileIo || capabilities.HasDiskIo)
        {
            var fileIoStackCoverage = GetDomainStackCoverage(capabilities, "file_io");
            var diskIoStackCoverage = GetDomainStackCoverage(capabilities, "disk_io");
            var hasFileIoStacks = fileIoStackCoverage?.StackedEventCount > 0;
            var hasDiskIoStacks = diskIoStackCoverage?.StackedEventCount > 0;
            var ioTools = new List<string>();
            if (capabilities.HasFileIo)
                ioTools.Add("file_io_top_files");
            if (hasFileIoStacks)
                ioTools.Add("file_io_top_stacks");
            if (hasDiskIoStacks)
                ioTools.Add("disk_io_top_stacks");
            flows.Add(Flow(
                "io_contention",
                "Use file IO by-file rows for path attribution, then only use stack views whose own event domain has attached stacks; compare file IO with disk IO to separate cache-served activity from physical media.",
                ioTools,
                ["io", "disk"],
                [
                    (capabilities.HasFileIo, "file_io"),
                    (capabilities.HasDiskIo, "disk_io"),
                    (hasFileIoStacks, "file_io_stacks"),
                    (hasDiskIoStacks, "disk_io_stacks"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasFileIo, "file_io_top_files will be empty without FileIO events."),
                    (capabilities.HasFileIo && !hasFileIoStacks, $"file_io_top_stacks omitted because file_io stack coverage is {fileIoStackCoverage?.CoverageState ?? "unknown"}."),
                    (capabilities.HasDiskIo && !hasDiskIoStacks, $"disk_io_top_stacks omitted because disk_io stack coverage is {diskIoStackCoverage?.CoverageState ?? "unknown"}."))));
        }

        if (capabilities.HasMemoryProcessInfo || capabilities.HasMemorySystemInfo ||
            capabilities.HasHardFaults)
        {
            flows.Add(Flow(
                "memory_pressure",
                "Use memory_resource_analysis for sampled process pressure and hard_fault_by_file for page-in evidence; use timestamps from both to choose a diagnose_window interval.",
                ["memory_resource_analysis", "hard_fault_by_file", "diagnose_window"],
                ["memory", "hard_faults"],
                [
                    (capabilities.HasMemoryProcessInfo, "memory_process_info"),
                    (capabilities.HasMemorySystemInfo, "memory_system_info"),
                    (capabilities.HasHardFaults, "hard_faults"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasMemoryProcessInfo, "memory_resource_analysis will not include working set or commit snapshots without MemoryInfoWS events."),
                    (!capabilities.HasHardFaults, "hard_fault_by_file will be empty without HardFaults events."))));
        }

        if (HasAnyClrCapability(capabilities))
        {
            var hasClrAllocStacks = HasDomainStacks(capabilities, "clr_alloc");
            var hasClrExceptionStacks = HasDomainStacks(capabilities, "clr_exception");
            var hasClrContentionStacks = HasDomainStacks(capabilities, "clr_contention");
            flows.Add(Flow(
                "dotnet_runtime",
                "Use CLR-specific tools only for the runtime signals present in this trace; missing CLR providers cannot be reconstructed after capture.",
                BuildClrRuntimeToolSequence(capabilities),
                BuildClrRuntimeGoals(capabilities),
                [
                    (capabilities.ClrGcIntervalEndpointEventCount > 0, "clr_gc_intervals"),
                    (capabilities.ClrGcHeapStatsEventCount > 0, "clr_gc_heap_stats"),
                    (capabilities.ClrFinalizerSourceEventCount > 0, "clr_finalizer_activity"),
                    (capabilities.HasClrAlloc, "clr_alloc"),
                    (capabilities.HasClrException, "clr_exception"),
                    (capabilities.HasClrContention, "clr_contention"),
                    (capabilities.ClrJitIntervalEndpointEventCount > 0, "clr_jit_interval_source_events"),
                    (hasClrAllocStacks, "clr_alloc_stacks"),
                    (hasClrExceptionStacks, "clr_exception_stacks"),
                    (hasClrContentionStacks, "clr_contention_stacks"),
                ],
                BuildDomainStackCaveats(
                    capabilities,
                    (capabilities.HasClrAlloc, "clr_alloc", "clr_alloc_top_stacks"),
                    (capabilities.HasClrException, "clr_exception", "clr_exception_top_stacks"),
                    (capabilities.HasClrContention, "clr_contention", "clr_contention_top_stacks"))));
        }

        if (capabilities.HasNetIo || HasNetworkConnectionLifecycle(capabilities))
        {
            var hasNetIoStacks = HasDomainStacks(capabilities, "net_io");
            var networkTools = new List<string>();
            if (HasNetworkConnectionLifecycle(capabilities))
                networkTools.Add("net_connections");
            if (hasNetIoStacks)
                networkTools.Add("net_top_stacks");
            flows.Add(Flow(
                "network_activity",
                "Use connection lifecycle rows for setup timing and network stack rows for byte attribution; either signal can exist without the other.",
                networkTools,
                ["network"],
                [
                    (HasNetworkConnectionLifecycle(capabilities), "network_connections"),
                    (capabilities.HasNetIo, "network_io"),
                    (hasNetIoStacks, "network_io_stacks"),
                ],
                BuildFlowCaveats(
                    (!HasNetworkConnectionLifecycle(capabilities), "net_connections omitted because connection lifecycle events were not observed."),
                    (!capabilities.HasNetIo, "net_top_stacks omitted because network byte events were not observed."))
                    .Concat(BuildDomainStackCaveats(
                        capabilities,
                        (capabilities.HasNetIo, "net_io", "net_top_stacks")))
                    .ToList()));
        }

        return flows;
    }

    private static void AddCapability(List<string> enabled, bool isEnabled, string name)
    {
        if (isEnabled)
            enabled.Add(name);
    }

    private static DiagnosticFlowRecommendation Flow(
        string name,
        string reason,
        IReadOnlyList<string> toolSequence,
        IReadOnlyList<string> goals,
        IReadOnlyList<(bool IsEnabled, string Name)> capabilities,
        IReadOnlyList<string> caveats)
        => new(
            FlowName: name,
            Reason: reason,
            ToolSequence: toolSequence,
            Goals: goals,
            EnabledCapabilities: capabilities.Where(capability => capability.IsEnabled).Select(capability => capability.Name).ToList(),
            MissingCapabilities: capabilities.Where(capability => !capability.IsEnabled).Select(capability => capability.Name).ToList(),
            Caveats: caveats);

    private static IReadOnlyList<string> BuildFlowCaveats(params (bool Applies, string Message)[] caveats)
        => caveats
            .Where(caveat => caveat.Applies)
            .Select(caveat => caveat.Message)
            .ToList();

    private static IReadOnlyList<string> BuildClrRuntimeToolSequence(TraceCapabilities capabilities)
    {
        var tools = new List<string>();
        if (capabilities.ClrGcIntervalEndpointEventCount > 0)
            tools.Add("clr_gc_analysis");
        if (capabilities.ClrGcHeapStatsEventCount > 0)
            tools.Add("clr_gc_heap_stats");
        if (capabilities.ClrFinalizerSourceEventCount > 0)
            tools.Add("clr_finalizer_analysis");
        if (capabilities.ClrJitIntervalEndpointEventCount > 0)
            tools.Add("clr_jit_analysis");
        if (capabilities.HasClrAlloc && HasDomainStacks(capabilities, "clr_alloc"))
            tools.Add("clr_alloc_top_stacks");
        if (capabilities.HasClrException && HasDomainStacks(capabilities, "clr_exception"))
            tools.Add("clr_exception_top_stacks");
        if (capabilities.HasClrContention && HasDomainStacks(capabilities, "clr_contention"))
            tools.Add("clr_contention_top_stacks");
        return tools;
    }

    private static IReadOnlyList<string> BuildClrRuntimeGoals(TraceCapabilities capabilities)
    {
        var goals = new List<string> { "dotnet" };
        if (capabilities.ClrGcIntervalEndpointEventCount > 0 ||
            capabilities.ClrGcHeapStatsEventCount > 0)
            goals.Add("gc");
        if (capabilities.ClrFinalizerSourceEventCount > 0)
            goals.Add("finalizers");
        if (capabilities.ClrJitIntervalEndpointEventCount > 0)
            goals.Add("jit");
        if (capabilities.HasClrAlloc)
            goals.Add("allocations");
        if (capabilities.HasClrException)
            goals.Add("exceptions");
        if (capabilities.HasClrContention)
            goals.Add("locks");
        return goals;
    }

    private static IReadOnlyList<ToolRecommendation> BuildToolRecommendationRecords(
        IReadOnlyList<(string ToolName, string Reason, string[] Goals)> recommendations)
        => recommendations
            .Select(recommendation => new ToolRecommendation(
                recommendation.ToolName,
                recommendation.Reason,
                recommendation.Goals))
            .ToList();

    private static bool HasAnyClrCapability(TraceCapabilities capabilities) =>
        capabilities.ClrGcIntervalEndpointEventCount > 0 ||
        capabilities.ClrGcHeapStatsEventCount > 0 ||
        capabilities.ClrFinalizerSourceEventCount > 0 ||
        capabilities.ClrJitIntervalEndpointEventCount > 0 ||
        capabilities.HasClrAlloc ||
        capabilities.HasClrException ||
        capabilities.HasClrContention;

    private static bool HasNetworkConnectionLifecycle(TraceCapabilities capabilities) =>
        capabilities.NetworkConnectionLifecycleEndpointEventCount > 0;

    private static DomainStackCoverage? GetDomainStackCoverage(
        TraceCapabilities capabilities,
        string domain)
    {
        if (capabilities.StackCoverageByDomain is not null &&
            capabilities.StackCoverageByDomain.TryGetValue(domain, out var coverage))
        {
            return coverage;
        }
        return null;
    }

    private static bool HasDomainStacks(TraceCapabilities capabilities, string domain) =>
        GetDomainStackCoverage(capabilities, domain)?.StackedEventCount > 0;

    private static IReadOnlyList<string> BuildDomainStackCaveats(
        TraceCapabilities capabilities,
        params (bool HasEvents, string Domain, string ToolName)[] domains)
    {
        var caveats = new List<string>();
        foreach (var (hasEvents, domain, toolName) in domains)
        {
            if (!hasEvents)
                continue;

            var coverage = GetDomainStackCoverage(capabilities, domain);
            if (coverage is null)
            {
                caveats.Add($"{toolName} omitted: stack_coverage_state=unknown;domain={domain}.");
            }
            else if (coverage.StackedEventCount == 0)
            {
                caveats.Add($"{toolName} omitted: stack_coverage_state={coverage.CoverageState};domain={domain};stacked_event_count=0;total_event_count={coverage.TotalEventCount}.");
            }
            else if (coverage.CoverageState == "partial")
            {
                caveats.Add($"{toolName} is partial evidence: stack_coverage_state=partial;domain={domain};stack_coverage_pct={coverage.StackCoveragePct:0.##};stacked_event_count={coverage.StackedEventCount};total_event_count={coverage.TotalEventCount}.");
            }
        }
        return caveats;
    }

    private static string BuildMemoryResourceRecommendationReason(TraceCapabilities capabilities)
    {
        var signals = new List<string>();
        if (capabilities.HasMemoryProcessInfo)
            signals.Add("working set and commit/private-byte snapshots");
        if (capabilities.HasMemorySystemInfo)
            signals.Add("system-wide available/free/commit snapshots");
        if (capabilities.HasHandleEvents)
            signals.Add("handle create/close deltas");
        if (capabilities.HasPoolEvents)
            signals.Add("observed pool allocation/free deltas");

        return $"Memory resource events are present; inspect {string.Join(", ", signals)}.";
    }

    private static readonly string[] CpuSampleToolNames =
    [
        "cpu_top_functions",
        "cpu_top_functions_batch",
        "cpu_caller_callee",
    ];

    private static readonly string[] ContextSwitchToolNames =
    [
        "cpu_precise_analysis",
        "wait_top_stacks",
        "wait_caller_callee",
        "wait_analysis",
        "ready_thread_top_stacks",
        "ready_thread_caller_callee",
    ];

    private static readonly string[] WaitStackToolNames =
    [
        "wait_top_stacks",
        "wait_caller_callee",
    ];

    private static readonly string[] ReadyThreadStackToolNames =
    [
        "ready_thread_top_stacks",
        "ready_thread_caller_callee",
    ];

    private static readonly string[] InterruptStackToolNames =
    [
        "interrupt_top_stacks",
        "interrupt_caller_callee",
    ];

    private static readonly string[] CompositeToolNames =
    [
        "diagnose_slow_startup",
    ];

    private static readonly string[] HighWaitCompositeToolNames =
    [
        "diagnose_high_wait",
    ];

    private static readonly string[] StackWalkDegradedCompositeToolNames =
    [
        "diagnose_high_wait",
    ];

    private static readonly string[] FileIoToolNames =
    [
        "file_io_top_files",
        "file_io_top_stacks",
        "file_io_caller_callee",
    ];

    private static readonly string[] DiskIoToolNames =
    [
        "disk_io_top_stacks",
        "disk_io_caller_callee",
    ];

    private static readonly string[] ClrToolNames =
    [
        "clr_gc_analysis",
        "clr_gc_heap_stats",
        "clr_finalizer_analysis",
        "clr_jit_analysis",
        "clr_alloc_top_stacks",
        "clr_alloc_caller_callee",
        "clr_exception_top_stacks",
        "clr_exception_caller_callee",
        "clr_contention_top_stacks",
        "clr_contention_caller_callee",
    ];

    private static readonly string[] NetworkByteToolNames =
    [
        "net_top_stacks",
        "net_caller_callee",
    ];

    private static readonly string[] NetworkConnectionToolNames =
    [
        "net_connections",
    ];

    private static readonly string[] MemoryResourceToolNames =
    [
        "memory_resource_analysis",
    ];

    private static readonly string[] StackDependentToolNames =
    [
        "cpu_top_functions",
        "cpu_top_functions_batch",
        "cpu_caller_callee",
        "wait_top_stacks",
        "wait_caller_callee",
        "ready_thread_top_stacks",
        "ready_thread_caller_callee",
        "file_io_top_stacks",
        "file_io_caller_callee",
        "disk_io_top_stacks",
        "disk_io_caller_callee",
        "hard_fault_top_stacks",
        "hard_fault_caller_callee",
        "image_load_top_stacks",
        "image_load_caller_callee",
        "registry_top_stacks",
        "registry_caller_callee",
        "net_top_stacks",
        "net_caller_callee",
        "alpc_top_stacks",
        "alpc_caller_callee",
        "interrupt_top_stacks",
        "interrupt_caller_callee",
        "heap_alloc_top_stacks",
        "heap_alloc_caller_callee",
        "virtual_alloc_top_stacks",
        "virtual_alloc_caller_callee",
        "clr_alloc_top_stacks",
        "clr_alloc_caller_callee",
        "clr_exception_top_stacks",
        "clr_exception_caller_callee",
        "clr_contention_top_stacks",
        "clr_contention_caller_callee",
        "generic_event_top_stacks",
        "generic_event_caller_callee",
    ];

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Lists process lifetimes in the loaded trace using exact cursor pagination. Default order is CPU time descending. " +
        "WaitRatio = WallUs/CpuUs ranks 'high wall, low CPU' process-lifetime candidates; " +
        "the ratio alone does not identify what they waited on. " +
        "PID 0 (Idle) and PID 4 (System) hidden by default — pass includeSystem=true to surface them. " +
        "When orderBy='wait_ratio', trace-resident processes (alive before trace start AND survived past " +
        "trace end) and processes with near-zero sampled CPU are pushed to the bottom because " +
        "their ratio is denominator-sensitive noise. Rows use a stable total order and top is the page-size limit; " +
        "follow nextCursor with traceId/orderBy/top/includeSystem unchanged until hasMore=false to enumerate every lifetime. " +
        "No startUs/endUs: this is a whole-trace process overview; use windowed analyzers for scoped metrics.")]
    public ProcessListResponse ListProcesses(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Sort order: 'cpu' (default), 'wall', or 'wait_ratio'")] string orderBy = "cpu",
        [Description("Maximum rows in this page (default 50, max 1000). Repeat unchanged with cursor.")] int top = 50,
        [Description("Include PID 0 (Idle) and PID 4 (System); default false")] bool includeSystem = false,
        [Description("Opaque qrc_ continuation from the preceding page; bound to this session, immutable trace generation, ordering, top, includeSystem, contract, and privacy profile.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        Validation.RequireTop(top);
        Validation.RequireText(orderBy);
        orderBy = orderBy.ToLowerInvariant();
        using var traceLease = _cache.Acquire(traceId);
        var facts = traceLease.GetFacts(cancellationToken);
        var rows = facts.Processes
            .Where(row => includeSystem || (row.Pid != 0 && row.Pid != 4))
            .ToList();
        var totalCount = rows.Count;
        var hidden = includeSystem
            ? 0
            : facts.Processes.Count(row => row.Pid == 0 || row.Pid == 4);

        rows = orderBy switch
        {
            "cpu" => rows
                .OrderByDescending(r => r.CpuUs)
                .ThenBy(r => r.Pid)
                .ThenBy(r => r.StartUs)
                .ToList(),
            "wall" => rows
                .OrderByDescending(r => r.WallUs)
                .ThenBy(r => r.Pid)
                .ThenBy(r => r.StartUs)
                .ToList(),
            "wait_ratio" => rows
                .OrderByDescending(WaitRatioSortKey)
                .ThenByDescending(r => r.WallUs)
                .ThenBy(r => r.Pid)
                .ThenBy(r => r.StartUs)
                .ToList(),
            _ => throw new ArgumentException(
                $"orderBy must be 'cpu', 'wall', or 'wait_ratio'; got '{orderBy}'", nameof(orderBy)),
        };

        var query = TimelinePagination.CanonicalQuery(
            TimelinePagination.ListProcessesTool,
            ("orderBy", orderBy),
            ("top", TimelinePagination.Number(top)),
            ("includeSystem", includeSystem ? "true" : "false"));
        var context = TimelinePagination.CreateContext(
            traceLease,
            traceId,
            TimelinePagination.ListProcessesTool,
            query,
            TimelinePagination.ListProcessesOrdering(orderBy));
        var position = _queryResults.ResolveTimeline(context, cursor);
        var page = TimelinePagination.Slice(
            rows,
            position,
            top,
            TimelinePagination.ProcessKey);
        return new ProcessListResponse(
            page.Rows,
            hidden,
            totalCount,
            context.PageContext(
                page.StartIndex,
                top,
                page.TotalCount,
                page.Rows.Count),
            ReturnedCount: page.Rows.Count,
            HasMore: page.HasMore,
            NextCursor: page.HasMore
                ? QueryResultCursorRegistry.PendingDeliveryToken
                : null);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Per-fork timing for a parent process — given a PID, returns every child the kernel " +
        "reported as having that parent, with FirstImageLoadOffsetUs (the kernel-side window " +
        "between ProcessStart and the first DLL load; this interval can include process-create " +
        "callbacks, security inspection, suspension, and other mechanisms, but does not identify " +
        "which mechanism caused the gap) and GapFromPreviousSpawnUs (lets you spot fork " +
        "bursts vs steady-state). Median/p95/max aggregates across kernel gaps surface " +
        "worst-case in a single number. No startUs/endUs: scope is the parent's child-process lifecycle. " +
        "Children use exact cursor pagination ordered by StartTimeUs, Pid, then SourceOrdinal; " +
        "SpawnCount remains the exact full-result total. Follow nextCursor until hasMore=false. " +
        "Clean parent-PID reuse returns structured " +
        "ScopeStatus/NoDataReason=process_start_required with replayable candidates; conflicting lifetime " +
        "evidence returns ambiguous_process_instance. A missing exact parent lifetime returns scope_not_found.")]
    public ProcessCreateTimingResponse ProcessCreateTiming(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Parent process ID — the process whose CreateProcess calls you want timed.")]
        int parentPid,
        [Description("Maximum children to return in this page (default 50, max 1000). This does not change SpawnCount.")]
        int pageSize = 50,
        [Description("Exact parent process start in trace-relative microseconds. Required when the parent PID has multiple lifetimes.")]
        long? processStartUs = null,
        [Description("Opaque qrc_ continuation returned by the previous page. It is bound to the principal/session, trace generation, tool contract, symbol/privacy context, normalized query, scope, and ordering.")]
        string? cursor = null)
    {
        Validation.RequireTop(pageSize);
        Validation.RequirePositivePid(parentPid);
        Validation.RequireThreadSelector(
            parentPid, tid: null, processStartUs, threadStartUs: null);
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var query = TimelinePagination.CanonicalQuery(
            TimelinePagination.ProcessCreateTimingTool,
            ("parentPid", TimelinePagination.Number(parentPid)),
            ("processStartUs", TimelinePagination.OptionalNumber(processStartUs)),
            ("pageSize", TimelinePagination.Number(pageSize)));
        var context = TimelinePagination.CreateContext(
            traceLease,
            traceId,
            TimelinePagination.ProcessCreateTimingTool,
            query,
            TimelinePagination.ProcessCreateTimingOrdering);
        var position = _queryResults.ResolveTimeline(context, cursor);
        return ProcessCreateTimingAnalysis.AnalyzePage(
            trace, parentPid, pageSize, processStartUs, position, context);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Per-process thread-lifecycle list — every ThreadStart / ThreadStop in chronological " +
        "order for one PID, with start time, end time, and lifetime in microseconds.  Useful " +
        "for 'did the thread pool spawn 200 threads in the startup window' / 'is something " +
        "thrashing thread creation'.  Threads still alive at trace end are flagged " +
        "TraceResidentEnd; threads alive when capture started are flagged TraceResidentStart " +
        "(their StartTimeUs is 0 = trace start, not the real spawn).  PeakConcurrentThreads " +
        "gives the maximum number of simultaneously-live threads for the selected process instance. " +
        "Rows use exact cursor pagination ordered by StartTimeUs, Tid, then ThreadGeneration; " +
        "TotalThreads remains the exact full-result total. Follow nextCursor until hasMore=false. " +
        "Clean PID reuse returns structured ScopeStatus/NoDataReason=process_start_required with replayable " +
        "candidates unless processStartUs selects one lifetime; conflicting lifetime evidence returns " +
        "ambiguous_process_instance. Requires the Thread " +
        "keyword in the capture profile (in default kernel profiles). No startUs/endUs: this reports a " +
        "per-PID thread lifecycle timeline; timestamps identify the interval boundaries. A missing exact " +
        "lifetime returns ScopeStatus=scope_not_found rather than falling back to another instance.")]
    public ThreadLifetimeResponse ThreadLifetime(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Process ID")] int pid,
        [Description("Maximum threads to return in this page (default 200, max 1000). This does not change TotalThreads.")] int pageSize = 200,
        [Description("Exact process start in trace-relative microseconds. Required when the PID has multiple clean lifetimes; otherwise process_start_required returns candidate keys.")]
        long? processStartUs = null,
        [Description("Opaque qrc_ continuation returned by the previous page. It is bound to the principal/session, trace generation, tool contract, symbol/privacy context, normalized query, scope, and ordering.")]
        string? cursor = null)
    {
        Validation.RequireTop(pageSize);
        Validation.RequirePositivePid(pid);
        Validation.RequireThreadSelector(
            pid, tid: null, processStartUs, threadStartUs: null);
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var query = TimelinePagination.CanonicalQuery(
            TimelinePagination.ThreadLifetimeTool,
            ("pid", TimelinePagination.Number(pid)),
            ("processStartUs", TimelinePagination.OptionalNumber(processStartUs)),
            ("pageSize", TimelinePagination.Number(pageSize)));
        var context = TimelinePagination.CreateContext(
            traceLease,
            traceId,
            TimelinePagination.ThreadLifetimeTool,
            query,
            TimelinePagination.ThreadLifetimeOrdering);
        var position = _queryResults.ResolveTimeline(context, cursor);
        return ThreadLifetimeAnalysis.AnalyzePage(
            trace, pid, pageSize, processStartUs, position, context);
    }

    // Trace-resident processes and near-zero-CPU rows get huge ratios from tiny denominators.
    // Keep the floor low enough that real high-wall/low-CPU IPC waits still surface, while
    // 1-4ms bookkeeping rows don't dominate the sort.
    private static double WaitRatioSortKey(ProcessRow r)
        => r.TraceResident || r.CpuUs < WaitRatioMinCpuUs(r)
            ? double.NegativeInfinity
            : r.WaitRatio ?? double.NegativeInfinity;

    private static long WaitRatioMinCpuUs(ProcessRow r)
        => Math.Max(MinCpuUsForWaitRatioSort, (long)(r.WallUs * MinCpuShareForWaitRatioSort));
}
