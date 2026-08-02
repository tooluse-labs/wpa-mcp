namespace WpaMcp.Output;

public static class WarningBuilder
{
    public const string LegacyAccountedDurationWarning =
        "time_semantics_v2: legacy DurationUs/PauseUs and duration totals are accounted overlap within the requested half-open window; use FullDurationUs/FullPauseUs for complete paired wall time.";

    public static string SymbolResolution(double rate)
        => $"observed_unique_code_frame_name_resolution_rate={rate * 100:F1}%; this post-lookup " +
           "heuristic covers sample-reachable code frames only and is not a per-PDB success rate. " +
           "Interpret it with LookupState, domain stack coverage, and the exact immutable symbolContextId used for lookup.";

    public static string SymbolResolutionSkipped(string toolName)
        => $"Native symbol resolution skipped for {toolName}; call prepare_symbols for an immutable same-generation context before requesting resolution. Context-bound frame lookup is unavailable in this build and fails explicitly rather than using an implicit fallback.";

    public const string HardFaultKeywordHint =
        "MemoryHardFaults keyword required in capture profile. " +
        "A low or empty selected scope does not prove that HardFaults collection was disabled; " +
        "no qualifying faults may have occurred or filters may exclude them.";

    /// <summary>
    /// Standard "no events of class X were observed" warning for analyzers commonly requiring
    /// an additional kernel keyword. Absence of observed events is not proof of capture settings.
    /// </summary>
    public static string MissingKeyword(string eventDescription, string keywordName) =>
        $"No {eventDescription} events were observed in the selected trace scope. This does not prove " +
        $"that the {keywordName} keyword was disabled: no qualifying events may have occurred, filters " +
        "may exclude them, or the capture/parser may not expose them. If these events were expected, " +
        $"verify the capture profile and recapture with {keywordName} enabled.";

    /// <summary>
    /// "No events observed" warning for analyzers whose event family is commonly included by
    /// default. The wording remains epistemically conservative about the actual capture profile.
    /// </summary>
    public static string NoEventsInDefaultProfile(string eventDescription, string keywordName) =>
        $"No {eventDescription} events were observed in the selected trace scope. This does not prove " +
        $"whether {keywordName} collection or stack walking was enabled: no qualifying events may have " +
        "occurred, filters may exclude them, or a custom capture/parser may not expose them. " +
        $"{keywordName} is commonly present in default WPR profiles; verify the actual profile if events were expected.";

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
        $"No CLR {eventDescription} events were observed in the selected trace scope. This does not prove " +
        $"that the Microsoft-Windows-DotNETRuntime provider or its {keywordName} keyword was absent: " +
        "the workload may not have emitted qualifying events, filters may exclude them, or capture/parser " +
        "coverage may be incomplete" +
        (string.IsNullOrEmpty(extraReason) ? "" : $"; {extraReason}") +
        ". If these events were expected, verify an explicit runtime <EventCollectorId> and keyword configuration.";

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
