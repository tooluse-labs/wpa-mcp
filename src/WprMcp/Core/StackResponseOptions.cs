namespace WprMcp.Core;

internal static class StackResponseOptions
{
    public const int CompactTopLimit = 25;
    public const string CompactStacksDescription =
        "Return lossy token-compact stack output for token-constrained clients. Current rows are already frame summaries; compact mode caps row count at the documented compact limit regardless of the requested top. Rerun with compactStacks=false, optionally with a larger top, when long-tail detail matters.";
    public const string SummaryOnlyDescription =
        "Return a lossy smaller leaf/metric summary by capping row count at the documented compact limit regardless of the requested top. Rerun with summaryOnly=false, optionally with a larger top, when long-tail detail matters.";

    // Approximate Claude Code's 10k/25k token response budgets for ASCII-heavy JSON
    // as serialized bytes. The guard is byte-based, not a tokenizer estimate.
    public const int WarningResponseBytes = 40_000;
    public const int MaximumResponseBytes = 100_000;

    public static int EffectiveTop(int requestedTop, bool compactStacks, bool summaryOnly)
        => compactStacks || summaryOnly ? Math.Min(requestedTop, CompactTopLimit) : requestedTop;
}
