namespace WprMcp.Core;

/// <summary>
/// One row of the catalog. Patterns match (case-insensitive substring) against the
/// module name as TraceEvent exposes it via <c>ModuleFile.Name</c>. The exposed form
/// is fixture-dependent — sometimes basename (<c>ntoskrnl</c>), sometimes basename plus
/// extension (<c>tcpip.sys</c>); substring matching tolerates either, so patterns may
/// include or omit the extension as long as they remain specific enough.
///
/// <para>
/// <c>ServerUrl</c> + <c>LoadTraceReason</c> are paired: both set means "load_trace
/// should recommend this server"; both null means "no public PDB server exists for
/// these modules" (consumed only by diagnose_symbols, skipped by load_trace).
/// </para>
/// </summary>
public sealed record SymbolHintEntry(
    string[] Patterns,
    string DiagnoseHint,
    string? ServerUrl = null,
    string? LoadTraceReason = null);

/// <summary>
/// Single source of truth for "given a module name, what symbol hint applies?".
/// Consumed by <c>SymbolTools.SuggestServerForModule</c> (per-module hint string) and
/// <c>MetaTools.BuildSymbolRecommendations</c> (load_trace's grouped server suggestions).
/// </summary>
/// <remarks>
/// Order matters — first-match-wins. Specific patterns precede generic ones so
/// <c>ffmpeg</c>'s no-PDB tier wins over a hypothetical Microsoft entry that might
/// otherwise catch it via substring.
/// </remarks>
public static class SymbolHintCatalog
{
    public static IReadOnlyList<SymbolHintEntry> Entries { get; } = new SymbolHintEntry[]
    {
        // Tier 1: no public PDB server
        new(
            Patterns: new[] { "ffmpeg", "ffprobe", "ffplay" },
            DiagnoseHint:
                "This module has no public PDB server — public Windows builds (gyan.dev / BtbN / " +
                "vendor app bundles) ship stripped.  Rebuild from source with linker /DEBUG:FULL " +
                "and place the local PDB folder ahead of any SRV* entries on _NT_SYMBOL_PATH; " +
                "otherwise treat this module as opaque and focus on resolved kernel frames."),
    };

    public static SymbolHintEntry? Match(string moduleName)
    {
        foreach (var entry in Entries)
            if (entry.Patterns.Any(p => moduleName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return entry;
        return null;
    }
}
