using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class SecurityTools
{
    private readonly TraceCache _cache;
    public SecurityTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Aggregates security-scan ETW activity across Microsoft Defender/Antimalware and scan-like events " +
        "from third-party security providers such as Aliedr/Alibaba, 360/Qihoo, PCManager, Sense, CrowdStrike, " +
        "and other EDR/AV products. Defender StreamScanRequestTask Start/Stop events are paired to expose " +
        "scan durations; providers without public paired scan events degrade to event-count/provider/path " +
        "evidence when their provider or event names expose scan/security terms. Vendor names classify " +
        "matched events but do not by themselves turn all vendor activity into scans. Rows expose evidence " +
        "kind, provenance, and confidence: known Defender schemas are high confidence, while name heuristics " +
        "are low-confidence presence evidence and do not establish exact duration or root cause. " +
        "The pid selector means the payload target PID, not the provider/emitter PID. When a provider omits " +
        "a payload target PID, the legacy emitter fallback is retained but explicitly provenance-marked. " +
        "Use this after find_marker shows scan/security events, or when file/CPU/wait analysis " +
        "suggests AV/EDR interference.")]
    public SecurityScanAnalysisResponse SecurityScanAnalysis(
        [Description("Canonical TraceId returned by load_trace")] string path,
        [Description("Top N target/provider rows and slow scan rows (default 50, max 1000)")] int top = 50,
        [Description("Filter by payload target PID (not provider/emitter PID); events without a payload target PID use only the explicitly reported emitter fallback")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Optional substring filter over target/related process path or process name")] string? processSubstring = null,
        [Description("Optional substring filter over scanned file/path fields")] string? pathSubstring = null,
        [Description("Optional substring filter over provider name or inferred security source")] string? providerSubstring = null,
        [Description("Optional trace-relative start time selecting one lifetime of the target PID; requires pid. PID-only queries aggregate matching lifetimes explicitly.")] long? targetProcessStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(
            pid,
            tid: null,
            processStartUs: targetProcessStartUs,
            threadStartUs: null);
        Validation.RequireTop(top);
        if (processSubstring is not null)
            Validation.RequireText(processSubstring, allowEmpty: true);
        if (pathSubstring is not null)
            Validation.RequireText(pathSubstring, allowEmpty: true);
        if (providerSubstring is not null)
            Validation.RequireText(providerSubstring, allowEmpty: true);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return Analyzers.SecurityScanAnalysis.Analyze(
            trace, top, pid, window.StartUs, window.EndUs,
            processSubstring, pathSubstring, providerSubstring, targetProcessStartUs);
    }
}
