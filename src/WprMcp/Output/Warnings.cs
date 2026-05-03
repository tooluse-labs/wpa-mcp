namespace WprMcp.Output;

public static class WarningBuilder
{
    public static string SymbolResolution(double rate)
        => $"{rate * 100:F1}% frame resolution rate. Run diagnose_symbols() for fix suggestions.";

    public const string MmapKeywordHint =
        "MemoryHardFaults keyword required in capture profile. " +
        "If row count is unexpectedly low, the trace may not include hard fault events.";

    /// <summary>
    /// Standard "no events of class X matched" warning for analyzers whose required kernel
    /// keyword isn't enabled by default WPR 'CPU' / 'CPU.light' profiles.  Use only when the
    /// keyword genuinely isn't in the default profile — analyzers whose keyword IS enabled by
    /// default (e.g., ImageLoad, ThreadCSwitch) need a different message and should hand-roll it.
    /// </summary>
    public static string MissingKeyword(string eventDescription, string keywordName) =>
        $"No {eventDescription} events matched. The capture profile likely omits the " +
        $"{keywordName} keyword (default WPR 'CPU' / 'CPU.light' profiles do); use " +
        $"'GeneralProfile' or a custom .wprp that enables it.";
}
