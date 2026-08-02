using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using ModelContextProtocol.Server;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class SymbolTools
{
    private const int MaxDisplayedLocalCandidatesPerModule = 10;

    private readonly SymbolService _symbols;
    private readonly TraceCache _cache;
    public SymbolTools(SymbolService symbols, TraceCache cache)
    {
        _symbols = symbols;
        _cache = cache;
    }

    [Description(
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

    [Description(
        "Appends a symbol server URL (with optional local cache directory) to the existing " +
        "_NT_SYMBOL_PATH.  Cache defaults to `%LocalAppData%\\WpaMcp\\Symbols`.  Use this for " +
        "incremental setup ('add msdl.microsoft.com, then Chromium's symbol server'); for a " +
        "full replacement string, use set_symbol_path.  PerfView equivalent: a single entry in " +
        "the File → Set Symbol Path dialog.  Idempotent — re-adding the same URL is a no-op.  " +
        "The URL is trusted as-is; add only vetted symbol servers because subsequent " +
        "stack-resolving tools may fetch PDBs from it and populate the local cache.  " +
        "Creating a caller-supplied UNC, mapped-drive, or reparse-backed cacheDir can interact " +
        "with external storage, so this tool is advertised OpenWorld=true even though it does not contact the server URL now. " +
        "Returns the path actually in effect after the change. No startUs/endUs: symbol-path " +
        "configuration is process-wide state, not trace-event analysis.")]
    public string AddSymbolServer(
        [Description("Symbol server URL (e.g. https://msdl.microsoft.com/download/symbols)")]
        string url,
        [Description("Local cache directory (optional). UNC, mapped-drive, or reparse-backed paths can cause immediate external filesystem access when the directory is created.")] string? cacheDir = null)
    {
        Validation.RequireText(url);
        if (cacheDir is not null)
            Validation.RequireText(cacheDir, allowEmpty: true);
        _symbols.AddServer(url, cacheDir);
        return _symbols.CurrentPath ?? "";
    }

    [Description(
        "Per-module symbol metadata and verified local-PDB readiness for an already-loaded trace, with suggested " +
        "path/server actions for modules that have complete lookup identity but no exact local PDB candidate " +
        "(for example, msdl.microsoft.com for ntdll/kernelbase, or the " +
        "Chromium symbol server for chrome.exe / cef.dll).  " +
        "The first metadata/local-readiness check to run when cpu_top_functions shows lots of `module!?` frames " +
        "or a low observed frame-name resolution rate. This tool directly opens discovered local PDBs to verify " +
        "their GUID/Age, but does not execute frame lookup; actual resolution is measured by stack tools. " +
        "PerfView equivalent: Modules tab + Set Symbol Path " +
        "dialog (this tool composes both, plus auto-recommends which server to add per module).  " +
        "Returns top 50 modules sorted local-not-ready-first, expected PDB name/GUID/Age, configured filesystem " +
        "symbol-path candidates, native DIA DLL health, and trace-directory symbol-path status. " +
        "Bare path entries are probed only for a flat PDB; non-UNC filesystem roots in SRV/SYMSRV/CACHE entries " +
        "are probed only in symbol-store layout, using direct OpenSymbolFile calls. The tool does not actively " +
        "access remote SRV or UNC entries, but a configured local-looking root can still be redirected by the OS " +
        "through a mapped drive or reparse point; no network-topology detection is attempted, so the tool is conservatively " +
        "advertised OpenWorld=true. Trace loading can still create or " +
        "refresh an ETLX sidecar, so the overall tool is not filesystem-read-only. If modules are not locally ready, " +
        "the response recommends running the target stack tool after fixing the path to measure actual frame-name resolution. No startUs/endUs: module symbol status " +
        "is a whole-trace image/module property.")]
    public DiagnoseSymbolsResponse DiagnoseSymbols(
        [Description("Canonical TraceId returned by load_trace")] string path)
    {
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var rows = new List<ModuleSymbolStatus>();
        var suggestions = new List<string>();
        var canonicalTracePath = Path.GetFullPath(path);
        var symbolPathSnapshot = SymbolPathState.GetSnapshot(canonicalTracePath);
        var configuredPath = symbolPathSnapshot.ConfiguredPath;
        var traceDirectory = Path.GetDirectoryName(canonicalTracePath) ?? "";
        var effectivePath = symbolPathSnapshot.EffectivePath;
        var configuredLocalSymbolPath = ParseLocalSymbolPath(configuredPath);
        var localSymbolPath = ParseLocalSymbolPath(effectivePath);
        var traceDirectoryInConfiguredPath = SymbolPathContainsLocalPath(
            configuredLocalSymbolPath.Roots, traceDirectory);
        var traceDirectoryInEffectivePath = SymbolPathContainsLocalPath(
            localSymbolPath.Roots, traceDirectory);
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
            suggestions.Add(NativeDiaSuggestion(nativeSupport));
        }

        if (rows.Any(r => !r.LocalPdbReady))
        {
            suggestions.Add(
                "After updating symbols, run the target stack tool with resolveSymbols=true and verify its observed frame-name resolution rates.");
        }

        return new DiagnoseSymbolsResponse(
            CurrentSymbolPath: configuredPath ?? "<unset>",
            CacheDir: _symbols.DefaultCacheDir,
            Modules: rows
                .OrderBy(r => r.LocalPdbReady ? 1 : 0)
                .ThenBy(r => r.Module, StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList(),
            Suggestions: suggestions,
            TraceDirectory: traceDirectory,
            TraceDirectoryInSymbolPath: traceDirectoryInEffectivePath,
            NativeSymbolSupport: nativeSupport,
            ConfiguredSymbolPath: configuredPath ?? "<unset>",
            EffectiveSymbolPath: effectivePath,
            TraceDirectoryInConfiguredSymbolPath: traceDirectoryInConfiguredPath,
            TraceDirectoryInEffectiveSymbolPath: traceDirectoryInEffectivePath,
            FrameResolutionMeasurementState: "not_measured",
            DefaultCacheDir: _symbols.DefaultCacheDir);
    }

    // Per-module hint lookup is centralised in SymbolHintCatalog (see Core/).
    // Both this method and MetaTools.BuildSymbolRecommendations consume that catalog.
    internal static string SuggestServerForModule(string moduleName)
        => SymbolHintCatalog.Match(moduleName)?.DiagnoseHint
           ?? "PDB not indexed; provide local PDB folder via set_symbol_path or contact the module owner.";

    internal static bool HasCompletePdbIdentity(
        string? pdbName,
        Guid pdbSignature,
        int pdbAge)
        => !string.IsNullOrWhiteSpace(Path.GetFileName(pdbName)) &&
           pdbSignature != Guid.Empty &&
           pdbAge > 0;

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

    internal static string NativeDiaSuggestion(
        NativeSymbolSupportStatus nativeSupport) =>
        $"Native DIA support is missing for {nativeSupport.Architecture}; " +
        (nativeSupport.Suggestion ??
         "install the release zip layout for this server architecture.");

    internal static ModuleSymbolStatus DiagnoseModule(
        TraceModuleFile module,
        string? symbolPath,
        NativeSymbolSupportStatus nativeSupport)
        => DiagnoseModule(module, ParseLocalSymbolPath(symbolPath), nativeSupport);

    internal static ModuleSymbolStatus DiagnoseModule(
        string moduleName,
        string? filePath,
        string pdbName,
        Guid pdbSignature,
        int pdbAge,
        string binaryFormat,
        string? symbolPath,
        NativeSymbolSupportStatus nativeSupport)
        => DiagnoseModule(
            moduleName,
            filePath,
            pdbName,
            pdbSignature,
            pdbAge,
            binaryFormat,
            ParseLocalSymbolPath(symbolPath),
            nativeSupport);

    private static ModuleSymbolStatus DiagnoseModule(
        TraceModuleFile module,
        ParsedSymbolPath localSymbolPath,
        NativeSymbolSupportStatus nativeSupport)
        => DiagnoseModule(
            module.Name,
            module.FilePath,
            module.PdbName,
            module.PdbSignature,
            module.PdbAge,
            module.BinaryFormat.ToString(),
            localSymbolPath,
            nativeSupport);

    private static ModuleSymbolStatus DiagnoseModule(
        string? moduleName,
        string? filePath,
        string? pdbPath,
        Guid pdbSignature,
        int pdbAge,
        string binaryFormat,
        ParsedSymbolPath localSymbolPath,
        NativeSymbolSupportStatus nativeSupport)
    {
        moduleName = string.IsNullOrWhiteSpace(moduleName)
            ? Path.GetFileNameWithoutExtension(filePath)
            : moduleName;
        if (string.IsNullOrWhiteSpace(moduleName))
            moduleName = "<unknown>";

        var pdbName = Path.GetFileName(pdbPath) ?? "";
        var hasPdbName = !string.IsNullOrWhiteSpace(pdbName);
        var hasPdbIdentity = HasCompletePdbIdentity(
            pdbName, pdbSignature, pdbAge);
        if (!hasPdbIdentity)
        {
            return new ModuleSymbolStatus(
                Module: moduleName,
                FrameCount: null,
                Resolved: null,
                Suggestion: "Recapture or merge the ETL on the collection machine so the module's PDB name, GUID, and Age are recorded before choosing a symbol server.",
                FilePath: EmptyToNull(filePath),
                ExpectedPdbName: EmptyToNull(pdbName),
                PdbSignature: pdbSignature == Guid.Empty ? null : pdbSignature.ToString("D"),
                PdbAge: pdbAge > 0 ? pdbAge : null,
                BinaryFormat: binaryFormat,
                LookupStatus: "missing_pdb_identity",
                FailureReason: "Trace/module metadata does not include a complete PDB name + GUID + Age identity. Recapture or merge the ETL on the collection machine so PDB signatures are present.",
                LocalSymbolCandidates: Array.Empty<string>(),
                LocalSymbolCandidateCount: 0,
                LocalSymbolCandidatesTruncated: false,
                HasPdbName: hasPdbName,
                HasCompletePdbIdentity: false,
                LocalPdbReady: false,
                FrameResolutionState: "not_measured",
                EvidenceScope: "module_metadata_and_local_candidate_probe");
        }

        var localCandidates = FindLocalSymbolCandidateDetails(
            localSymbolPath.Roots,
            pdbName,
            pdbSignature,
            pdbAge);
        var verifiedCandidates = VerifyLocalSymbolCandidates(
            localCandidates,
            pdbSignature,
            pdbAge,
            nativeSupport);
        var candidatePaths = verifiedCandidates
            .OrderByDescending(candidate =>
                candidate.VerificationState == LocalPdbVerificationState.ExactIdentityMatch)
            .Take(MaxDisplayedLocalCandidatesPerModule)
            .Select(candidate => candidate.Path)
            .ToList();
        var hasExactMatch = verifiedCandidates.Any(candidate =>
            candidate.VerificationState == LocalPdbVerificationState.ExactIdentityMatch);
        var hasUnavailableCandidate = verifiedCandidates.Any(candidate =>
            candidate.VerificationState == LocalPdbVerificationState.VerificationUnavailable);
        var hasIdentityMismatch = verifiedCandidates.Any(candidate =>
            candidate.VerificationState == LocalPdbVerificationState.IdentityMismatch);
        var hasInvalidCandidate = verifiedCandidates.Any(candidate =>
            candidate.VerificationState == LocalPdbVerificationState.Invalid);
        var lookupStatus = hasExactMatch
            ? "exact_identity_match"
            : hasUnavailableCandidate
                ? "candidate_identity_unverified"
                : hasIdentityMismatch
                    ? "identity_mismatch"
                    : hasInvalidCandidate
                        ? "invalid_local_pdb_candidate"
                        : "not_found_in_local_symbol_path";
        var failureReason = lookupStatus switch
        {
            "exact_identity_match" => null,
            "candidate_identity_unverified" => BuildIdentityUnverifiedReason(verifiedCandidates),
            "identity_mismatch" => "At least one local PDB candidate was readable, but no verified candidate matched the trace module's expected GUID/Age identity.",
            "invalid_local_pdb_candidate" => "A local file candidate was rejected by the PDB container probe, or a portable-PDB reader explicitly reported malformed/truncated data.",
            _ => "No matching local PDB candidate was found in eligible non-UNC filesystem entries of the effective query path (configured path plus trace directory). Bare paths are checked only for root\\PdbName; filesystem roots declared through SRV/SYMSRV/CACHE are checked only in symbol-store layout. Remote SRV and UNC entries are not actively accessed. A local-looking root may still be redirected by the OS through a mapped drive or reparse point."
        };
        var suggestion = lookupStatus switch
        {
            "exact_identity_match" =>
                "Exact local PDB GUID/Age match verified; run the target stack tool with resolveSymbols=true to measure actual frame-name resolution.",
            "candidate_identity_unverified" =>
                "Make the local PDB readable and ensure its format-appropriate symbol reader is available, then rerun diagnose_symbols.",
            "identity_mismatch" =>
                "Replace the stale or wrong-build local PDB with the exact GUID/Age requested by the trace, then rerun diagnose_symbols.",
            "invalid_local_pdb_candidate" =>
                "Replace the invalid local PDB candidate, then rerun diagnose_symbols and the target stack tool with resolveSymbols=true.",
            _ => SuggestServerForModule(moduleName)
        };

        return new ModuleSymbolStatus(
            Module: moduleName,
            FrameCount: null,
            Resolved: null,
            Suggestion: suggestion,
            FilePath: EmptyToNull(filePath),
            ExpectedPdbName: pdbName,
            PdbSignature: pdbSignature.ToString("D"),
            PdbAge: pdbAge,
            BinaryFormat: binaryFormat,
            LookupStatus: lookupStatus,
            FailureReason: failureReason,
            LocalSymbolCandidates: candidatePaths,
            LocalSymbolCandidateCount: verifiedCandidates.Count,
            LocalSymbolCandidatesTruncated: verifiedCandidates.Count > candidatePaths.Count,
            HasPdbName: true,
            HasCompletePdbIdentity: true,
            LocalPdbReady: hasExactMatch,
            FrameResolutionState: "not_measured",
            EvidenceScope: hasExactMatch || hasUnavailableCandidate || hasIdentityMismatch
                ? "module_metadata_and_local_pdb_identity_verification"
                : "module_metadata_and_local_candidate_probe");
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
            if (root.ProbeFlatCandidates)
                AddIfExists(candidates, Path.Combine(root.Path, pdbName));

            if (root.ProbeSymbolStoreCandidates)
            {
                AddIfExists(
                    candidates,
                    Path.Combine(root.Path, pdbName, SymbolStoreKey(pdbSignature, pdbAge), pdbName));
                AddIfExists(
                    candidates,
                    Path.Combine(root.Path, pdbName, SymbolStoreKey(pdbSignature, unchecked((int)0xffffffff)), pdbName));
            }
        }

        return candidates
            .DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
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
                    roots.Add(new LocalSymbolRoot(
                        normalized,
                        entry.ProbeFlatCandidates,
                        entry.ProbeSymbolStoreCandidates));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(skipReason))
                    warnings.Add(skipReason);
            }
        }

        return new ParsedSymbolPath(
            roots.DistinctBy(
                    root => (root.Path.ToUpperInvariant(), root.ProbeFlatCandidates, root.ProbeSymbolStoreCandidates))
                .ToList(),
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

                yield return new SymbolPathProbeEntry(
                    part,
                    ProbeFlatCandidates: false,
                    ProbeSymbolStoreCandidates: true);
            }

            yield break;
        }

        if (rawEntry.StartsWith("CACHE*", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in rawEntry.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
                yield return new SymbolPathProbeEntry(
                    part,
                    ProbeFlatCandidates: false,
                    ProbeSymbolStoreCandidates: true);

            yield break;
        }

        yield return new SymbolPathProbeEntry(
            rawEntry,
            ProbeFlatCandidates: true,
            ProbeSymbolStoreCandidates: false);
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
        bool ProbeFlatCandidates,
        bool ProbeSymbolStoreCandidates);

    private sealed record LocalSymbolCandidate(
        string Path,
        LocalPdbContainerKind ContainerKind,
        LocalPdbVerificationState VerificationState = LocalPdbVerificationState.NotAttempted,
        LocalPdbVerificationFailure VerificationFailure = LocalPdbVerificationFailure.None);

    private enum LocalPdbContainerKind
    {
        Missing,
        Portable,
        Windows,
        Invalid,
        Unreadable
    }

    private enum LocalPdbVerificationState
    {
        NotAttempted,
        ExactIdentityMatch,
        IdentityMismatch,
        Invalid,
        VerificationUnavailable
    }

    private enum LocalPdbVerificationFailure
    {
        None,
        CandidateUnreadable,
        NativeReaderUnavailable,
        CandidateOrReaderAmbiguous,
        ReaderUnavailable
    }

    private sealed record SymbolPathProbeEntry(
        string Path,
        bool ProbeFlatCandidates,
        bool ProbeSymbolStoreCandidates);

    private static NativeDependencyStatus DependencyStatus(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return new NativeDependencyStatus(fileName, path, File.Exists(path));
    }

    private static void AddIfExists(List<LocalSymbolCandidate> candidates, string path)
    {
        var containerKind = ProbePdbContainer(path);
        if (containerKind != LocalPdbContainerKind.Missing)
            candidates.Add(new LocalSymbolCandidate(path, containerKind));
    }

    private static IReadOnlyList<LocalSymbolCandidate> VerifyLocalSymbolCandidates(
        IReadOnlyList<LocalSymbolCandidate> candidates,
        Guid expectedSignature,
        int expectedAge,
        NativeSymbolSupportStatus nativeSupport)
    {
        if (candidates.Count == 0)
            return candidates;

        var verified = new List<LocalSymbolCandidate>(candidates.Count);
        SymbolReader? reader = null;
        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate.ContainerKind == LocalPdbContainerKind.Missing)
                    continue;

                if (candidate.ContainerKind == LocalPdbContainerKind.Invalid)
                {
                    verified.Add(candidate with { VerificationState = LocalPdbVerificationState.Invalid });
                    continue;
                }

                if (candidate.ContainerKind == LocalPdbContainerKind.Unreadable)
                {
                    verified.Add(candidate with
                    {
                        VerificationState = LocalPdbVerificationState.VerificationUnavailable,
                        VerificationFailure = LocalPdbVerificationFailure.CandidateUnreadable
                    });
                    continue;
                }

                if (candidate.ContainerKind == LocalPdbContainerKind.Windows && !nativeSupport.Msdia140Present)
                {
                    verified.Add(candidate with
                    {
                        VerificationState = LocalPdbVerificationState.VerificationUnavailable,
                        VerificationFailure = LocalPdbVerificationFailure.NativeReaderUnavailable
                    });
                    continue;
                }

                try
                {
                    // Empty SymbolPath plus direct OpenSymbolFile(path) confines the reader to
                    // this exact candidate: no FindSymbolFilePath/server lookup or download is
                    // initiated here. The OS can still redirect a mapped/reparse-backed path.
                    reader ??= new SymbolReader(TextWriter.Null, string.Empty);
                    var symbolModule = reader.OpenSymbolFile(candidate.Path);
                    if (symbolModule is null)
                    {
                        verified.Add(candidate with
                        {
                            VerificationState = LocalPdbVerificationState.VerificationUnavailable,
                            VerificationFailure = candidate.ContainerKind == LocalPdbContainerKind.Windows
                                ? LocalPdbVerificationFailure.CandidateOrReaderAmbiguous
                                : LocalPdbVerificationFailure.ReaderUnavailable
                        });
                        continue;
                    }

                    // Portable PDB CodeView identities have a fixed Age of 1. TraceEvent 3.2.2
                    // exposes PdbAge only on NativeSymbolModule, so keep that convention explicit.
                    var actualAge = symbolModule is NativeSymbolModule nativeModule
                        ? nativeModule.PdbAge
                        : 1;
                    var state = symbolModule.PdbGuid == expectedSignature && actualAge == expectedAge
                        ? LocalPdbVerificationState.ExactIdentityMatch
                        : LocalPdbVerificationState.IdentityMismatch;
                    verified.Add(candidate with { VerificationState = state });
                }
                catch (Exception ex) when (
                    candidate.ContainerKind == LocalPdbContainerKind.Portable &&
                    IsExplicitPortablePdbDataFailure(ex))
                {
                    verified.Add(candidate with { VerificationState = LocalPdbVerificationState.Invalid });
                }
                catch (Exception ex) when (IsPdbVerificationFailure(ex))
                {
                    verified.Add(candidate with
                    {
                        VerificationState = LocalPdbVerificationState.VerificationUnavailable,
                        VerificationFailure = ex is IOException or UnauthorizedAccessException
                            ? LocalPdbVerificationFailure.CandidateUnreadable
                            : candidate.ContainerKind == LocalPdbContainerKind.Windows
                                ? LocalPdbVerificationFailure.CandidateOrReaderAmbiguous
                                : LocalPdbVerificationFailure.ReaderUnavailable
                    });
                }
            }
        }
        finally
        {
            reader?.Dispose();
        }

        return verified;
    }

    private static string BuildIdentityUnverifiedReason(
        IReadOnlyList<LocalSymbolCandidate> candidates)
    {
        var hasUnreadable = candidates.Any(candidate =>
            candidate.VerificationFailure == LocalPdbVerificationFailure.CandidateUnreadable);
        var hasNativeReaderUnavailable = candidates.Any(candidate =>
            candidate.VerificationFailure == LocalPdbVerificationFailure.NativeReaderUnavailable);
        var hasCandidateOrReaderAmbiguity = candidates.Any(candidate =>
            candidate.VerificationFailure == LocalPdbVerificationFailure.CandidateOrReaderAmbiguous);
        var hasOtherReaderUnavailable = candidates.Any(candidate =>
            candidate.VerificationFailure == LocalPdbVerificationFailure.ReaderUnavailable);

        var reasons = new List<string>(4);
        if (hasUnreadable)
            reasons.Add("At least one local PDB candidate is unreadable.");
        if (hasNativeReaderUnavailable)
            reasons.Add("A Windows PDB candidate was found, but the native DIA reader dependency was not observed in the expected release layout; identity was not verified.");
        if (hasCandidateOrReaderAmbiguity)
            reasons.Add("A Windows PDB candidate's identity could not be verified; this probe cannot distinguish candidate/DIA incompatibility from a reader failure.");
        if (hasOtherReaderUnavailable)
            reasons.Add("The format-appropriate local PDB reader could not verify a candidate's GUID/Age.");
        reasons.Add("No remote SRV/UNC lookup was actively attempted; the OS may still redirect a configured local-looking root through a mapped drive or reparse point.");
        return string.Join(" ", reasons);
    }

    private static bool IsExplicitPortablePdbDataFailure(Exception exception)
        => exception is BadImageFormatException or InvalidDataException or EndOfStreamException;

    private static bool IsPdbVerificationFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or DllNotFoundException or
            TypeInitializationException or NotSupportedException or COMException or
            BadImageFormatException or InvalidDataException or InvalidOperationException or
            ArgumentException or EndOfStreamException;

    private static LocalPdbContainerKind ProbePdbContainer(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                return LocalPdbContainerKind.Missing;

            Span<byte> header = stackalloc byte[32];
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var count = stream.Read(header);
            if (count >= 16 &&
                header[0] == (byte)'B' && header[1] == (byte)'S' &&
                header[2] == (byte)'J' && header[3] == (byte)'B')
            {
                return LocalPdbContainerKind.Portable;
            }

            ReadOnlySpan<byte> msfPrefix = "Microsoft C/C++ MSF "u8;
            if (count >= msfPrefix.Length && header[..msfPrefix.Length].SequenceEqual(msfPrefix))
                return LocalPdbContainerKind.Windows;

            return LocalPdbContainerKind.Invalid;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return LocalPdbContainerKind.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocalPdbContainerKind.Unreadable;
        }
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
