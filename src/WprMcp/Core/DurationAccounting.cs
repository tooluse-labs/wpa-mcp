using WprMcp.Analyzers;

namespace WprMcp.Core;

internal readonly record struct AccountedPairedInterval<TKey, TStart, TStop>(
    TKey Key,
    long StartUs,
    long EndUs,
    long FullDurationUs,
    long AccountedDurationUs,
    string AccountingMode,
    TStart StartData,
    TStop StopData) where TKey : notnull;

internal static class DurationAccounting
{
    public const string ClippedOverlapMode = "clipped_overlap_v2";

    public static AccountedPairedInterval<TKey, TStart, TStop>? Project<TKey, TStart, TStop>(
        PairedInterval<TKey, TStart, TStop> pair,
        TimeWindow window) where TKey : notnull
    {
        var accountedUs = window.IntersectDurationUs(pair.StartUs, pair.EndUs);
        return accountedUs == 0
            ? null
            : new AccountedPairedInterval<TKey, TStart, TStop>(
                pair.Key,
                pair.StartUs,
                pair.EndUs,
                pair.FullDurationUs,
                accountedUs,
                ClippedOverlapMode,
                pair.StartData,
                pair.StopData);
    }
}
