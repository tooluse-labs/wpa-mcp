namespace WprMcp.Output;

public static class WarningBuilder
{
    public static string SymbolResolution(double rate)
        => $"{rate * 100:F1}% frame resolution rate. Run diagnose_symbols() for fix suggestions.";

    public const string HardFaultKeywordHint =
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
    /// Interrupt-specific stack warning. DPC/ISR events can exist without stack walks, which
    /// makes driver attribution impossible even though interrupt timing is present.
    /// </summary>
    public static string MissingInterruptStacks(long noStackCount, long totalCount, long noStackUs, long totalUs)
        => $"{noStackUs} of {totalUs} us across {noStackCount} of {totalCount} DPC/ISR events did not carry call stacks; " +
           "interrupt_top_stacks will collapse those samples into the synthetic ?!? frame. " +
           "Capture with stack walking enabled for PerfInfoDPC and PerfInfoISR to identify " +
           "driver routines.";

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

    /// <summary>
    /// "No NT-heap events" warning — the heap kernel provider is enabled per-process at
    /// capture time (PerfView's /HeapTrace flag, or a .wprp &lt;Heap&gt; element naming the
    /// target process), NOT through a global keyword. Default WPR profiles never enable it.
    /// </summary>
    public const string MissingPerProcessHeapTrace =
        "No NT-heap events matched.  The Heap provider is per-process — it has to be " +
        "explicitly enabled for the target process at capture time (PerfView's /HeapTrace " +
        "flag or a .wprp <Heap> element listing the process name).  Default WPR profiles " +
        "do NOT enable it.";
}
