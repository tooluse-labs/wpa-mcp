using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tests;

public sealed class DurationAnalyzerInvariantTests
{
    public static IEnumerable<object[]> Sections()
    {
        yield return [DurationInvariantFixtures.Gc()];
        yield return [DurationInvariantFixtures.Jit()];
        yield return [DurationInvariantFixtures.SecurityScan()];
        yield return [DurationInvariantFixtures.Finalizer()];
        yield return [DurationInvariantFixtures.Contention()];
    }

    [Theory]
    [MemberData(nameof(Sections))]
    public void CompleteRowsAndTopRowsRespectAccountedTotal(DurationSectionProbe section)
    {
        Assert.Equal(
            section.CompleteRows.Sum(row => row.AccountedDurationUs),
            section.TotalAccountedDurationUs);
        Assert.True(
            section.ReturnedRows.Sum(row => row.AccountedDurationUs) <=
            section.TotalAccountedDurationUs);
        if (!section.HasMore)
        {
            Assert.Equal(
                section.TotalAccountedDurationUs,
                section.ReturnedRows.Sum(row => row.AccountedDurationUs));
        }
        Assert.All(section.CompleteRows, row =>
        {
            Assert.Equal("clipped_overlap_v2", row.AccountingMode);
            Assert.True(row.FullDurationUs >= row.AccountedDurationUs);
        });
    }

    [Theory]
    [MemberData(nameof(Sections))]
    public void LegacyAliasesAreAccountedAndWarn(DurationSectionProbe section)
    {
        Assert.Equal(section.TotalAccountedDurationUs, section.LegacyTotalUs);
        Assert.All(
            section.ReturnedRows,
            row => Assert.Equal(row.AccountedDurationUs, row.LegacyDurationUs));
        Assert.Contains(
            section.Warnings,
            warning => warning.StartsWith("time_semantics_v2:", StringComparison.Ordinal));
    }
}

public sealed record DurationRowProbe(
    long FullDurationUs,
    long AccountedDurationUs,
    long LegacyDurationUs,
    string AccountingMode);

public sealed record DurationSectionProbe(
    IReadOnlyList<DurationRowProbe> CompleteRows,
    IReadOnlyList<DurationRowProbe> ReturnedRows,
    long TotalAccountedDurationUs,
    long LegacyTotalUs,
    bool HasMore,
    IReadOnlyList<string> Warnings);

internal static class DurationInvariantFixtures
{
    private static readonly TimeWindow Window = new(100, 200);

    private static readonly (long StartUs, long EndUs)[] Spans =
    [
        (90, 130),
        (120, 180),
        (170, 210),
    ];

    public static DurationSectionProbe Gc()
    {
        var process = new ProcessInstanceKey(10, 0);
        var gcs = Spans.Select((span, index) =>
            new GcWallWithPauses(
                new ClrGcKey(process, 1, index + 1),
                span.StartUs,
                span.EndUs,
                span.EndUs - span.StartUs,
                Generation: index,
                Reason: "test",
                Pauses: Array.Empty<GcPauseInterval>()))
            .ToList();
        var response = GcAnalysis.Project(
            new GcIntervalSet(gcs, [], [], 0, 0, 0, 0, 0),
            Window,
            pid: 10);
        var rows = response.Events.Select(row => Probe(
            row.FullDurationUs,
            row.AccountedDurationUs,
            row.DurationUs,
            row.AccountingMode)).ToList();
        return new DurationSectionProbe(
            rows,
            rows,
            response.TotalAccountedGcUs,
            response.TotalGcUs,
            HasMore: false,
            response.Warnings);
    }

    public static DurationSectionProbe Jit()
    {
        var process = new ProcessInstanceKey(10, 0);
        var pairs = Spans.Select((span, index) =>
            new PairedInterval<JitPairKey, JitStartData, JitStopData>(
                new JitPairKey(process, 1, index + 1),
                span.StartUs,
                span.EndUs,
                new JitStartData($"Method{index}", index + 1),
                new JitStopData()))
            .ToList();
        var complete = JitAnalysis.ProjectPairs(
            pairs,
            Window,
            pid: 10,
            top: int.MaxValue);
        var returned = JitAnalysis.ProjectPairs(pairs, Window, pid: 10, top: 2);
        return new DurationSectionProbe(
            complete.TopMethods.Select(row => Probe(
                row.FullDurationUs,
                row.AccountedDurationUs,
                row.JitDurationUs,
                row.AccountingMode)).ToList(),
            returned.TopMethods.Select(row => Probe(
                row.FullDurationUs,
                row.AccountedDurationUs,
                row.JitDurationUs,
                row.AccountingMode)).ToList(),
            returned.TotalAccountedJitUs,
            returned.TotalJitUs,
            returned.HasMore,
            returned.Warnings);
    }

    public static DurationSectionProbe SecurityScan()
    {
        var pairs = Spans.Select((span, index) =>
            ScanPair(index, span.StartUs, span.EndUs)).ToList();
        var complete = SecurityScanAnalysis.ProjectPairs(
            pairs,
            Window,
            int.MaxValue,
            null,
            null,
            null,
            null);
        var returned = SecurityScanAnalysis.ProjectPairs(
            pairs,
            Window,
            2,
            null,
            null,
            null,
            null);
        return new DurationSectionProbe(
            complete.SlowScans.Select(row => Probe(
                row.FullDurationUs,
                row.AccountedDurationUs,
                row.DurationUs,
                row.AccountingMode)).ToList(),
            returned.SlowScans.Select(row => Probe(
                row.FullDurationUs,
                row.AccountedDurationUs,
                row.DurationUs,
                row.AccountingMode)).ToList(),
            returned.TotalAccountedDurationUs,
            returned.TotalDurationUs,
            returned.SlowScansHasMore,
            returned.Warnings);
    }

    public static DurationSectionProbe Finalizer()
    {
        var process = new ProcessInstanceKey(10, 0);
        var pairs = Spans.Select((span, index) =>
            new PairedInterval<FinalizerPairKey, FinalizerStartData, FinalizerStopData>(
                new FinalizerPairKey(process, 1),
                span.StartUs,
                span.EndUs,
                new FinalizerStartData(),
                new FinalizerStopData(index + 1)))
            .ToList();
        var response = FinalizerAnalysis.ProjectBatches(pairs, Window, pid: 10);
        var rows = response.Batches.Select(row => Probe(
            row.FullDurationUs,
            row.AccountedDurationUs,
            row.DurationUs,
            row.AccountingMode)).ToList();
        return new DurationSectionProbe(
            rows,
            rows,
            response.TotalAccountedBatchUs,
            response.TotalBatchUs,
            HasMore: false,
            response.Warnings);
    }

    public static DurationSectionProbe Contention()
    {
        var process = new ProcessInstanceKey(10, 0);
        var thread = new ThreadInstanceKey(process, 7, 1);
        var pairs = Spans.Select(span =>
            new PairedInterval<ThreadInstanceKey, ContentionStartData, ContentionStopData>(
                thread,
                span.StartUs,
                span.EndUs,
                new ContentionStartData(default),
                new ContentionStopData()))
            .ToList();
        var scope = new ThreadAnalysisScope(
            Window,
            Pid: 10,
            Process: new ProcessLifetime(
                process,
                EndUs: 300,
                StartObserved: true,
                EndObserved: true),
            Thread: null,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);
        var projection = ClrContentionStackAnalysis.ProjectIntervals(
            pairs,
            scope,
            unmatchedIntervalCount: 0,
            invalidIntervalCount: 0);
        var rows = projection.Samples.Select(row => Probe(
            row.FullDurationUs,
            row.AccountedDurationUs,
            row.AccountedDurationUs,
            row.AccountingMode)).ToList();
        return new DurationSectionProbe(
            rows,
            rows,
            projection.TotalAccountedDurationUs,
            projection.TotalAccountedDurationUs,
            HasMore: false,
            [WarningBuilder.LegacyAccountedDurationWarning]);
    }

    private static PairedInterval<
        SecurityScanPairKey,
        SecurityScanStartData,
        SecurityScanStopData> ScanPair(
        int index,
        long startUs,
        long endUs)
    {
        var emitter = new ProcessInstanceKey(4, 0);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__Source"] = "Microsoft Defender",
            ["__ProviderName"] = "Microsoft-Antimalware-Engine",
            ["__Id"] = $"scan-{index}",
            ["Path"] = $"c:\\file-{index}.dll",
            ["Process"] = "app.exe",
            ["PID"] = "10",
        };
        return new PairedInterval<
            SecurityScanPairKey,
            SecurityScanStartData,
            SecurityScanStopData>(
            new SecurityScanPairKey(
                emitter,
                "Microsoft-Antimalware-Engine",
                $"scan-{index}"),
            startUs,
            endUs,
            new SecurityScanStartData(fields),
            new SecurityScanStopData(fields));
    }

    private static DurationRowProbe Probe(
        long fullDurationUs,
        long accountedDurationUs,
        long legacyDurationUs,
        string accountingMode) =>
        new(fullDurationUs, accountedDurationUs, legacyDurationUs, accountingMode);
}
