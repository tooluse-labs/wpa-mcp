using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

internal readonly record struct ImageLoadObservation(
    int Pid,
    long TimeUs,
    string FileName,
    long ImageSize);

// Per-process DLL/image-load sequence with timestamps relative to process start.
//
// Mirrors PerfView's "Image Load Stacks" view (src/TraceEvent/Computers/ImageLoadStackComputer.cs)
// but without the stack source — we just want the ordered list, not a flame graph. Each ImageLoad
// kernel event marks "the kernel mapped this PE into the process's address space"; for "why is
// process X slow to start", the Loader serializes most of these so the *count* and *spread* across
// the process startup window are typically the relevant signals.
//
// True per-DLL load duration requires correlating ImageLoad with the loading thread's blocked-time
// (a job for WaitAnalysis on the LdrpSnapModule region). v1 here just orders the events and reports
// the offset from ProcessStart.
public static class ImageLoadAnalysis
{
    /// <summary>
    /// Bucket ImageLoad events by exact process instance in a single trace pass.
    /// Returns ordered (chronological) lists per requested instance; instances with no
    /// matching events map to an empty list.
    /// </summary>
    internal static Dictionary<ProcessInstanceKey, List<ImageLoadRow>> ForProcesses(
        TraceLog trace,
        IReadOnlyCollection<ProcessInstanceKey> processes)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(processes);
        var lifetimes = TraceIdentityIndex.For(trace).Processes.Lifetimes;
        var selected = processes
            .Distinct()
            .ToDictionary(
                key => key,
                key => SelectProcessInstance(lifetimes, key.Pid, key.StartUs));
        var pidSet = selected.Keys.Select(key => key.Pid).ToHashSet();
        var observations = pidSet.ToDictionary(
            pid => pid,
            _ => new List<ImageLoadObservation>());

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ImageLoad += data =>
            {
                if (!pidSet.Contains(data.ProcessID)) return;
                var tsUs = (long)(data.TimeStampRelativeMSec * 1000);
                observations[data.ProcessID].Add(new ImageLoadObservation(
                    data.ProcessID,
                    tsUs,
                    data.FileName ?? "<unknown>",
                    data.ImageSize));
            };
        });

        return selected.ToDictionary(
            item => item.Key,
            item => ProjectLoads(observations[item.Key.Pid], item.Value));
    }

    public static ImageLoadTimingResponse PerProcess(
        TraceLog trace,
        int pid,
        int top,
        long? processStartUs = null)
    {
        if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top));

        var identities = TraceIdentityIndex.For(trace);
        var scope = ResolveSingleProcessScope(
            identities, pid, processStartUs);
        if (!scope.IsResolved)
        {
            return new ImageLoadTimingResponse(
                Pid: pid,
                ProcessName: string.Empty,
                ProcessStartUs: null,
                TotalImageLoads: 0,
                FirstLoadOffsetUs: null,
                MaxGapUs: null,
                Loads: [],
                Warnings:
                [
                    ProcessAnalysisScope.ResolutionFailureWarning(scope.ScopeStatus),
                ],
                SelectedProcess: null,
                ScopeMode: scope.ScopeMode,
                PidReuseObserved: scope.PidReuseObserved,
                IncludedProcesses: scope.IncludedProcesses,
                ScopeStatus: scope.ScopeStatus,
                CapabilityStatus: "unknown",
                MatchedEventCount: 0,
                NoDataReason: scope.ScopeStatus);
        }

        var process = ExactLifetime(identities, scope.SelectedProcess!.Value);
        var collected = CollectAndSortLoads(trace, process);
        var withGaps = collected.Loads;
        var totalLoads = withGaps.Count;
        var truncated = withGaps.Take(top).ToList();

        long? firstLoadOffset = withGaps.Count > 0 ? withGaps[0].TimeFromProcessStartUs : (long?)null;
        // MaxGapUs requires at least 2 loads (first row's GapFromPrevUs is null by definition).
        long? maxGap = withGaps.Count >= 2
            ? withGaps.Skip(1).Max(r => r.GapFromPrevUs!.Value)
            : (long?)null;

        var warnings = new List<string>();
        if (totalLoads == 0)
        {
            warnings.Add(collected.GlobalEventCount == 0
                ? "event_class_not_observed: no ImageLoad events were observed in the materialized trace. This does not prove that Loader capture was disabled."
                : "no_events_in_scope: ImageLoad events were observed in the trace, but none matched the selected process lifetime.");
        }

        return new ImageLoadTimingResponse(
            Pid: pid,
            ProcessName: ProcessName(trace, process.Key),
            ProcessStartUs: process.Key.StartUs,
            TotalImageLoads: totalLoads,
            FirstLoadOffsetUs: firstLoadOffset,
            MaxGapUs: maxGap,
            Loads: truncated,
            Warnings: warnings,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses,
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: totalLoads > 0
                ? "observed"
                : collected.GlobalEventCount == 0
                    ? "not_observed"
                    : "unknown",
            MatchedEventCount: totalLoads,
            NoDataReason: totalLoads > 0
                ? null
                : collected.GlobalEventCount == 0
                    ? "event_class_not_observed"
                    : "no_events_in_scope");
    }

    // Returns the top-N loads with the largest GapFromPrevUs (the "where did the loader
    // freeze for a long stretch" question). Different ordering from PerProcess (which is
    // chronological); same underlying event walk.
    public static ImageLoadTopGapsResponse TopGaps(
        TraceLog trace,
        int pid,
        int top,
        long? processStartUs = null)
    {
        if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top));

        var identities = TraceIdentityIndex.For(trace);
        var scope = ResolveSingleProcessScope(
            identities, pid, processStartUs);
        if (!scope.IsResolved)
        {
            return new ImageLoadTopGapsResponse(
                Pid: pid,
                ProcessName: string.Empty,
                ProcessStartUs: null,
                TotalImageLoads: 0,
                FirstLoadOffsetUs: null,
                TopGaps: [],
                Warnings:
                [
                    ProcessAnalysisScope.ResolutionFailureWarning(scope.ScopeStatus),
                ],
                SelectedProcess: null,
                ScopeMode: scope.ScopeMode,
                PidReuseObserved: scope.PidReuseObserved,
                IncludedProcesses: scope.IncludedProcesses,
                ScopeStatus: scope.ScopeStatus,
                CapabilityStatus: "unknown",
                MatchedEventCount: 0,
                NoDataReason: scope.ScopeStatus);
        }

        var process = ExactLifetime(identities, scope.SelectedProcess!.Value);
        var collected = CollectAndSortLoads(trace, process);
        var withGaps = collected.Loads;
        var totalLoads = withGaps.Count;
        long? firstLoadOffset = withGaps.Count > 0 ? withGaps[0].TimeFromProcessStartUs : (long?)null;

        // Skip the first row (no prior, GapFromPrevUs=null). Sort the rest by gap descending.
        var topGaps = withGaps.Skip(1)
            .Where(r => r.GapFromPrevUs.HasValue)
            .OrderByDescending(r => r.GapFromPrevUs!.Value)
            .Take(top)
            .ToList();

        var warnings = new List<string>();
        if (totalLoads == 0)
        {
            warnings.Add(collected.GlobalEventCount == 0
                ? "event_class_not_observed: no ImageLoad events were observed in the materialized trace. This does not prove that Loader capture was disabled."
                : "no_events_in_scope: ImageLoad events were observed in the trace, but none matched the selected process lifetime.");
        }
        else if (totalLoads < 2)
        {
            warnings.Add("Only one ImageLoad event in this PID; gap analysis requires at least two.");
        }

        return new ImageLoadTopGapsResponse(
            Pid: pid,
            ProcessName: ProcessName(trace, process.Key),
            ProcessStartUs: process.Key.StartUs,
            TotalImageLoads: totalLoads,
            FirstLoadOffsetUs: firstLoadOffset,
            TopGaps: topGaps,
            Warnings: warnings,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses,
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: totalLoads > 0
                ? "observed"
                : collected.GlobalEventCount == 0
                    ? "not_observed"
                    : "unknown",
            MatchedEventCount: totalLoads,
            NoDataReason: totalLoads > 0
                ? null
                : collected.GlobalEventCount == 0
                    ? "event_class_not_observed"
                    : "no_events_in_scope");
    }

    internal static ProcessLifetime SelectProcessInstance(
        IEnumerable<ProcessLifetime> lifetimes,
        int pid,
        long? processStartUs)
    {
        ArgumentNullException.ThrowIfNull(lifetimes);
        var candidates = lifetimes
            .Where(lifetime =>
                lifetime.Key.Pid == pid &&
                (!processStartUs.HasValue ||
                 lifetime.Key.StartUs == processStartUs.Value))
            .GroupBy(lifetime => lifetime.Key)
            .Select(group => group.OrderByDescending(lifetime => lifetime.EndUs).First())
            .OrderBy(lifetime => lifetime.Key.StartUs)
            .ToArray();

        if (candidates.Length == 1)
            return candidates[0];
        if (candidates.Length == 0)
        {
            var code = processStartUs.HasValue
                ? "process_instance_not_found"
                : "pid_not_found";
            throw new ArgumentException(
                $"{code}: PID {pid}" +
                (processStartUs.HasValue
                    ? $" with processStartUs={processStartUs.Value}"
                    : string.Empty) +
                " was not found in the trace.",
                nameof(pid));
        }

        throw ProcessAnalysisScope.ProcessStartRequiredException(
            pid,
            candidates.Select(candidate => candidate.Key),
            nameof(pid));
    }

    internal static List<ImageLoadRow> ProjectLoads(
        IEnumerable<ImageLoadObservation> observations,
        ProcessLifetime process)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var ordered = observations
            .Where(observation =>
                observation.Pid == process.Key.Pid &&
                process.Contains(observation.TimeUs))
            .OrderBy(observation => observation.TimeUs)
            .Select(observation => new ImageLoadRow(
                TimeUs: observation.TimeUs,
                TimeFromProcessStartUs: observation.TimeUs - process.Key.StartUs,
                FileName: observation.FileName,
                ImageSize: observation.ImageSize,
                GapFromPrevUs: null))
            .ToList();
        return FillGaps(ordered);
    }

    private static (List<ImageLoadRow> Loads, long GlobalEventCount) CollectAndSortLoads(
        TraceLog trace,
        ProcessLifetime process)
    {
        var loads = new List<ImageLoadObservation>();
        long globalEventCount = 0;
        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ImageLoad += data =>
            {
                globalEventCount++;
                if (data.ProcessID != process.Key.Pid) return;
                var tsUs = (long)(data.TimeStampRelativeMSec * 1000);
                loads.Add(new ImageLoadObservation(
                    data.ProcessID,
                    tsUs,
                    data.FileName ?? "<unknown>",
                    data.ImageSize));
            };
        });
        return (ProjectLoads(loads, process), globalEventCount);
    }

    private static ProcessAnalysisScope ResolveSingleProcessScope(
        TraceIdentityIndex identities,
        int pid,
        long? processStartUs)
    {
        return ProcessAnalysisScope.Resolve(
            new TimeWindow(0, identities.TraceEndUs),
            pid,
            processStartUs,
            identities).RequireSingleProcess();
    }

    private static ProcessLifetime ExactLifetime(
        TraceIdentityIndex identities,
        ProcessInstanceKey process) =>
        identities.Processes.FindExact(process)
            .OrderByDescending(candidate => candidate.EndUs)
            .First();

    private static string ProcessName(TraceLog trace, ProcessInstanceKey key) =>
        trace.Processes
            .Where(process => process.ProcessID == key.Pid)
            .FirstOrDefault(process =>
                TraceTime.FromMilliseconds(process.StartTimeRelativeMsec) == key.StartUs)
            ?.Name ?? string.Empty;

    private static List<ImageLoadRow> FillGaps(List<ImageLoadRow> ordered)
    {
        if (ordered.Count == 0) return ordered;
        var result = new List<ImageLoadRow>(ordered.Count) { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var cur = ordered[i];
            result.Add(cur with { GapFromPrevUs = cur.TimeUs - prev.TimeUs });
        }
        return result;
    }
}
