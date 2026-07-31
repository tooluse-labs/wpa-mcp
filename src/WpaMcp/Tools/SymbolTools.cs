using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Etlx;
using ModelContextProtocol.Server;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class SymbolTools
{
    private const int MaxLocalCandidatesPerModule = 10;

    private readonly SymbolService _symbols;
    private readonly TraceCache _cache;
    public SymbolTools(SymbolService symbols, TraceCache cache)
    {
        _symbols = symbols;
        _cache = cache;
    }

    [McpServerTool(ReadOnly = false, Idempotent = false, OpenWorld = false, Destructive = false), Description(
        "Sets the entire _NT_SYMBOL_PATH for symbol resolution in the running server (replaces " +
        "or appends).  Use this when you want to drop in a curated path string (multiple " +
        "servers + caches separated by `;`); for incremental setup of one server at a time, " +
        "prefer add_symbol_server.  PerfView equivalent: File → Set Symbol Path… dialog.  " +
        "Affects all subsequent stack-resolving tool calls until the server restarts or this " +
        "is called again. Entries are trusted as-is; use only vetted local paths and symbol " +
        "servers because subsequent stack-resolving tools may fetch PDBs from SRV* URLs and " +
        "populate the local cache.  Returns the resulting path so callers can verify what was applied. " +
        "No startUs/endUs: symbol-path configuration is process-wide state, not trace-event analysis.")]
    public string SetSymbolPath(
        [Description("New path (e.g. 'SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols')")]
        string path,
        [Description("Append to existing path instead of replacing (default true)")]
        bool append = true)
    {
        Validation.RequireText(path, allowEmpty: true);
        _symbols.SetPath(path, append);
        return _symbols.CurrentPath ?? "";
    }

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Appends a symbol server URL (with optional local cache directory) to the existing " +
        "_NT_SYMBOL_PATH.  Cache defaults to `%LocalAppData%\\WpaMcp\\Symbols`.  Use this for " +
        "incremental setup ('add msdl.microsoft.com, then Chromium's symbol server'); for a " +
        "full replacement string, use set_symbol_path.  PerfView equivalent: a single entry in " +
        "the File → Set Symbol Path dialog.  Idempotent — re-adding the same URL is a no-op.  " +
        "The URL is trusted as-is; add only vetted symbol servers because subsequent " +
        "stack-resolving tools may fetch PDBs from it and populate the local cache.  " +
        "Returns the path actually in effect after the change. No startUs/endUs: symbol-path " +
        "configuration is process-wide state, not trace-event analysis.")]
    public string AddSymbolServer(
        [Description("Symbol server URL (e.g. https://msdl.microsoft.com/download/symbols)")]
        string url,
        [Description("Local cache directory (optional)")] string? cacheDir = null)
    {
        Validation.RequireText(url);
        if (cacheDir is not null)
            Validation.RequireText(cacheDir, allowEmpty: true);
        _symbols.AddServer(url, cacheDir);
        return _symbols.CurrentPath ?? "";
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Per-module symbol-resolution status for an already-loaded trace, with auto-suggested " +
        "fixes for unresolved modules (which symbol server to add for which module — e.g., " +
        "msdl.microsoft.com for ntdll/kernelbase, Chromium symbol server for chrome.exe / cef.dll).  " +
        "The first sanity check to run when cpu_top_functions shows lots of `module!?` frames " +
        "or `Stats.ResolutionRate < 0.8`.  PerfView equivalent: Modules tab + Set Symbol Path " +
        "dialog (this tool composes both, plus auto-recommends which server to add per module).  " +
        "Returns top 50 modules sorted unresolved-first, expected PDB name/GUID/Age, local disk " +
        "symbol-path candidates, native DIA DLL health, and trace-directory symbol-path status. " +
        "Local disk paths plus local disk SRV/CACHE caches and stores are probed read-only; remote " +
        "SRV URLs are not contacted to avoid surprise downloads. If any are unresolved, includes a 'after fixing, " +
        "re-run cpu_top_functions to verify' suggestion. No startUs/endUs: module symbol status " +
        "is a whole-trace image/module property.")]
    public DiagnoseSymbolsResponse DiagnoseSymbols(
        [Description("Absolute path to .etl file")] string path)
    {
        var trace = _cache.Get(path);
        var rows = new List<ModuleSymbolStatus>();
        var suggestions = new List<string>();
        var path0 = _symbols.CurrentPath;
        var traceDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        var localSymbolPath = ParseLocalSymbolPath(path0);
        var traceDirectoryInSymbolPath = SymbolPathContainsLocalPath(localSymbolPath.Roots, traceDirectory);
        var nativeSupport = BuildNativeSymbolSupport();
        suggestions.AddRange(localSymbolPath.Warnings);

        // Walk modules listed in the trace and diagnose what the current local symbol path
        // can prove without triggering remote symbol-server downloads.
        foreach (var module in trace.ModuleFiles)
        {
            var diagnosis = DiagnoseModule(module, localSymbolPath, nativeSupport);
            rows.Add(diagnosis);
        }

        if (!nativeSupport.Msdia140Present)
        {
            suggestions.Add(
                "Native DIA support is missing; install the release zip layout or place msdia140.dll under native\\amd64 beside the installed bin directory.");
        }

        if (!traceDirectoryInSymbolPath)
        {
            suggestions.Add(
                "Trace directory is not in _NT_SYMBOL_PATH; load_trace normally adds it automatically, but set_symbol_path append=false can remove it.");
        }

        if (rows.Any(r => !r.Resolved))
        {
            suggestions.Add(
                "After updating symbols, re-run cpu_top_functions to verify resolution_rate improved.");
        }

        return new DiagnoseSymbolsResponse(
            CurrentSymbolPath: path0 ?? "<unset>",
            CacheDir: _symbols.DefaultCacheDir,
            Modules: rows
                .OrderBy(r => r.Resolved ? 1 : 0)
                .ThenBy(r => r.Module, StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList(),
            Suggestions: suggestions,
            TraceDirectory: traceDirectory,
            TraceDirectoryInSymbolPath: traceDirectoryInSymbolPath,
            NativeSymbolSupport: nativeSupport);
    }

    // Per-module hint lookup is centralised in SymbolHintCatalog (see Core/).
    // Both this method and MetaTools.BuildSymbolRecommendations consume that catalog.
    internal static string SuggestServerForModule(string moduleName)
        => SymbolHintCatalog.Match(moduleName)?.DiagnoseHint
           ?? "PDB not indexed; provide local PDB folder via set_symbol_path or contact the module owner.";

    internal static NativeSymbolSupportStatus BuildNativeSymbolSupport()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant()
        };

        var baseDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var parentDir = Directory.GetParent(baseDir)?.FullName ?? baseDir;
        var nativeDirs = new[]
            {
                Path.Combine(parentDir, "native", architecture),
                Path.Combine(baseDir, architecture),
                Path.Combine(baseDir, "native", architecture)
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dependencies = nativeDirs
            .SelectMany(dir => new[]
            {
                DependencyStatus(dir, "msdia140.dll"),
                DependencyStatus(dir, "KernelTraceControl.dll")
            })
            .ToArray();
        var completeDirectory = nativeDirs.FirstOrDefault(dir =>
            File.Exists(Path.Combine(dir, "msdia140.dll")) &&
            File.Exists(Path.Combine(dir, "KernelTraceControl.dll")));
        var msdiaPresent = dependencies.Any(dep =>
            dep.Name.Equals("msdia140.dll", StringComparison.OrdinalIgnoreCase) && dep.Present);
        var kernelTracePresent = dependencies.Any(dep =>
            dep.Name.Equals("KernelTraceControl.dll", StringComparison.OrdinalIgnoreCase) && dep.Present);
        var status = completeDirectory is not null ? "ready" : "missing_native_dependency";
        var suggestion = status == "ready"
            ? null
            : $"Install native dependencies under {nativeDirs[0]}. Release zip installs them there automatically.";

        return new NativeSymbolSupportStatus(
            Architecture: architecture,
            Msdia140Present: msdiaPresent,
            KernelTraceControlPresent: kernelTracePresent,
            Status: status,
            Dependencies: dependencies,
            Suggestion: suggestion);
    }

    internal static ModuleSymbolStatus DiagnoseModule(
        TraceModuleFile module,
        string? symbolPath,
        NativeSymbolSupportStatus nativeSupport)
        => DiagnoseModule(module, ParseLocalSymbolPath(symbolPath), nativeSupport);

    private static ModuleSymbolStatus DiagnoseModule(
        TraceModuleFile module,
        ParsedSymbolPath localSymbolPath,
        NativeSymbolSupportStatus nativeSupport)
    {
        var moduleName = string.IsNullOrWhiteSpace(module.Name)
            ? Path.GetFileNameWithoutExtension(module.FilePath)
            : module.Name;
        if (string.IsNullOrWhiteSpace(moduleName))
            moduleName = "<unknown>";

        var pdbName = Path.GetFileName(module.PdbName);
        var hasPdbIdentity = !string.IsNullOrWhiteSpace(pdbName) &&
                             module.PdbSignature != Guid.Empty &&
                             module.PdbAge > 0;
        if (!hasPdbIdentity)
        {
            return new ModuleSymbolStatus(
                Module: moduleName,
                FrameCount: 0,
                Resolved: false,
                Suggestion: SuggestServerForModule(moduleName),
                FilePath: EmptyToNull(module.FilePath),
                ExpectedPdbName: EmptyToNull(pdbName),
                PdbSignature: module.PdbSignature == Guid.Empty ? null : module.PdbSignature.ToString("D"),
                PdbAge: module.PdbAge > 0 ? module.PdbAge : null,
                BinaryFormat: module.BinaryFormat.ToString(),
                LookupStatus: "missing_pdb_identity",
                FailureReason: "Trace/module metadata does not include a complete PDB name + GUID + Age identity. Recapture or merge the ETL on the collection machine so PDB signatures are present.",
                LocalSymbolCandidates: Array.Empty<string>());
        }

        var localCandidates = FindLocalSymbolCandidateDetails(
            localSymbolPath.Roots,
            pdbName,
            module.PdbSignature,
            module.PdbAge);
        var foundExactPdb = localCandidates.Any(candidate => candidate.ExactIdentityMatch);
        var foundFlatPdb = localCandidates.Any(candidate => !candidate.ExactIdentityMatch);
        var candidatePaths = localCandidates
            .Select(candidate => candidate.Path)
            .ToList();
        var lookupStatus = foundExactPdb
            ? nativeSupport.Msdia140Present ? "found_in_local_symbol_path" : "found_but_native_dia_missing"
            : foundFlatPdb ? "found_flat_candidate_identity_unverified"
            : "not_found_in_local_symbol_path";
        var failureReason = lookupStatus switch
        {
            "found_in_local_symbol_path" => null,
            "found_but_native_dia_missing" => "A matching local PDB candidate exists, but msdia140.dll is missing so TraceEvent cannot open Windows PDBs.",
            "found_flat_candidate_identity_unverified" => "A flat PDB with the expected file name exists, but diagnose_symbols did not verify its GUID/Age. Run a stack tool with resolveSymbols=true or provide a symbol-store layout PDB to confirm it matches this trace.",
            _ => "No matching local PDB candidate was found in local disk _NT_SYMBOL_PATH entries. UNC paths are skipped to avoid SMB latency; if your symbol store is on a network share, copy it to a local disk first. Remote SRV entries are not probed by diagnose_symbols to avoid downloads."
        };
        var suggestion = lookupStatus switch
        {
            "found_in_local_symbol_path" => "Local symbol-store PDB candidate found; rerun the target stack tool with resolveSymbols=true to verify function-name resolution.",
            "found_flat_candidate_identity_unverified" => "Flat PDB candidate found by file name only; rerun the target stack tool with resolveSymbols=true to let DIA verify GUID/Age.",
            _ => SuggestServerForModule(moduleName)
        };

        return new ModuleSymbolStatus(
            Module: moduleName,
            FrameCount: 0,
            Resolved: lookupStatus == "found_in_local_symbol_path",
            Suggestion: suggestion,
            FilePath: EmptyToNull(module.FilePath),
            ExpectedPdbName: pdbName,
            PdbSignature: module.PdbSignature.ToString("D"),
            PdbAge: module.PdbAge,
            BinaryFormat: module.BinaryFormat.ToString(),
            LookupStatus: lookupStatus,
            FailureReason: failureReason,
            LocalSymbolCandidates: candidatePaths);
    }

    internal static IReadOnlyList<string> FindLocalSymbolCandidates(
        string? symbolPath,
        string pdbName,
        Guid pdbSignature,
        int pdbAge)
        => FindLocalSymbolCandidateDetails(ParseLocalSymbolPath(symbolPath).Roots, pdbName, pdbSignature, pdbAge)
            .Select(candidate => candidate.Path)
            .ToList();

    private static IReadOnlyList<LocalSymbolCandidate> FindLocalSymbolCandidateDetails(
        IReadOnlyList<LocalSymbolRoot> roots,
        string pdbName,
        Guid pdbSignature,
        int pdbAge)
    {
        var candidates = new List<LocalSymbolCandidate>();
        if (roots.Count == 0 || string.IsNullOrWhiteSpace(pdbName))
            return candidates;

        foreach (var root in roots)
        {
            AddIfExists(
                candidates,
                Path.Combine(root.Path, pdbName, SymbolStoreKey(pdbSignature, pdbAge), pdbName),
                exactIdentityMatch: true);
            AddIfExists(
                candidates,
                Path.Combine(root.Path, pdbName, SymbolStoreKey(pdbSignature, unchecked((int)0xffffffff)), pdbName),
                exactIdentityMatch: true);

            if (root.ProbeFlatCandidates)
                AddIfExists(candidates, Path.Combine(root.Path, pdbName), exactIdentityMatch: false);
        }

        return candidates
            .DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(candidate => candidate.ExactIdentityMatch ? 0 : 1)
            .Take(MaxLocalCandidatesPerModule)
            .ToList();
    }

    internal static IReadOnlyList<string> BuildSymbolPathEntryWarnings(string? symbolPath)
        => ParseLocalSymbolPath(symbolPath).Warnings;

    private static ParsedSymbolPath ParseLocalSymbolPath(string? symbolPath)
    {
        var roots = new List<LocalSymbolRoot>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(symbolPath))
            return new ParsedSymbolPath(Array.Empty<LocalSymbolRoot>(), Array.Empty<string>());

        foreach (var rawEntry in symbolPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var entry in EnumerateSymbolPathProbeEntries(rawEntry))
            {
                if (TryNormalizeLocalDiskPath(entry.Path, out var normalized, out var skipReason))
                {
                    roots.Add(new LocalSymbolRoot(normalized, entry.ProbeFlatCandidates));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(skipReason))
                    warnings.Add(skipReason);
            }
        }

        var dedupedRoots = new List<LocalSymbolRoot>();
        foreach (var root in roots)
        {
            var existingIndex = dedupedRoots.FindIndex(existing =>
                string.Equals(existing.Path, root.Path, StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0)
            {
                dedupedRoots.Add(root);
                continue;
            }

            if (root.ProbeFlatCandidates && !dedupedRoots[existingIndex].ProbeFlatCandidates)
                dedupedRoots[existingIndex] = dedupedRoots[existingIndex] with { ProbeFlatCandidates = true };
        }

        return new ParsedSymbolPath(
            dedupedRoots,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static IEnumerable<SymbolPathProbeEntry> EnumerateSymbolPathProbeEntries(string rawEntry)
    {
        if (rawEntry.StartsWith("SRV*", StringComparison.OrdinalIgnoreCase) ||
            rawEntry.StartsWith("SYMSRV*", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in rawEntry.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
            {
                if (part.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new SymbolPathProbeEntry(part, ProbeFlatCandidates: false);
            }

            yield break;
        }

        if (rawEntry.StartsWith("CACHE*", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in rawEntry.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
                yield return new SymbolPathProbeEntry(part, ProbeFlatCandidates: false);

            yield break;
        }

        yield return new SymbolPathProbeEntry(rawEntry, ProbeFlatCandidates: true);
    }

    private static bool SymbolPathContainsLocalPath(IReadOnlyList<LocalSymbolRoot> roots, string localPath)
    {
        if (roots.Count == 0 || string.IsNullOrWhiteSpace(localPath))
            return false;

        if (!TryNormalizeLocalDiskPath(localPath, out var normalized, out _))
            return false;

        return roots.Any(root => string.Equals(root.Path, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryNormalizeLocalDiskPath(
        string path,
        out string normalizedPath,
        out string? skipReason)
    {
        normalizedPath = "";
        skipReason = null;
        var trimmed = path.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (trimmed.Contains("://", StringComparison.Ordinal))
            return false;

        if (trimmed.StartsWith("\\\\", StringComparison.Ordinal))
        {
            skipReason = $"Skipped UNC symbol path entry '{trimmed}' to avoid SMB latency. Copy network symbol stores to a local disk before probing with diagnose_symbols.";
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(trimmed) && !Directory.Exists(trimmed))
            {
                skipReason = $"Skipped relative symbol path entry '{trimmed}' because it does not exist relative to '{Environment.CurrentDirectory}'.";
                return false;
            }

            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            skipReason = $"Skipped malformed symbol path entry '{trimmed}': {ex.Message}";
            return false;
        }
    }

    private static string SymbolStoreKey(Guid signature, int age)
        => signature.ToString("N").ToUpperInvariant() + age.ToString("X");

    private sealed record ParsedSymbolPath(
        IReadOnlyList<LocalSymbolRoot> Roots,
        IReadOnlyList<string> Warnings);

    private sealed record LocalSymbolRoot(
        string Path,
        bool ProbeFlatCandidates);

    private sealed record LocalSymbolCandidate(
        string Path,
        bool ExactIdentityMatch);

    private sealed record SymbolPathProbeEntry(
        string Path,
        bool ProbeFlatCandidates);

    private static NativeDependencyStatus DependencyStatus(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return new NativeDependencyStatus(fileName, path, File.Exists(path));
    }

    private static void AddIfExists(List<LocalSymbolCandidate> candidates, string path, bool exactIdentityMatch)
    {
        if (File.Exists(path))
            candidates.Add(new LocalSymbolCandidate(path, exactIdentityMatch));
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
