namespace WprMcp.Output;

public static class WarningBuilder
{
    public static string SymbolResolution(double rate)
        => $"{rate * 100:F1}% frame resolution rate. Run diagnose_symbols() for fix suggestions.";

    public const string MmapKeywordHint =
        "MemoryHardFaults keyword required in capture profile. " +
        "If row count is unexpectedly low, the trace may not include hard fault events.";
}
