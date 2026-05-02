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

        // 1. Pick candidates from list_processes.
        var processes = trace.Processes
            .Where(p => p.ProcessID != 0 && p.ProcessID != 4)
            .Select(p =>
            {
                var startUs = (long)(p.StartTimeRelativeMsec * 1000);
                var endUs = (long)(p.EndTimeRelativeMsec * 1000);
                var wallUs = Math.Max(0, endUs - startUs);
                var cpuUs = (long)(p.CPUMSec * 1000);
                double? ratio = cpuUs > 0 ? (double)wallUs / cpuUs : (double?)null;
                return new
                {
                    Pid = p.ProcessID,
                    ParentPid = p.ParentID,
                    Name = p.Name ?? string.Empty,
                    StartUs = startUs,
                    EndUs = endUs,
                    WallUs = wallUs,
                    CpuUs = cpuUs,
                    Ratio = ratio,
                    ImageLoadCount = p.LoadedModules.Count(),
                };
            })
            .Where(x => x.WallUs > 0); // can't analyze processes without measurable lifetime

        if (!string.IsNullOrEmpty(nameSubstring))
        {
            processes = processes.Where(x =>
                x.Name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase));
        }

        var ranked = processes
            .Where(x => x.Ratio is { } r && r >= minWaitRatio)
            .OrderByDescending(x => x.Ratio ?? 0)
            .ThenByDescending(x => x.WallUs)
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

        // 2. For each candidate, gather wait reasons + image loads + top CPU functions.
        var candidates = new List<SlowStartupCandidate>();
        foreach (var c in ranked)
        {
            // Wait reasons (per-process, all threads collapsed into top buckets).
            var waitResp = WaitAnalysis.Analyze(trace, top: 200, pid: c.Pid, startUs: null, endUs: null);
            var collapsedReasons = waitResp.Rows
                .SelectMany(r => r.TopWaitReasons)
                .GroupBy(b => b.Reason)
                .Select(g => new WaitReasonBucket(
                    g.Key,
                    g.Sum(b => b.BlockedUs),
                    g.Sum(b => b.Count)))
                .OrderByDescending(b => b.BlockedUs)
                .Take(5)
                .ToList();

            IReadOnlyList<ImageLoadRow>? firstLoads = null;
            try
            {
                var ilResp = ImageLoadAnalysis.PerProcess(trace, c.Pid, topImageLoads);
                firstLoads = ilResp.Loads;
            }
            catch (Exception ex)
            {
                warnings.Add($"image_load_timing for pid {c.Pid}: {ex.Message}");
            }

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
                WaitRatio: c.Ratio,
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
