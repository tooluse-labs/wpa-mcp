using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal static class ToolEnvelopeProjection
{
    private static readonly MethodInfo SuccessMethod = typeof(ToolEnvelopeProjection)
        .GetMethod(nameof(CreateSuccess), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo FailureMethod = typeof(ToolEnvelopeProjection)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .Single(method => method.Name == nameof(CreateFailure) &&
            method.IsGenericMethodDefinition &&
            method.GetParameters().Length == 4);
    private static readonly ConcurrentDictionary<Type, MethodInfo> SuccessMethods = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> FailureMethods = new();

    internal static object Success(
        ActiveToolDefinition tool,
        object data,
        ReviewedToolResult reviewed,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        IReadOnlyList<CapabilityRuntimeAssessment> capabilityAssessments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(reviewed);
        if (!tool.OutputDataType.IsInstanceOfType(data))
            throw new ArgumentException("The result does not match the manifest return type.", nameof(data));

        try
        {
            return SuccessMethods.GetOrAdd(
                    tool.OutputDataType,
                    static type => SuccessMethod.MakeGenericMethod(type))
                .Invoke(null, [tool, data, reviewed, arguments, capabilityAssessments])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    internal static object Failure(
        ActiveToolDefinition tool,
        ToolError error,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        IReadOnlyList<CapabilityRuntimeAssessment> capabilityAssessments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            return FailureMethods.GetOrAdd(
                    tool.OutputDataType,
                    static type => FailureMethod.MakeGenericMethod(type))
                .Invoke(null, [tool, error, arguments, capabilityAssessments])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static ToolEnvelope<TData> CreateSuccess<TData>(
        ActiveToolDefinition tool,
        object untypedData,
        ReviewedToolResult reviewed,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        IReadOnlyList<CapabilityRuntimeAssessment> capabilityAssessments)
        where TData : class
    {
        var data = (TData)untypedData;
        var root = reviewed.Domain as JsonObject;
        var traceRef = TraceReference(tool, root, arguments);
        var scope = Scope(tool, root, arguments, reviewed.Outcome.ScopeSource);
        if (scope.Status != ToolScopeStatus.Ok && scope.Status != ToolScopeStatus.NotApplicable)
        {
            return CreateFailure<TData>(
                tool,
                ScopeError(scope.Status),
                arguments,
                traceRef,
                scope,
                capabilityAssessments);
        }

        var evidence = Evidence(tool, capabilityAssessments);
        var boundary = Boundary(tool, capabilityAssessments, reviewed.Sections);
        ToolEvidenceContractValidator.Validate(tool, boundary);
        var warnings = ReadWarnings(root, "warnings")
            .Concat(TraceQueryExecutionContext.CurrentReference?.Warnings ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sections = Sections(
            tool,
            root,
            reviewed.Sections,
            reviewed.Outcome);
        var domainSections = sections
            .Where(IsDomainSection)
            .ToArray();
        var allEmpty = !reviewed.Outcome.HasUsableData &&
            domainSections.Length > 0 &&
            domainSections.All(section => section.Returned == 0);
        var noData = allEmpty
            ? new ToolNoData(
                NormalizeNoDataReason(reviewed.Outcome.NoDataReason),
                "REVIEWED_ALL_DOMAIN_SECTIONS_EMPTY",
                domainSections.SelectMany(section => section.EvidenceIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray())
            : !reviewed.Outcome.HasUsableData && reviewed.Outcome.NoDataReason is not null
                ? new ToolNoData(
                    NormalizeNoDataReason(reviewed.Outcome.NoDataReason),
                    "REVIEWED_SEMANTIC_NO_DATA_POLICY",
                    boundary.Items.Select(item => item.EvidenceId).ToArray())
                : null;
        if (!reviewed.Outcome.HasUsableData && noData is null)
            throw new InvalidOperationException($"Reviewed policy for '{tool.ToolName}' did not explain its empty result.");
        var isPartial = reviewed.Outcome.Partial;
        var failedSections = isPartial
            ? new[]
            {
                new ToolSectionFailure(
                    "omitted_work",
                    reviewed.Outcome.PartialErrorCode ?? throw new InvalidOperationException(
                        $"Reviewed policy for '{tool.ToolName}' omitted its typed partial-failure code."),
                    reviewed.Outcome.PartialErrorCode == "budget_exceeded"
                        ? "The requested analysis exceeded its execution budget; completed sections remain usable."
                        : "Part of the requested analysis did not complete; completed sections remain usable.",
                    retryable: true),
            }
            : Array.Empty<ToolSectionFailure>();

        var sectionsWithData = domainSections.Count(section => section.Returned > 0);
        var conceptualDataSections = domainSections.Length == 0 && noData is null ? 1 : domainSections.Length;
        var requestedSectionCount = Math.Max(1, conceptualDataSections + failedSections.Length);
        var sectionHasMore = sections.Any(section => section.HasMore);
        var hasMore = ReadBoolean(root, "hasMore") ?? sectionHasMore;
        if (sectionHasMore && !hasMore)
            throw new InvalidOperationException("A section continuation cannot exceed the response continuation state.");
        var completenessStatus = isPartial
            ? ToolCompletenessStatus.Partial
            : noData is not null
                ? ToolCompletenessStatus.NoData
                : hasMore && tool.PaginationMode == "cursor"
                    ? ToolCompletenessStatus.Paged
                : reviewed.Sections.Where(IsDomainSection).All(section => section.Requested is null)
                    ? ToolCompletenessStatus.Complete
                : tool.PaginationMode switch
                {
                    "top_n" => ToolCompletenessStatus.TopN,
                    "cursor" => ToolCompletenessStatus.Paged,
                    _ => ToolCompletenessStatus.Complete,
                };
        var completeness = new ToolCompleteness(
            completenessStatus,
            requestedSectionCount,
            domainSections.Length == 0 ? (noData is null ? 1 : 0) : sectionsWithData,
            failedSections.Length,
            hasMore);

        return new ToolEnvelope<TData>(
            ToolContractVersions.V2,
            isPartial ? ToolCompletionStatus.Partial : ToolCompletionStatus.Succeeded,
            data,
            error: null,
            failedSections,
            sections,
            warnings,
            hasMore,
            ToolReference(tool),
            traceRef,
            scope,
            evidence,
            completeness,
            boundary,
            noData,
            Precision(tool.OutputDataType));
    }

    private static ToolEnvelope<TData> CreateFailure<TData>(
        ActiveToolDefinition tool,
        ToolError error,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        IReadOnlyList<CapabilityRuntimeAssessment> capabilityAssessments)
        where TData : class =>
        CreateFailure<TData>(
            tool,
            error,
            arguments,
            TraceReference(tool, null, arguments),
            FailureScope(tool, arguments),
            capabilityAssessments);

    private static ToolEnvelope<TData> CreateFailure<TData>(
        ActiveToolDefinition tool,
        ToolError error,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        ToolTraceReference? traceRef,
        ToolScope scope,
        IReadOnlyList<CapabilityRuntimeAssessment> capabilityAssessments)
        where TData : class
    {
        var boundary = Boundary(tool, capabilityAssessments);
        ToolEvidenceContractValidator.Validate(tool, boundary);
        return new ToolEnvelope<TData>(
            ToolContractVersions.V2,
            ToolCompletionStatus.Failed,
            data: null,
            error,
            failedSections: Array.Empty<ToolSectionFailure>(),
            sections: Array.Empty<ToolSectionPage>(),
            warnings: Array.Empty<string>(),
            hasMore: false,
            ToolReference(tool),
            traceRef,
            scope,
            Evidence(tool, capabilityAssessments),
            new ToolCompleteness(ToolCompletenessStatus.Failed, 1, 0, 0, false),
            boundary,
            noData: null,
            Precision(tool.OutputDataType));
    }

    private static ToolReference ToolReference(ActiveToolDefinition tool) =>
        new(tool.ToolName, tool.Capabilities.Select(item => item.CapabilityId).ToArray());

    private static IReadOnlyList<ToolCapabilityEvidence> Evidence(
        ActiveToolDefinition tool,
        IReadOnlyList<CapabilityRuntimeAssessment> assessments)
    {
        var byCapability = assessments.ToDictionary(
            assessment => assessment.CapabilityId,
            StringComparer.Ordinal);
        return tool.Capabilities.Select(capability =>
        {
            if (!byCapability.TryGetValue(capability.CapabilityId, out var assessment))
                throw new InvalidOperationException($"Tool '{tool.ToolName}' omitted runtime assessment for '{capability.CapabilityId}'.");
            var capabilityEvidenceIds = assessment.Evidence
                .Select(evidence => evidence.EvidenceId)
                .ToArray();
            if (capabilityEvidenceIds.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Tool '{tool.ToolName}' has no evidence reference mapped to '{capability.CapabilityId}'.");
            }
            return new ToolCapabilityEvidence(
                capability.CapabilityId,
                assessment.TraceStatus,
                assessment.ScopedStatus,
                assessment.TraceEligibleEventCount,
                assessment.ScopedMatchedEventCount,
                assessment.CaptureIntegrity,
                capabilityEvidenceIds,
                assessment.TraceCompletedEvidenceCount,
                assessment.TraceUnmatchedEvidenceCount,
                assessment.TraceBoundaryEvidenceCount,
                assessment.EvidenceCompletionState,
                assessment.CountRepresentation);
        }).ToArray();
    }

    private static ToolEvidenceBoundary Boundary(
        ActiveToolDefinition tool,
        IReadOnlyList<CapabilityRuntimeAssessment> assessments,
        IReadOnlyList<ReviewedSectionProof>? sections = null)
    {
        var inferences = assessments
            .SelectMany(assessment => assessment.Evidence)
            .GroupBy(inference => inference.EvidenceId, StringComparer.Ordinal)
            .Select(group =>
            {
                var values = group.ToArray();
                var first = values[0];
                if (values.Any(value =>
                        value.MeasurementBasis != first.MeasurementBasis ||
                        value.Relationship != first.Relationship ||
                        value.ConclusionStatus != first.ConclusionStatus ||
                        value.CaptureIntegrity != first.CaptureIntegrity ||
                        !value.DoesNotProve.SequenceEqual(first.DoesNotProve)))
                {
                    throw new InvalidOperationException(
                        $"Evidence '{group.Key}' has conflicting capability-specific runtime inferences.");
                }
                var reference = tool.Capabilities
                    .SelectMany(capability => capability.EvidenceReferences)
                    .First(candidate => candidate.EvidenceId == first.EvidenceId);
                var mappedSections = (sections ?? Array.Empty<ReviewedSectionProof>())
                    .Where(section => section.EvidenceIds.Contains(first.EvidenceId, StringComparer.Ordinal))
                    .Select(section => SectionName(section.Pointer))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return new ToolEvidenceBoundaryItem(
                    first.EvidenceId,
                    sections: mappedSections,
                    first.MeasurementBasis,
                    first.Relationship,
                    first.ConclusionStatus,
                    first.DoesNotProve,
                    new ToolEvidenceProvenance(
                        reference.Kind,
                        "wpa_mcp_typed_result",
                        first.Provenance,
                        ruleId: null,
                        first.CaptureIntegrity));
            })
            .OrderBy(item => tool.EvidenceReferenceIds.IndexOf(item.EvidenceId))
            .ToArray();
        return new ToolEvidenceBoundary(inferences);
    }

    private static IReadOnlyList<ToolSectionPage> Sections(
        ActiveToolDefinition tool,
        JsonObject? root,
        IReadOnlyList<ReviewedSectionProof> proofs,
        ReviewedToolRuntimeOutcome outcome)
    {
        if (root is null || proofs.Count == 0)
            return Array.Empty<ToolSectionPage>();

        var results = new List<ToolSectionPage>(proofs.Count);
        foreach (var proof in proofs)
        {
            var section = SectionName(proof.Pointer);
            var noData = proof.Returned == 0 && IsDomainSection(proof)
                ? new ToolNoData(
                    NormalizeNoDataReason(proof.NoDataReason ?? outcome.NoDataReason),
                    "REVIEWED_SECTION_EMPTY",
                    proof.EvidenceIds)
                : null;
            var conclusionStatus = ResolveSectionConclusion(proof, outcome);
            results.Add(new ToolSectionPage(
                section,
                proof.Mode,
                proof.Requested,
                proof.Returned,
                proof.TotalAvailable,
                proof.TotalState switch
                {
                    "exact" => ToolSectionTotalState.Exact,
                    "lower_bound" => ToolSectionTotalState.LowerBound,
                    "unknown" => ToolSectionTotalState.Unknown,
                    _ => throw new InvalidOperationException("A reviewed adapter emitted an invalid total proof state."),
                },
                proof.HasMore,
                proof.SortKey,
                proof.SortDirection,
                proof.TieBreakers,
                proof.NextCursor,
                proof.TruncationReason,
                noData,
                proof.Role,
                proof.EvidenceIds,
                proof.HasMore
                    ? ToolSectionMoreState.Present
                    : proof.TotalState == "unknown" &&
                      string.Equals(proof.TruncationReason, "source_limit_saturated", StringComparison.Ordinal)
                        ? ToolSectionMoreState.Unknown
                        : ToolSectionMoreState.Absent,
                continuationAvailable: proof.NextCursor is not null,
                measurementBasis: proof.MeasurementBasis,
                relationship: proof.Relationship,
                conclusionStatus: conclusionStatus));
        }
        return results;
    }

    private static ConclusionStatus ResolveSectionConclusion(
        ReviewedSectionProof proof,
        ReviewedToolRuntimeOutcome outcome)
    {
        if (proof.Role is ToolSectionRole.Boundary or ToolSectionRole.Provenance or
            ToolSectionRole.Recommendation)
        {
            return ConclusionStatus.NotApplicable;
        }
        if (proof.Returned == 0 || proof.ConclusionStatus == ConclusionStatus.NotConcluded)
            return ConclusionStatus.NotConcluded;
        if (outcome.ScopedCapabilityStatus is ToolCapabilityStatus.Unknown or
            ToolCapabilityStatus.Unavailable)
        {
            return ConclusionStatus.NotConcluded;
        }
        if (outcome.ScopedCapabilityStatus == ToolCapabilityStatus.Partial ||
            outcome.CaptureIntegrity == ToolCaptureIntegrityStatus.Partial)
        {
            return ConclusionStatus.Partial;
        }
        return proof.ConclusionStatus;
    }

    private static bool IsDomainSection(ReviewedSectionProof section) =>
        section.Role is ToolSectionRole.DomainData or ToolSectionRole.DomainEvidence;

    private static bool IsDomainSection(ToolSectionPage section) =>
        section.Role is ToolSectionRole.DomainData or ToolSectionRole.DomainEvidence;

    private static string SectionName(string pointer) => pointer.Trim('/').Replace('/', '.');

    private static ToolTraceReference? TraceReference(
        ActiveToolDefinition tool,
        JsonObject? root,
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var current = TraceQueryExecutionContext.CurrentReference;
        var traceId = current?.TraceId ?? ReadString(root, "traceId") ??
            ReadArgumentString(arguments, "traceId");
        if (traceId is null || !traceId.StartsWith("trc_", StringComparison.Ordinal))
            return null;
        var symbolContextId = SymbolQueryExecutionContext.CurrentSymbolContextId ??
            ReadString(root, "symbolContextId") ??
            ReadArgumentString(arguments, "symbolContextId");
        var refKind = current?.RefKind == "ephemeral" ? ToolTraceRefKind.Ephemeral : ToolTraceRefKind.Canonical;
        return new ToolTraceReference(traceId, generationAlias: null, symbolContextId, refKind);
    }

    private static ToolScope Scope(
        ActiveToolDefinition tool,
        JsonObject? root,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        ReviewedScopeSource scopeSource)
    {
        var requested = Requested(arguments);
        if (scopeSource == ReviewedScopeSource.NotApplicable)
        {
            return new ToolScope(
                ToolScopeStatus.NotApplicable,
                ToolScopeMode.NotApplicable,
                requested,
                selected: null,
                candidates: Array.Empty<ToolScopeIdentity>(),
                included: Array.Empty<ToolScopeIdentity>(),
                pidReuseObserved: false,
                identityUnresolved: false);
        }
        if (scopeSource == ReviewedScopeSource.Trace)
        {
            return new ToolScope(
                ToolScopeStatus.Ok,
                ToolScopeMode.Trace,
                requested,
                selected: null,
                candidates: Array.Empty<ToolScopeIdentity>(),
                included: Array.Empty<ToolScopeIdentity>(),
                pidReuseObserved: false,
                identityUnresolved: false);
        }
        var rawStatus = ReadString(root, "scopeStatus");
        if (rawStatus is null)
            throw new InvalidOperationException($"Scoped tool '{tool.ToolName}' omitted reviewed ScopeStatus.");
        var status = ParseScopeStatus(rawStatus, requested.Tid is not null);
        var rawIncluded = root?["includedThreads"] is JsonArray includedThreads
            ? ReadThreadCandidates(includedThreads)
            : ReadProcessIdentities(root?["includedProcesses"]);
        var selected = root?["selectedThread"] is JsonObject selectedThread
            ? ReadSelectedThreadIdentity(selectedThread, rawIncluded)
            : ReadProcessIdentity(root?["selectedProcess"]);
        var candidates = status == ToolScopeStatus.Ok ? Array.Empty<ToolScopeIdentity>() : rawIncluded;
        var included = status == ToolScopeStatus.Ok ? rawIncluded : Array.Empty<ToolScopeIdentity>();
        var mode = ParseScopeMode(tool, ReadString(root, "scopeMode"), requested);
        if (requested.ProcessStartUs is not null && status == ToolScopeStatus.Ok)
        {
            if (mode == ToolScopeMode.PidAggregate || selected is null)
                throw new InvalidOperationException("An exact processStartUs selector cannot be projected as a PID aggregate or unresolved scope.");
            if (selected.Pid != requested.Pid || selected.ProcessStartUs != requested.ProcessStartUs)
                throw new InvalidOperationException("The selected process identity differs from the requested exact process instance.");
        }
        if (requested.Tid is not null && status == ToolScopeStatus.Ok && mode == ToolScopeMode.ThreadInstance)
        {
            if (selected?.Tid != requested.Tid)
                throw new InvalidOperationException("The selected thread identity differs from the requested TID.");
            if (selected.ThreadStartUs is null || selected.ThreadGeneration is null)
                throw new InvalidOperationException("An exact thread scope omitted the replayable threadStartUs/threadGeneration identity.");
            if (requested.ThreadStartUs is not null && selected.ThreadStartUs != requested.ThreadStartUs)
                throw new InvalidOperationException("The selected thread start differs from the requested threadStartUs.");
            if (requested.ThreadGeneration is not null &&
                !string.Equals(selected.ThreadGeneration, requested.ThreadGeneration, StringComparison.Ordinal))
                throw new InvalidOperationException("The selected thread generation differs from the requested threadGeneration.");
        }
        var reuse = ReadBoolean(root, "pidReuseObserved") ?? throw new InvalidOperationException(
            $"Scoped tool '{tool.ToolName}' omitted reviewed PidReuseObserved.");
        var unresolved = status == ToolScopeStatus.IdentityUnresolved ||
            HasScopedIdentityUnresolvedEvidence(root);
        return new ToolScope(status, mode, requested, selected, candidates, included, reuse, unresolved);
    }

    internal static bool HasScopedIdentityUnresolvedEvidence(JsonObject? root)
    {
        if (root is null)
            return false;
        if (string.Equals(
                ReadString(root, "noDataReason"),
                "source_events_unattributed",
                StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var property in root)
        {
            if ((!property.Key.StartsWith("scopedIdentityUnresolved", StringComparison.OrdinalIgnoreCase) &&
                 !property.Key.Equals("scopedUnattributedEventCount", StringComparison.OrdinalIgnoreCase)) ||
                !property.Key.EndsWith("Count", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if ((ReadLong(root, property.Key) ?? 0) > 0)
                return true;
        }
        return false;
    }

    private static ToolScope FailureScope(
        ActiveToolDefinition tool,
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var requested = Requested(arguments);
        return new ToolScope(
            ToolScopeStatus.NotEvaluated,
            tool.SelectableScopes.Contains("server", StringComparer.Ordinal) &&
            !tool.SelectableScopes.Contains("trace", StringComparer.Ordinal)
                ? ToolScopeMode.Server
                : ToolScopeMode.Trace,
            requested,
            selected: null,
            candidates: Array.Empty<ToolScopeIdentity>(),
            included: Array.Empty<ToolScopeIdentity>(),
            pidReuseObserved: false,
            identityUnresolved: false);
    }

    private static ToolScopeSelector Requested(IReadOnlyDictionary<string, JsonElement>? arguments) =>
        new(
            ReadArgumentInt(arguments, "pid") ?? ReadArgumentInt(arguments, "awakenedPid") ?? ReadArgumentInt(arguments, "parentPid"),
            ReadArgumentLong(arguments, "processStartUs") ?? ReadArgumentLong(arguments, "awakenedProcessStartUs") ?? ReadArgumentLong(arguments, "targetProcessStartUs"),
            ReadArgumentInt(arguments, "tid"),
            ReadArgumentLong(arguments, "threadStartUs"),
            ReadArgumentLong(arguments, "threadGeneration")?.ToString(CultureInfo.InvariantCulture),
            ReadArgumentLong(arguments, "startUs"),
            ReadArgumentLong(arguments, "endUs"));

    private static ToolScopeIdentity? ReadProcessIdentity(JsonNode? node)
    {
        if (node is not JsonObject value)
            return null;
        var pid = ReadInt(value, "pid");
        var processStart = ReadLong(value, "startUs");
        if (pid is null || processStart is null)
            return null;
        return new ToolScopeIdentity(
            pid.Value,
            processStart.Value,
            Tid: null,
            ThreadStartUs: null,
            ThreadGeneration: null);
    }

    private static ToolScopeIdentity? ReadSelectedThreadIdentity(
        JsonObject thread,
        IReadOnlyList<ToolScopeIdentity> included)
    {
        if (thread["process"] is not JsonObject process ||
            ReadProcessIdentity(process) is not { } processIdentity ||
            ReadInt(thread, "tid") is not { } tid ||
            ReadLong(thread, "generation") is not { } generation)
        {
            return null;
        }
        var generationText = generation.ToString(CultureInfo.InvariantCulture);
        var candidate = included.SingleOrDefault(item =>
            item.Pid == processIdentity.Pid &&
            item.ProcessStartUs == processIdentity.ProcessStartUs &&
            item.Tid == tid &&
            string.Equals(item.ThreadGeneration, generationText, StringComparison.Ordinal));
        return new ToolScopeIdentity(
            processIdentity.Pid,
            processIdentity.ProcessStartUs,
            tid,
            candidate?.ThreadStartUs,
            generationText);
    }

    private static IReadOnlyList<ToolScopeIdentity> ReadProcessIdentities(JsonNode? node)
    {
        if (node is null)
            return Array.Empty<ToolScopeIdentity>();
        if (node is not JsonArray array)
            throw new InvalidOperationException("IncludedProcesses must be an array or null.");
        return array.Select((item, index) => ReadProcessIdentity(item) ??
                throw new InvalidOperationException($"IncludedProcesses[{index}] omitted its process identity."))
            .ToArray();
    }

    private static IReadOnlyList<ToolScopeIdentity> ReadThreadCandidates(JsonArray array) =>
        array.Select((item, index) => ReadThreadCandidate(item) ??
                throw new InvalidOperationException($"IncludedThreads[{index}] omitted its replayable thread identity."))
            .ToArray();

    private static ToolScopeIdentity? ReadThreadCandidate(JsonNode? node)
    {
        if (node is not JsonObject candidate ||
            candidate["thread"] is not JsonObject thread ||
            thread["process"] is not JsonObject process ||
            ReadProcessIdentity(process) is not { } processIdentity ||
            ReadInt(thread, "tid") is not { } tid ||
            ReadLong(thread, "generation") is not { } generation ||
            ReadLong(candidate, "threadStartUs") is not { } threadStartUs)
        {
            return null;
        }
        return new ToolScopeIdentity(
            processIdentity.Pid,
            processIdentity.ProcessStartUs,
            tid,
            threadStartUs,
            generation.ToString(CultureInfo.InvariantCulture));
    }

    internal static ToolScopeStatus ParseScopeStatus(string? status, bool threadRequested) => status switch
    {
        "not_evaluated" => ToolScopeStatus.NotEvaluated,
        "ok" => ToolScopeStatus.Ok,
        "scope_not_found" => threadRequested ? ToolScopeStatus.ThreadInstanceNotFound : ToolScopeStatus.ProcessInstanceNotFound,
        "process_instance_not_found" => ToolScopeStatus.ProcessInstanceNotFound,
        "thread_instance_not_found" => ToolScopeStatus.ThreadInstanceNotFound,
        "process_start_required" => ToolScopeStatus.ProcessStartRequired,
        "ambiguous_process_instance" => ToolScopeStatus.AmbiguousProcessInstance,
        "ambiguous_thread_instance" => ToolScopeStatus.AmbiguousThreadInstance,
        "identity_unresolved" => ToolScopeStatus.IdentityUnresolved,
        _ => throw new InvalidOperationException($"Unregistered reviewed scope status '{status ?? "<null>"}'."),
    };

    internal static ToolScopeMode ParseScopeMode(
        ActiveToolDefinition tool,
        string? mode,
        ToolScopeSelector requested)
    {
        if (mode is null)
            throw new InvalidOperationException($"Scoped tool '{tool.ToolName}' omitted reviewed ScopeMode.");
        return mode switch
        {
            "not_evaluated" when requested.Tid is not null => ToolScopeMode.ThreadInstance,
            "not_evaluated" when requested.ProcessStartUs is not null => ToolScopeMode.ProcessInstance,
            "not_evaluated" when requested.Pid is not null => ToolScopeMode.PidAggregate,
            "not_evaluated" => ToolScopeMode.Trace,
            "server" when tool.SelectableScopes.Contains("server", StringComparer.Ordinal) => ToolScopeMode.Server,
            "trace" when tool.SelectableScopes.Contains("trace", StringComparer.Ordinal) => ToolScopeMode.Trace,
            "all_processes" => ToolScopeMode.AllProcesses,
            "pid_aggregate" => ToolScopeMode.PidAggregate,
            "single_process" or "process_instance" => ToolScopeMode.ProcessInstance,
            "single_thread" or "thread_instance" => ToolScopeMode.ThreadInstance,
            // "unresolved" is an explicit analyzer result. Preserve selector intent
            // without pretending that an instance was resolved.
            "unresolved" when requested.Tid is not null => ToolScopeMode.ThreadInstance,
            "unresolved" when requested.ProcessStartUs is not null => ToolScopeMode.ProcessInstance,
            "unresolved" when requested.Pid is not null => ToolScopeMode.PidAggregate,
            "unresolved" => ToolScopeMode.Trace,
            _ => throw new InvalidOperationException(
                $"Scoped tool '{tool.ToolName}' emitted unregistered ScopeMode '{mode}'."),
        };
    }

    private static ToolError ScopeError(ToolScopeStatus status) => status switch
    {
        ToolScopeStatus.NotEvaluated => new(
            "invalid_argument",
            "The requested scope violates a pre-execution input limit; narrow the requested window and retry.",
            false),
        ToolScopeStatus.ProcessInstanceNotFound => new("process_instance_not_found", "No process instance matched the requested scope.", false),
        ToolScopeStatus.ProcessStartRequired => new("process_start_required", "The PID matched multiple lifetimes; retry with a candidate processStartUs.", false),
        ToolScopeStatus.AmbiguousProcessInstance => new("ambiguous_process_instance", "The requested process instance could not be resolved safely.", false),
        ToolScopeStatus.ThreadInstanceNotFound => new("thread_instance_not_found", "No thread instance matched the requested scope.", false),
        ToolScopeStatus.AmbiguousThreadInstance => new("ambiguous_thread_instance", "The requested thread instance could not be resolved safely.", false),
        _ => new("analysis_failed", "The requested identity could not be resolved safely.", false),
    };

    private static ToolPrecision Precision(Type type)
    {
        var containsFloatingPoint = ContainsFloatingPoint(type, new HashSet<Type>());
        return new ToolPrecision(
            ToolIdentifierPrecision.Exact,
            containsFloatingPoint ? ToolMetricPrecision.Mixed : ToolMetricPrecision.Exact,
            containsFloatingPoint ? "see_output_schema_x_metric_for_per_field_precision" : null,
            "per_field_x_metric_accounting; int64_uint64_canonical_decimal_strings",
            denominator: null);
    }

    private static bool ContainsFloatingPoint(Type declared, HashSet<Type> visited)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return true;
        if (type.IsPrimitive || type == typeof(string) || type.IsEnum) return false;
        if (ToolOutputSchemaFactory.SchemaBuilder.TryGetCollectionElement(type, out var element))
            return ContainsFloatingPoint(element, visited);
        if (ToolOutputSchemaFactory.SchemaBuilder.TryGetDictionaryArguments(type, out var key, out var value))
            return ContainsFloatingPoint(key, visited) || ContainsFloatingPoint(value, visited);
        if (!visited.Add(type)) return false;
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => ContainsFloatingPoint(property.PropertyType, visited));
    }

    internal static string NormalizeNoDataReason(string? value) => value switch
    {
        "event_class_not_observed" => "event_class_not_observed",
        "no_events_in_scope" => "no_events_in_scope",
        "no_completed_intervals_in_scope" or "no_completed_intervals" => "no_completed_intervals_in_scope",
        "unpaired_endpoints_in_scope" => "unpaired_endpoints_in_scope",
        "source_events_unattributed" => "source_events_unattributed",
        "stacks_unavailable" => "stacks_unavailable",
        "symbols_unresolved" => "symbols_unresolved",
        "focus_not_found" => "focus_not_found",
        "no_name_match" => "no_name_match",
        "no_candidates_in_considered_input" => "no_candidates_in_considered_input",
        "no_candidates_in_retained_input" => "no_candidates_in_retained_input",
        "no_capabilities_match_filter" => "no_capabilities_match_filter",
        "invalid_lifetime_boundaries" => "invalid_lifetime_boundaries",
        _ => throw new InvalidOperationException($"Unregistered reviewed no-data reason '{value ?? "<null>"}'."),
    };

    private static IReadOnlyList<string> ReadWarnings(JsonObject? value, string property)
    {
        if (value?[property] is null)
            return Array.Empty<string>();
        if (value[property] is not JsonArray array)
            throw new InvalidOperationException($"Reviewed warning field '{property}' must be an array.");

        return array.Select((item, index) => item switch
        {
            JsonValue scalar when scalar.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text) => text,
            JsonObject warning when
                ReadString(warning, "code") is { Length: > 0 } code &&
                ReadString(warning, "severity") is { Length: > 0 } severity &&
                ReadString(warning, "message") is { Length: > 0 } message =>
                    $"{severity}:{code}:{message}",
            _ => throw new InvalidOperationException(
                $"Reviewed warning field '{property}[{index}]' is neither a string nor a typed trace-quality warning."),
        }).ToArray();
    }

    private static string? ReadString(JsonObject? value, string property) =>
        value?[property] is JsonValue node && node.TryGetValue<string>(out var result) ? result : null;

    private static bool? ReadBoolean(JsonObject? value, string property) =>
        value?[property] is JsonValue node && node.TryGetValue<bool>(out var result) ? result : null;

    private static int? ReadInt(JsonObject? value, string property) =>
        value?[property] is JsonValue node && node.TryGetValue<int>(out var result) ? result : null;

    private static long? ReadLong(JsonObject? value, string property)
    {
        if (value?[property] is not JsonValue node) return null;
        if (node.TryGetValue<long>(out var number)) return number;
        if (node.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static int? ReadArgumentInt(IReadOnlyDictionary<string, JsonElement>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;

    private static long? ReadArgumentLong(IReadOnlyDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static string? ReadArgumentString(IReadOnlyDictionary<string, JsonElement>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal static class ToolEvidenceContractValidator
{
    internal static void Validate(ActiveToolDefinition tool, ToolEvidenceBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(boundary);
        var expectedIds = tool.EvidenceReferenceIds.ToHashSet(StringComparer.Ordinal);
        var actualIds = boundary.Items.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
        if (!expectedIds.SetEquals(actualIds))
            throw new InvalidOperationException($"Tool '{tool.ToolName}' runtime evidence IDs differ from its manifest.");

        var maximumRank = RelationshipRank(tool.MaximumRelationship);
        foreach (var item in boundary.Items)
        {
            if (!tool.AllowedMeasurementBases.Contains(Wire(item.MeasurementBasis), StringComparer.Ordinal))
                throw new InvalidOperationException($"Tool '{tool.ToolName}' emitted a measurement basis outside its manifest.");
            if (RelationshipRank(Wire(item.Relationship)) > maximumRank)
                throw new InvalidOperationException($"Tool '{tool.ToolName}' emitted a relationship stronger than its manifest.");
            if (item.Provenance.RuleId is not null && !tool.ConclusionRules.Contains(item.Provenance.RuleId, StringComparer.Ordinal))
                throw new InvalidOperationException($"Tool '{tool.ToolName}' emitted an unreviewed conclusion rule.");
            if ((item.Relationship == Relationship.Causal || item.ConclusionStatus == ConclusionStatus.Supported) &&
                item.Provenance.RuleId is null)
                throw new InvalidOperationException($"Tool '{tool.ToolName}' emitted a concluded relationship without a reviewed rule.");
            var expectedBoundaries = tool.Capabilities
                .Where(capability => capability.EvidenceReferences.Any(reference =>
                    string.Equals(reference.EvidenceId, item.EvidenceId, StringComparison.Ordinal)))
                .SelectMany(capability => capability.ConclusionBoundaryCodes)
                .Where(tool.DoesNotProve.Contains)
                .ToHashSet(StringComparer.Ordinal);
            if (!expectedBoundaries.SetEquals(item.DoesNotProve))
                throw new InvalidOperationException(
                    $"Tool '{tool.ToolName}' emitted an incorrect capability-specific evidence boundary for '{item.EvidenceId}'.");
        }
    }

    private static int RelationshipRank(string value) => value switch
    {
        "descriptive" => 0,
        "temporal" => 1,
        "association" => 2,
        "attribution" => 3,
        "causal" => 4,
        _ => throw new InvalidOperationException($"Unknown relationship '{value}'."),
    };

    private static string Wire<T>(T value) where T : struct, Enum
    {
        var field = typeof(T).GetField(value.ToString())!;
        return field.GetCustomAttribute<System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute>()?.Name
            ?? value.ToString();
    }
}
