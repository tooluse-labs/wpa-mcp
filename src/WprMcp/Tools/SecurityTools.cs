using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

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
        "matched events but do not by themselves turn all vendor activity into scans. " +
        "Use this after find_marker shows scan/security events, or when file/CPU/wait analysis " +
        "suggests AV/EDR interference.")]
    public SecurityScanAnalysisResponse SecurityScanAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N target/provider rows and slow scan rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to scan events associated with this PID when the provider exposes a target/process PID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Optional substring filter over target/related process path or process name")] string? processSubstring = null,
        [Description("Optional substring filter over scanned file/path fields")] string? pathSubstring = null,
        [Description("Optional substring filter over provider name or inferred security source")] string? providerSubstring = null)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return Analyzers.SecurityScanAnalysis.Analyze(trace, top, pid, startUs, endUs, processSubstring, pathSubstring, providerSubstring);
    }
}
