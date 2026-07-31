using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

internal readonly record struct JitPairKey(
    ProcessInstanceKey Process,
    ushort ClrInstanceId,
    long MethodId);

internal readonly record struct JitStartData(string Method, int MethodIlSize);

internal readonly record struct JitStopData;

// Pair every JIT start/load event over the complete trace, then charge only the overlap with
// the requested window. This preserves methods that begin before or finish after the window.
public static class JitAnalysis
{
    public static JitAnalysisResponse Analyze(
        TraceLog trace,
        int? pid,
        int top,
        long? startUs,
        long? endUs)
    {
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = TimeWindowInput.Validate(startUs, endUs, maxDurationUs: null)
            .Resolve(traceEndUs, maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var pairer = new IntervalPairAccumulator<JitPairKey, JitStartData, JitStopData>();
        var incompleteIdentityCount = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.MethodJittingStarted += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = identities.Processes.Resolve(
                    data.ProcessID,
                    timestampUs,
                    processStartUs: null);
                var clrInstanceId = GcAnalysis.TryReadClrInstanceId(data);
                if (process.Status != InstanceResolutionStatus.Resolved ||
                    !process.Value.HasValue ||
                    !clrInstanceId.HasValue)
                {
                    incompleteIdentityCount++;
                    return;
                }

                var fullName =
                    $"{data.MethodNamespace}.{data.MethodName}{data.MethodSignature}";
                pairer.AddStart(
                    new JitPairKey(process.Value.Value, clrInstanceId.Value, data.MethodID),
                    timestampUs,
                    new JitStartData(fullName, (int)data.MethodILSize));
            };

            clr.MethodLoadVerbose += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = identities.Processes.ResolveAtEndpoint(
                    data.ProcessID,
                    timestampUs);
                var clrInstanceId = GcAnalysis.TryReadClrInstanceId(data);
                if (process.Status != InstanceResolutionStatus.Resolved ||
                    !process.Value.HasValue ||
                    !clrInstanceId.HasValue)
                {
                    incompleteIdentityCount++;
                    return;
                }

                pairer.AddStop(
                    new JitPairKey(process.Value.Value, clrInstanceId.Value, data.MethodID),
                    timestampUs,
                    new JitStopData());
            };
        });

        var result = pairer.Complete();
        var response = ProjectPairs(
            result.Pairs,
            window,
            pid,
            top,
            unmatchedIntervalCount:
                result.UnmatchedStarts.Count +
                result.UnmatchedStops.Count +
                incompleteIdentityCount,
            invalidIntervalCount: result.InvalidIntervals.Count);

        if (incompleteIdentityCount == 0)
            return response;

        return response with
        {
            Warnings = response.Warnings
                .Concat(
                [
                    $"identity_incomplete: skipped {incompleteIdentityCount} JIT endpoint events because their process or CLR instance identity was unresolved or ambiguous.",
                ])
                .ToArray(),
        };
    }

    internal static JitAnalysisResponse ProjectPairs(
        IReadOnlyList<PairedInterval<JitPairKey, JitStartData, JitStopData>> pairs,
        TimeWindow window,
        int? pid,
        int top,
        int unmatchedIntervalCount = 0,
        int invalidIntervalCount = 0)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (top < 1)
            throw new ArgumentOutOfRangeException(nameof(top), top, "Top must be positive.");

        var completed = new List<JitMethodRow>();
        long totalFullJitUs = 0;
        long totalAccountedJitUs = 0;

        foreach (var pair in pairs)
        {
            if (pid.HasValue && pair.Key.Process.Pid != pid.Value)
                continue;

            var projected = DurationAccounting.Project(pair, window);
            if (!projected.HasValue)
                continue;

            totalFullJitUs += projected.Value.FullDurationUs;
            totalAccountedJitUs += projected.Value.AccountedDurationUs;
            completed.Add(new JitMethodRow(
                Method: pair.StartData.Method,
                JitDurationUs: projected.Value.AccountedDurationUs,
                MethodIlSize: pair.StartData.MethodIlSize,
                Pid: pair.Key.Process.Pid,
                StartUs: pair.StartUs,
                EndUs: pair.EndUs,
                FullDurationUs: projected.Value.FullDurationUs,
                AccountedDurationUs: projected.Value.AccountedDurationUs,
                AccountingMode: DurationAccounting.ClippedOverlapMode,
                ProcessStartUs: pair.Key.Process.StartUs));
        }

        var rows = completed
            .OrderByDescending(row => row.AccountedDurationUs)
            .ThenBy(row => row.StartUs)
            .Take(top)
            .ToArray();

        var warnings = new List<string>();
        if (completed.Count == 0)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "JIT",
                "JIT",
                "or all executed code was R2R/NGen pre-compiled, or filtered out"));
        }
        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);

        return new JitAnalysisResponse(
            Pid: pid,
            TotalMethodsJitted: completed.Count,
            TotalJitUs: totalAccountedJitUs,
            TopMethods: rows,
            Warnings: warnings,
            TotalFullJitUs: totalFullJitUs,
            TotalAccountedJitUs: totalAccountedJitUs,
            HasMore: completed.Count > rows.Length,
            UnmatchedIntervalCount: unmatchedIntervalCount,
            InvalidIntervalCount: invalidIntervalCount,
            AccountingMode: DurationAccounting.ClippedOverlapMode);
    }
}
