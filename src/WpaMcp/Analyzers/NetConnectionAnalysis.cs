using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Per-connection lifecycle list. Pairing identity is the emitting process
// lifetime plus connid; connid alone is not unique across processes or PID reuse.
public static class NetConnectionAnalysis
{
    public static NetConnectionsResponse Analyze(
        TraceLog trace,
        int? pid,
        int top,
        long? startUs,
        long? endUs,
        long? processStartUs = null)
    {
        var traceEndUs = TraceTime.FromMilliseconds(
            trace.SessionDuration.TotalMilliseconds);
        var window = new TimeWindow(startUs ?? 0, endUs ?? traceEndUs);
        var identities = TraceIdentityIndex.For(trace);
        var scope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        var events = new List<NetConnectionEvent>();

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.TcpIpConnect += data => events.Add(OpenEvent(
                data.ProcessID, data.connid, NetConnectionEventKind.Connect,
                data.TimeStampRelativeMSec,
                data.daddr.ToString(), data.dport,
                data.saddr.ToString(), data.sport,
                isIPv6: false));
            kernel.TcpIpConnectIPV6 += data => events.Add(OpenEvent(
                data.ProcessID, data.connid, NetConnectionEventKind.Connect,
                data.TimeStampRelativeMSec,
                data.daddr.ToString(), data.dport,
                data.saddr.ToString(), data.sport,
                isIPv6: true));
            kernel.TcpIpAccept += data => events.Add(OpenEvent(
                data.ProcessID, data.connid, NetConnectionEventKind.Accept,
                data.TimeStampRelativeMSec,
                data.daddr.ToString(), data.dport,
                data.saddr.ToString(), data.sport,
                isIPv6: false));
            kernel.TcpIpAcceptIPV6 += data => events.Add(OpenEvent(
                data.ProcessID, data.connid, NetConnectionEventKind.Accept,
                data.TimeStampRelativeMSec,
                data.daddr.ToString(), data.dport,
                data.saddr.ToString(), data.sport,
                isIPv6: true));
            kernel.TcpIpDisconnect += data => events.Add(CloseEvent(
                data.ProcessID, data.connid, NetConnectionEventKind.Disconnect,
                data.TimeStampRelativeMSec));
            kernel.TcpIpDisconnectIPV6 += data => events.Add(CloseEvent(
                data.ProcessID, data.connid, NetConnectionEventKind.Disconnect,
                data.TimeStampRelativeMSec));
            kernel.TcpIpReconnect += data => events.Add(CloseEvent(
                data.ProcessID, data.connid, NetConnectionEventKind.Reconnect,
                data.TimeStampRelativeMSec));
            kernel.TcpIpReconnectIPV6 += data => events.Add(CloseEvent(
                data.ProcessID, data.connid, NetConnectionEventKind.Reconnect,
                data.TimeStampRelativeMSec));
        });

        return AnalyzeResolved(traceEndUs, identities, events, pid, top, scope);
    }

    internal static NetConnectionsResponse AnalyzeEvents(
        long traceEndUs,
        IReadOnlyList<ProcessLifetime> processLifetimes,
        IReadOnlyList<NetConnectionEvent> events,
        int? pid,
        int top,
        TimeWindow window,
        long? processStartUs)
    {
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs,
            processLifetimes,
            Array.Empty<ThreadLifecycleEvent>());
        var scope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        return AnalyzeResolved(traceEndUs, identities, events, pid, top, scope);
    }

    private static NetConnectionsResponse AnalyzeResolved(
        long traceEndUs,
        TraceIdentityIndex identities,
        IReadOnlyList<NetConnectionEvent> events,
        int? pid,
        int top,
        ProcessAnalysisScope scope)
    {
        var open = new Dictionary<ConnectionInstanceKey, OpenSlot>();
        var projected = new List<ProjectedConnection>();
        long traceIdentityUnresolvedEndpointCount = 0;
        long scopedIdentityUnresolvedEndpointCount = 0;
        long matchedSourceEventCount = 0;
        long unpairedCloseCount = 0;

        foreach (var item in events
                     .Select((value, index) => (value, index))
                     .OrderBy(item => item.value.TimeUs)
                     .ThenBy(item => item.index))
        {
            var observation = item.value;
            if (!scope.IsResolved)
                continue;
            if (scope.Pid.HasValue && observation.Pid != scope.Pid.Value)
                continue;
            var processResolution = observation.Kind is
                NetConnectionEventKind.Disconnect or NetConnectionEventKind.Reconnect
                ? identities.Processes.ResolveAtEndpoint(
                    observation.Pid, observation.TimeUs)
                : identities.Processes.Resolve(
                    observation.Pid,
                    observation.TimeUs,
                    processStartUs: null);
            if (processResolution.Status != InstanceResolutionStatus.Resolved ||
                !processResolution.Value.HasValue)
            {
                traceIdentityUnresolvedEndpointCount++;
                if (scope.Window.ContainsPoint(observation.TimeUs))
                    scopedIdentityUnresolvedEndpointCount++;
                continue;
            }

            var process = processResolution.Value.Value;
            if (!scope.IncludedProcesses.Contains(process))
                continue;
            if (scope.Window.ContainsPoint(observation.TimeUs))
                matchedSourceEventCount++;
            var key = new ConnectionInstanceKey(process, observation.ConnId);
            if (observation.Kind is NetConnectionEventKind.Connect or NetConnectionEventKind.Accept)
            {
                if (open.Remove(key, out var prior))
                {
                    projected.Add(prior.Close(
                        observation.TimeUs,
                        endState: "replaced_open"));
                }
                open[key] = new OpenSlot(process, observation);
                continue;
            }

            if (open.Remove(key, out var slot))
            {
                projected.Add(slot.Close(
                    observation.TimeUs,
                    observation.Kind == NetConnectionEventKind.Reconnect
                        ? "reconnect"
                        : "disconnect"));
            }
            else if (scope.Window.ContainsPoint(observation.TimeUs))
            {
                unpairedCloseCount++;
            }
        }

        long processEndUnobservedCount = 0;
        foreach (var slot in open.Values)
        {
            var lifetime = identities.Processes.FindExact(slot.Process)
                .OrderByDescending(candidate => candidate.EndUs)
                .FirstOrDefault();
            var intervalEndUs = lifetime is null
                ? traceEndUs
                : Math.Min(traceEndUs, lifetime.EndUs);
            var traceResidentEnd = lifetime is null ||
                                   (!lifetime.EndObserved && intervalEndUs == traceEndUs);
            if (!traceResidentEnd)
                processEndUnobservedCount++;
            projected.Add(slot.CloseUnobserved(intervalEndUs, traceResidentEnd));
        }

        var filtered = projected
            .Where(connection =>
                connection.Row.OpenTimeUs < scope.Window.EndUs &&
                connection.IntervalEndUs > scope.Window.StartUs)
            .ToArray();
        var ordered = filtered
            .OrderByDescending(connection =>
                connection.Row.DurationUs ?? long.MaxValue)
            .ThenBy(connection => connection.Row.Pid)
            .ThenBy(connection => connection.Row.ProcessStartUs)
            .ThenBy(connection => connection.Row.ConnId)
            .Take(top)
            .Select(connection => connection.Row)
            .ToArray();

        var warnings = new List<string>();
        if (events.Count == 0)
        {
            warnings.Add(WarningBuilder.MissingKeyword(
                "TcpIp Connect/Accept/Disconnect", "NetworkTrace"));
        }
        else if (filtered.Length == 0 &&
                 scope.IsResolved &&
                 scopedIdentityUnresolvedEndpointCount > 0)
        {
            warnings.Add(
                $"source_events_unattributed: {scopedIdentityUnresolvedEndpointCount:N0} network endpoint(s) had the selected raw PID and an in-window timestamp, but process-lifetime identity was unresolved; no connection lifecycle attribution was guessed.");
        }
        else if (filtered.Length == 0 && scope.IsResolved && unpairedCloseCount > 0)
        {
            warnings.Add(
                $"unpaired_network_close: {unpairedCloseCount:N0} in-scope Disconnect/Reconnect endpoint(s) had no preceding Connect/Accept for the same process instance and connid; no completed connection lifecycle could be reconstructed.");
        }
        else if (filtered.Length == 0 && scope.IsResolved)
        {
            warnings.Add(
                "no_matching_network_connections: network lifecycle events were observed, but no connection lifecycle matched the selected process scope and half-open window.");
        }
        if (!scope.IsResolved)
        {
            warnings.Add(ProcessAnalysisScope.ResolutionFailureWarning(
                scope.ScopeStatus));
        }
        if (scope.ScopeMode == "pid_aggregate")
        {
            warnings.Add(
                "pid_aggregate: pid-only scope explicitly includes multiple process lifetimes; " +
                "connections remain separated by ProcessStartUs.");
        }
        if (traceIdentityUnresolvedEndpointCount > 0)
        {
            warnings.Add(
                $"network_process_identity_unresolved: {traceIdentityUnresolvedEndpointCount:N0} selected-PID/all-process endpoint(s) could not be tied to a process lifetime; {scopedIdentityUnresolvedEndpointCount:N0} were inside the requested half-open window.");
        }
        if (processEndUnobservedCount > 0)
        {
            warnings.Add(
                $"connection_end_unobserved: {processEndUnobservedCount:N0} connection(s) lacked a Disconnect/Reconnect before the owning process ended; CloseTimeUs and DurationUs are null.");
        }

        return new NetConnectionsResponse(
            Pid: pid,
            TotalConnections: filtered.Length,
            Connections: ordered,
            Warnings: warnings,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses,
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: scope.IsResolved
                ? matchedSourceEventCount > 0 || filtered.Length > 0
                    ? "observed"
                    : events.Count == 0
                        ? "not_observed"
                        : "unknown"
                : "unknown",
            MatchedEventCount: matchedSourceEventCount,
            NoDataReason: !scope.IsResolved
                ? scope.ScopeStatus
                : events.Count == 0
                    ? "event_class_not_observed"
                    : filtered.Length == 0
                        ? scopedIdentityUnresolvedEndpointCount > 0
                            ? "source_events_unattributed"
                            : unpairedCloseCount > 0
                            ? "unpaired_endpoints_in_scope"
                            : "no_events_in_scope"
                        : null,
            UnpairedCloseCount: unpairedCloseCount,
            TraceIdentityUnresolvedEndpointCount:
                traceIdentityUnresolvedEndpointCount,
            ScopedIdentityUnresolvedEndpointCount:
                scopedIdentityUnresolvedEndpointCount);
    }

    private static NetConnectionEvent OpenEvent(
        int pid,
        ulong connId,
        NetConnectionEventKind kind,
        double timestampMilliseconds,
        string remoteAddress,
        int remotePort,
        string localAddress,
        int localPort,
        bool isIPv6) =>
        new(
            pid,
            connId,
            kind,
            TraceTime.FromMilliseconds(timestampMilliseconds),
            remoteAddress,
            remotePort,
            localAddress,
            localPort,
            isIPv6);

    private static NetConnectionEvent CloseEvent(
        int pid,
        ulong connId,
        NetConnectionEventKind kind,
        double timestampMilliseconds) =>
        new(pid, connId, kind, TraceTime.FromMilliseconds(timestampMilliseconds));

    private readonly record struct ConnectionInstanceKey(
        ProcessInstanceKey Process,
        ulong ConnId);

    private sealed record OpenSlot(
        ProcessInstanceKey Process,
        NetConnectionEvent Open)
    {
        public ProjectedConnection Close(long closeTimeUs, string endState) =>
            new(
                ToRow(
                    closeTimeUs,
                    durationUs: checked(closeTimeUs - Open.TimeUs),
                    traceResidentEnd: false,
                    endState),
                closeTimeUs);

        public ProjectedConnection CloseUnobserved(
            long intervalEndUs,
            bool traceResidentEnd) =>
            new(
                ToRow(
                    closeTimeUs: null,
                    durationUs: null,
                    traceResidentEnd,
                    traceResidentEnd
                        ? "trace_end_unobserved"
                        : "process_end_unobserved"),
                intervalEndUs);

        private NetConnectionRow ToRow(
            long? closeTimeUs,
            long? durationUs,
            bool traceResidentEnd,
            string endState) =>
            new(
                Pid: Process.Pid,
                ConnId: Open.ConnId,
                Role: Open.Kind == NetConnectionEventKind.Accept
                    ? "accept"
                    : "connect",
                IsIPv6: Open.IsIPv6,
                LocalAddress: Open.LocalAddress,
                LocalPort: Open.LocalPort,
                RemoteAddress: Open.RemoteAddress,
                RemotePort: Open.RemotePort,
                OpenTimeUs: Open.TimeUs,
                CloseTimeUs: closeTimeUs,
                DurationUs: durationUs,
                TraceResidentEnd: traceResidentEnd,
                ProcessStartUs: Process.StartUs,
                EndState: endState);
    }

    private sealed record ProjectedConnection(
        NetConnectionRow Row,
        long IntervalEndUs);
}

internal enum NetConnectionEventKind
{
    Connect,
    Accept,
    Disconnect,
    Reconnect,
}

internal readonly record struct NetConnectionEvent(
    int Pid,
    ulong ConnId,
    NetConnectionEventKind Kind,
    long TimeUs,
    string RemoteAddress = "",
    int RemotePort = 0,
    string LocalAddress = "",
    int LocalPort = 0,
    bool IsIPv6 = false);
