using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class DiagnoseTools
{
    private readonly TraceCache _cache;
    public DiagnoseTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        "Composite 'why is process X slow to start' analysis. Picks the slowest-by-wait-ratio processes " +
        "(or the ones matching nameSubstring), then runs wait_analysis (top wait reasons), image_load_timing " +
        "(first N DLLs from process start), and cpu_top_functions (top hot functions in the startup window) " +
        "for each. Equivalent to manually composing list_processes + wait_analysis + image_load_timing + " +
        "cpu_top_functions but with a single tool call.")]
    public DiagnoseSlowStartupResponse DiagnoseSlowStartup(
        [Description("Absolute path to .etl file")] string path,
        [Description("Match candidates whose process name contains this substring (case-insensitive). " +
                     "Empty/null = pick the top candidates by wait ratio across the whole trace.")]
        string? nameSubstring = null,
        [Description("How many candidate processes to investigate (default 5, max 20)")] int maxCandidates = 5,
        [Description("Minimum WallUs / CpuUs ratio to consider a process 'slow' (default 3.0)")]
        double minWaitRatio = 3.0,
        [Description("Startup window width from ProcessStart, in microseconds (default 5_000_000 = 5s)")]
        long startupWindowUs = 5_000_000,
        [Description("Top N image-loads per candidate (default 30)")] int topImageLoads = 30,
        [Description("Top N CPU functions per candidate (default 15)")] int topCpu = 15)
    {
        if (maxCandidates <= 0 || maxCandidates > 20)
            throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        if (minWaitRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(minWaitRatio));
        if (startupWindowUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(startupWindowUs));

        var trace = _cache.Get(path);
        var warnings = new List<string>();

        // 1. Pick candidates via the shared ProcessProjection.
        IEnumerable<ProcessRow> rows = ProcessProjection.Rows(trace, includeSystem: false)
            .Where(r => r.WallUs > 0);
        if (!string.IsNullOrEmpty(nameSubstring))
            rows = rows.Where(r => r.Name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase));

        var ranked = rows
            .Where(r => r.WaitRatio is { } w && w >= minWaitRatio)
            .OrderByDescending(r => r.WaitRatio ?? 0)
            .ThenByDescending(r => r.WallUs)
            .Take(maxCandidates)
            .ToList();

        if (ranked.Count == 0)
        {
            warnings.Add(
                $"No processes matched (nameSubstring='{nameSubstring ?? "<any>"}', " +
                $"minWaitRatio={minWaitRatio}). Try lowering minWaitRatio or removing nameSubstring.");
            return new DiagnoseSlowStartupResponse(
                Candidates: new List<SlowStartupCandidate>(),
                Summary: "No candidates above minWaitRatio.",
                Warnings: warnings);
        }

        var candidatePids = new HashSet<int>(ranked.Select(r => r.Pid));

        // 2. ONE wait_analysis pass for the whole trace (CSwitch ~M-events; we'd otherwise
        //    re-walk it once per candidate). top=int.MaxValue is intentional: WaitAnalysis
        //    truncates AFTER the per-thread aggregation, and a global top-N would silently
        //    drop threads belonging to a candidate PID whose global rank doesn't make the
        //    cut, distorting per-PID reason histograms.
        var waitResp = WaitAnalysis.Analyze(trace, top: int.MaxValue, pid: null, startUs: null, endUs: null);
        var waitByPid = waitResp.Rows
            .Where(r => candidatePids.Contains(r.Pid))
            .GroupBy(r => r.Pid)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<WaitAnalysisRow>)g.ToList());

        // 3. ONE image-load pass for all candidates.
        var imageLoadsByPid = ImageLoadAnalysis.ForPids(trace, candidatePids);

        // 4. CPU is per-pid (one CallTree per pid, so no shared-pass shortcut). We accept N passes here.
        var candidates = new List<SlowStartupCandidate>();
        foreach (var c in ranked)
        {
            var collapsedReasons = waitByPid.TryGetValue(c.Pid, out var rowsForPid)
                ? rowsForPid
                    .SelectMany(r => r.TopWaitReasons)
                    .GroupBy(b => b.Reason)
                    .Select(g => new WaitReasonBucket(g.Key, g.Sum(b => b.BlockedUs), g.Sum(b => b.Count)))
                    .OrderByDescending(b => b.BlockedUs)
                    .Take(5)
                    .ToList()
                : new List<WaitReasonBucket>();

            var firstLoads = imageLoadsByPid.TryGetValue(c.Pid, out var loads)
                ? (IReadOnlyList<ImageLoadRow>)loads.Take(topImageLoads).ToList()
                : null;

            IReadOnlyList<CpuFunctionRow>? topCpuRows = null;
            try
            {
                var cpuResp = CpuAnalysis.TopFunctions(
                    trace, top: topCpu, pid: c.Pid,
                    startUs: c.StartUs, endUs: c.StartUs + startupWindowUs,
                    symbolLog: Console.Error,
                    excludeEtwSelfOverhead: true);
                topCpuRows = cpuResp.Rows;
            }
            catch (Exception ex)
            {
                warnings.Add($"cpu_top_functions for pid {c.Pid}: {ex.Message}");
            }

            candidates.Add(new SlowStartupCandidate(
                Pid: c.Pid,
                ParentPid: c.ParentPid,
                Name: c.Name,
                WallUs: c.WallUs,
                CpuUs: c.CpuUs,
                WaitRatio: c.WaitRatio,
                ImageLoadCount: c.ImageLoadCount,
                TopWaitReasons: collapsedReasons,
                FirstImageLoads: firstLoads,
                TopCpuFunctions: topCpuRows));
        }

        return new DiagnoseSlowStartupResponse(
            Candidates: candidates,
            Summary: BuildSummary(candidates),
            Warnings: warnings);
    }

    private static string BuildSummary(IReadOnlyList<SlowStartupCandidate> candidates)
    {
        if (candidates.Count == 0) return "No candidates.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {candidates.Count} slow-startup candidate(s):");
        foreach (var c in candidates)
        {
            var ratioStr = c.WaitRatio is { } r ? $"{r:F1}x" : "n/a";
            sb.AppendLine($"  - pid {c.Pid} ({c.Name}): wall={c.WallUs / 1000.0:F1}ms, cpu={c.CpuUs / 1000.0:F1}ms, wait_ratio={ratioStr}");
            if (c.TopWaitReasons.Count > 0)
            {
                var reasons = string.Join(", ", c.TopWaitReasons.Take(3).Select(b => b.Reason));
                sb.AppendLine($"    top wait reasons: {reasons}");
            }
        }
        return sb.ToString();
    }
}
