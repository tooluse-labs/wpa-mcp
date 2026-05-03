using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// .NET CLR JIT compilation analysis — top methods by JIT time and per-PID summary.
// PerfView equivalent: 'JIT Stats'.
//
// Two events bracket each method JIT:
//
//   MethodJittingStarted: fires when JIT begins compiling a method.  Carries MethodID,
//     MethodNamespace, MethodName, MethodSignature, MethodSize.
//   MethodLoadVerbose: fires when the JIT's output is loaded (after compilation completes).
//     Carries the same MethodID + the resulting method's address.
//
// Match by (ProcessID, MethodID) to compute per-method JIT duration.  R2R / NGen / pre-jitted
// methods don't fire JittingStarted, so they're invisible here — which is the right
// behaviour for "what's the JIT cost in this trace".
public static class JitAnalysis
{
    public static JitAnalysisResponse Analyze(TraceLog trace, int? pid, int top, long? startUs, long? endUs)
    {
        // (pid, methodId) → (startUs, fullName, methodSize)
        var pending = new Dictionary<(int pid, long methodId), (long startUs, string name, int size)>();
        var completed = new List<JitMethodRow>();
        long totalJitUs = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.MethodJittingStarted += data =>
            {
                var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (pid is { } p && data.ProcessID != p) return;
                if (startUs is { } s && nowUs < s) return;
                if (endUs is { } e && nowUs > e) return;

                var fullName = $"{data.MethodNamespace}.{data.MethodName}{data.MethodSignature}";
                pending[(data.ProcessID, data.MethodID)] = (nowUs, fullName, (int)data.MethodILSize);
            };

            clr.MethodLoadVerbose += data =>
            {
                var nowUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (pid is { } p && data.ProcessID != p) return;
                if (!pending.Remove((data.ProcessID, data.MethodID), out var s)) return;

                var dur = nowUs - s.startUs;
                totalJitUs += dur;
                completed.Add(new JitMethodRow(
                    Method: s.name,
                    JitDurationUs: dur,
                    MethodSize: s.size,
                    Pid: data.ProcessID));
            };
        });

        var rows = completed
            .OrderByDescending(r => r.JitDurationUs)
            .Take(top)
            .ToList();

        var warnings = new List<string>();
        if (completed.Count == 0)
        {
            warnings.Add(
                "No CLR JIT events matched. Either the trace lacks the .NET runtime ETW " +
                "provider (Microsoft-Windows-DotNETRuntime, JIT keyword), all the executed " +
                "code was R2R/NGen pre-compiled (no JIT happened), or the window/PID filter " +
                "excluded all events.");
        }

        return new JitAnalysisResponse(
            Pid: pid,
            TotalMethodsJitted: completed.Count,
            TotalJitUs: totalJitUs,
            TopMethods: rows,
            Warnings: warnings);
    }
}
