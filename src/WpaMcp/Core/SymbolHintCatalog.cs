namespace WpaMcp.Core;

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
    IReadOnlyList<string> Patterns,
    string DiagnoseHint,
    string? ServerUrl = null,
    string? LoadTraceReason = null)
{
    /// <summary>
    /// True iff <paramref name="moduleName"/> case-insensitively contains any of this entry's
    /// patterns. Null/empty input never matches. Both <c>SymbolHintCatalog.Match</c> (single-hit)
    /// and <c>MetaTools.BuildSymbolRecommendations</c> (group-by) read through this method, so
    /// changing matching semantics is a one-line edit.
    /// </summary>
    public bool Matches(string moduleName)
        => !string.IsNullOrEmpty(moduleName) &&
           Patterns.Any(p => moduleName.Contains(p, StringComparison.OrdinalIgnoreCase));
}

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

        // Tier 2: Chromium symbol server (Google Chrome official builds, Electron, CEF).
        // Excludes msedge / msedgewebview2 — those are MS-shipped and live on msdl
        // (verified empirically: msedge.exe.pdb HEADs 200 on msdl, 404 on chromium-symsrv).
        new(
            Patterns: new[] { "chrome", "chromium", "electron", "cef" },
            DiagnoseHint: "Add Chromium symbol server: add_symbol_server('https://chromium-browser-symsrv.commondatastorage.googleapis.com')",
            ServerUrl: "https://chromium-browser-symsrv.commondatastorage.googleapis.com",
            LoadTraceReason: "Chromium-based browser (Chrome / Electron / CEF)"),

        // Tier 3: Microsoft public symbols (msdl). Patterns ported from the previous
        // MetaTools.SymbolServerHints list, plus `msedge` (which catches `msedgewebview2`
        // via substring — both are MS-published and live on msdl).
        new(
            Patterns: new[]
            {
                "ntoskrnl", "ntdll", "kernel32", "kernelbase", "win32k", "user32", "gdi32",
                "advapi32", "rpcrt4", "combase", "ole32", "oleaut32", "shell32", "shlwapi",
                "msvcrt", "ucrtbase", "vcruntime", "msvcp",
                "fltmgr", "mssecflt", "wdf01000", "wdfldr",
                "mpengine", "mpsvc",
                "msedge",
                "dxgi", "d3d11", "d3d12", "d2d1", "dwrite", "windows.ui", "wininet", "winhttp",
                "afd.sys", "netio.sys", "tcpip.sys", "http.sys",
                "win32u", "dwmapi", "dwmcore",
            },
            DiagnoseHint: "Add Microsoft symbol server: add_symbol_server('https://msdl.microsoft.com/download/symbols')",
            ServerUrl: "https://msdl.microsoft.com/download/symbols",
            LoadTraceReason: "Microsoft public symbols"),
    };

    public static SymbolHintEntry? Match(string moduleName)
    {
        foreach (var entry in Entries)
            if (entry.Matches(moduleName))
                return entry;
        return null;
    }
}
