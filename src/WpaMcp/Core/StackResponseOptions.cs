namespace WpaMcp.Core;

internal static class StackResponseOptions
{
    private static readonly AsyncLocal<bool?> ResolveSymbolsOverride = new();

    public const int CompactTopLimit = 25;
    public const string CompactStacksDescription =
        "Return lossy token-compact stack output for token-constrained clients. Current rows are already frame summaries; compact mode caps row count at the documented compact limit regardless of the requested top. Rerun with compactStacks=false, optionally with a larger top, when long-tail detail matters.";
    public const string SummaryOnlyDescription =
        "Return a lossy smaller leaf/metric summary by capping row count at the documented compact limit regardless of the requested top. Rerun with summaryOnly=false, optionally with a larger top, when long-tail detail matters.";
    public const string ResolveSymbolsDescription =
        "Resolve warm native symbols through _NT_SYMBOL_PATH. Default false for stack-heavy MCP tools so broad whole-trace calls do not block on remote PDB downloads; rerun with resolveSymbols=true after narrowing pid/startUs/endUs when exact function names matter. Enabling it may download and write PDBs into the configured local symbol cache.";

    // Approximate Claude Code's 10k/25k token response budgets for ASCII-heavy JSON
    // as serialized bytes. The guard is byte-based, not a tokenizer estimate.
    public const int WarningResponseBytes = 40_000;
    public const int MaximumResponseBytes = 100_000;

    public static bool CurrentResolveSymbols => ResolveSymbolsOverride.Value ?? false;

    public static int EffectiveTop(int requestedTop, bool compactStacks, bool summaryOnly)
        => compactStacks || summaryOnly ? Math.Min(requestedTop, CompactTopLimit) : requestedTop;

    public static IDisposable UseResolveSymbols(bool resolveSymbols)
        => new ResolveSymbolsScope(resolveSymbols);

    private sealed class ResolveSymbolsScope : IDisposable
    {
        private readonly bool? _previous;

        public ResolveSymbolsScope(bool resolveSymbols)
        {
            _previous = ResolveSymbolsOverride.Value;
            ResolveSymbolsOverride.Value = resolveSymbols;
        }

        public void Dispose() => ResolveSymbolsOverride.Value = _previous;
    }
}
