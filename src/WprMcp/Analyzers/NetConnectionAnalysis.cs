using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Per-connection lifecycle list — TCP Connect / Accept / Disconnect / Reconnect events
// matched on `connid` to give "connection X opened at T1, closed at T2, lasted T2−T1".
// PerfView's Events view can show the underlying rows but doesn't pair them; this is the
// "connect-to-disconnect latency outliers" tool.
//
// Roles:
//   Connect = outbound (this process initiated)
//   Accept  = inbound (this process accepted from a remote peer)
// IPv4 and IPv6 share the same `connid` namespace from the kernel's perspective and we merge
// them into one list with an IsIPv6 flag.
//
// `Reconnect` is treated as a close: the connection ID was reused, the prior session ended.
// Subsequent kernel events under the same connid then start a fresh row.  We don't track
// `Retransmit` events here — they're per-segment, not per-connection — see find_marker for
// per-retransmit volume.
//
// Some connections never see a Disconnect within the trace window.  Those rows have
// CloseTimeUs = null and Duration = null, with TraceResidentEnd = true (analogous to the
// thread-lifetime flag).
//
// Requires the NetworkTrace kernel keyword in the capture profile (NOT in default WPR
// 'CPU' / 'CPU.light' profiles; 'GeneralProfile' or a custom .wprp does).
public static class NetConnectionAnalysis
{
    public static NetConnectionsResponse Analyze(TraceLog trace, int? pid, int top, long? startUs, long? endUs)
    {
        var traceEndUs = (long)trace.SessionDuration.TotalMicroseconds;
        var open = new Dictionary<ulong, OpenSlot>();
        var rows = new List<NetConnectionRow>();

        void HandleOpen(int processId, ulong connid, string daddr, int dport, string saddr, int sport,
                        bool isIPv6, bool isAccept, long nowUs)
        {
            // If a Connect/Accept fires for an already-open connid, the prior one is treated
            // as if it terminated implicitly here — emit a closing row and start a fresh slot.
            if (open.Remove(connid, out var prior))
                rows.Add(prior.ToRow(closeTimeUs: nowUs, traceResidentEnd: false));
            open[connid] = new OpenSlot(processId, connid, daddr, dport, saddr, sport, isIPv6, isAccept, nowUs);
        }

        void HandleClose(ulong connid, long nowUs)
        {
            if (open.Remove(connid, out var slot))
                rows.Add(slot.ToRow(closeTimeUs: nowUs, traceResidentEnd: false));
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.TcpIpConnect      += d => HandleOpen(d.ProcessID, d.connid, d.daddr.ToString(), d.dport, d.saddr.ToString(), d.sport, isIPv6: false, isAccept: false, (long)(d.TimeStampRelativeMSec * 1000));
            kernel.TcpIpConnectIPV6  += d => HandleOpen(d.ProcessID, d.connid, d.daddr.ToString(), d.dport, d.saddr.ToString(), d.sport, isIPv6: true,  isAccept: false, (long)(d.TimeStampRelativeMSec * 1000));
            kernel.TcpIpAccept       += d => HandleOpen(d.ProcessID, d.connid, d.daddr.ToString(), d.dport, d.saddr.ToString(), d.sport, isIPv6: false, isAccept: true,  (long)(d.TimeStampRelativeMSec * 1000));
            kernel.TcpIpAcceptIPV6   += d => HandleOpen(d.ProcessID, d.connid, d.daddr.ToString(), d.dport, d.saddr.ToString(), d.sport, isIPv6: true,  isAccept: true,  (long)(d.TimeStampRelativeMSec * 1000));
            kernel.TcpIpDisconnect    += d => HandleClose(d.connid, (long)(d.TimeStampRelativeMSec * 1000));
            kernel.TcpIpDisconnectIPV6+= d => HandleClose(d.connid, (long)(d.TimeStampRelativeMSec * 1000));
            kernel.TcpIpReconnect     += d => HandleClose(d.connid, (long)(d.TimeStampRelativeMSec * 1000));
            kernel.TcpIpReconnectIPV6 += d => HandleClose(d.connid, (long)(d.TimeStampRelativeMSec * 1000));
        });

        // Connections still open at trace end.
        foreach (var slot in open.Values)
            rows.Add(slot.ToRow(closeTimeUs: traceEndUs, traceResidentEnd: true));

        // Apply pid + window filters now (kept off the hot path to preserve the connid
        // pairing — a Connect outside the window must still pair with a Disconnect inside).
        var filtered = rows.Where(r =>
        {
            if (pid is { } p && r.Pid != p) return false;
            if (startUs is { } s && r.OpenTimeUs < s) return false;
            if (endUs is { } e && r.OpenTimeUs >= e) return false;
            return true;
        }).ToList();

        var ordered = filtered
            .OrderByDescending(r => r.DurationUs ?? long.MaxValue)
            .Take(top)
            .ToList();

        var warnings = new List<string>();
        if (rows.Count == 0)
            warnings.Add(WarningBuilder.MissingKeyword("TcpIp Connect/Accept/Disconnect", "NetworkTrace"));

        return new NetConnectionsResponse(
            Pid: pid,
            TotalConnections: filtered.Count,
            Connections: ordered,
            Warnings: warnings);
    }

    private record OpenSlot(
        int Pid, ulong ConnId, string Daddr, int Dport, string Saddr, int Sport,
        bool IsIPv6, bool IsAccept, long OpenUs)
    {
        public NetConnectionRow ToRow(long closeTimeUs, bool traceResidentEnd) =>
            new(
                Pid: Pid,
                ConnId: ConnId,
                Role: IsAccept ? "accept" : "connect",
                IsIPv6: IsIPv6,
                LocalAddress: Saddr,
                LocalPort: Sport,
                RemoteAddress: Daddr,
                RemotePort: Dport,
                OpenTimeUs: OpenUs,
                CloseTimeUs: closeTimeUs,
                DurationUs: closeTimeUs - OpenUs,
                TraceResidentEnd: traceResidentEnd);
    }
}
