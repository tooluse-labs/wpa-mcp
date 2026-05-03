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
    /// default should call <see cref="NoEventsInDefaultProfile"/> instead.
    /// </summary>
    public static string MissingKeyword(string eventDescription, string keywordName) =>
        $"No {eventDescription} events matched. The capture profile likely omits the " +
        $"{keywordName} keyword (default WPR 'CPU' / 'CPU.light' profiles do); use " +
        $"'GeneralProfile' or a custom .wprp that enables it.";

    /// <summary>
    /// "No events matched" warning for analyzers whose keyword IS in the default WPR profile —
    /// either no events occurred in the window, or a custom .wprp dropped the keyword.
    /// </summary>
    public static string NoEventsInDefaultProfile(string eventDescription, string keywordName) =>
        $"No {eventDescription} events matched. {keywordName} is enabled by default WPR " +
        $"profiles, so either no events occurred in the filter window, or a custom .wprp " +
        "dropped the keyword (or its <Stacks> element).";

    /// <summary>
    /// "No CLR events of class X matched" — separate from <see cref="MissingKeyword"/> because
    /// the underlying provider is user-mode (Microsoft-Windows-DotNETRuntime), not a kernel
    /// keyword, and WPR profiles need an explicit &lt;EventCollectorId&gt; to capture it.
    /// </summary>
    public static string MissingClrKeyword(string eventDescription, string keywordName, string extraReason = "") =>
        $"No CLR {eventDescription} events matched. Either the trace lacks the .NET runtime ETW " +
        $"provider (Microsoft-Windows-DotNETRuntime, {keywordName} keyword)" +
        (string.IsNullOrEmpty(extraReason) ? "" : $", {extraReason}") +
        ". WPR profiles need an explicit <EventCollectorId> for the runtime provider.";
}
