namespace WpaMcp.Core;

internal static class SymbolPathDefaults
{
    private const string SymbolPathEnvVar = "_NT_SYMBOL_PATH";
    private static readonly object Lock = new();

    public static string? EnsureTraceDirectory(string tracePath)
    {
        var traceDir = TraceDirectory(tracePath);
        if (traceDir is null)
            return Environment.GetEnvironmentVariable(SymbolPathEnvVar);

        lock (Lock)
        {
            var current = Environment.GetEnvironmentVariable(SymbolPathEnvVar);
            var effective = AddLocalPathBeforeSymbolServers(current, traceDir);
            if (!string.Equals(current, effective, StringComparison.Ordinal))
                Environment.SetEnvironmentVariable(SymbolPathEnvVar, effective);
            return effective;
        }
    }

    internal static string AddLocalPathBeforeSymbolServers(string? currentPath, string localPath)
    {
        var normalizedLocalPath = NormalizePathEntry(localPath);
        if (string.IsNullOrWhiteSpace(normalizedLocalPath))
            return currentPath ?? "";

        var parts = SplitEntries(currentPath).ToList();
        if (parts.Any(part => IsSameLocalPath(part, normalizedLocalPath)))
            return string.Join(';', parts);

        var firstServer = parts.FindIndex(IsSymbolServerEntry);
        if (firstServer >= 0)
            parts.Insert(firstServer, normalizedLocalPath);
        else
            parts.Add(normalizedLocalPath);

        return string.Join(';', parts);
    }

    private static string? TraceDirectory(string tracePath)
    {
        if (string.IsNullOrWhiteSpace(tracePath))
            return null;

        var directory = Path.GetDirectoryName(Path.GetFullPath(tracePath));
        return string.IsNullOrWhiteSpace(directory) ? null : NormalizePathEntry(directory);
    }

    private static IEnumerable<string> SplitEntries(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            yield break;

        foreach (var part in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!string.IsNullOrWhiteSpace(part))
                yield return part;
    }

    private static bool IsSymbolServerEntry(string entry)
        => entry.StartsWith("SRV*", StringComparison.OrdinalIgnoreCase) ||
           entry.StartsWith("SYMSRV*", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameLocalPath(string entry, string localPath)
    {
        if (IsSymbolServerEntry(entry))
            return false;

        return TryNormalizePathEntry(entry, out var normalizedEntry) &&
               string.Equals(normalizedEntry, localPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathEntry(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static bool TryNormalizePathEntry(string path, out string normalizedPath)
    {
        try
        {
            normalizedPath = NormalizePathEntry(path);
            return true;
        }
        catch (Exception) when (path.Contains("://", StringComparison.Ordinal))
        {
            normalizedPath = "";
            return false;
        }
        catch (ArgumentException)
        {
            normalizedPath = "";
            return false;
        }
        catch (NotSupportedException)
        {
            normalizedPath = "";
            return false;
        }
    }
}
