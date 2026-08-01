namespace WpaMcp.Core;

/// <summary>
/// Owns the process-wide configured symbol path and creates immutable, query-local
/// effective-path snapshots. Query-local trace directories are never written back to
/// the environment.
/// </summary>
internal static class SymbolPathState
{
    private const string EnvironmentVariable = "_NT_SYMBOL_PATH";
    private static readonly object Sync = new();

    public static string? CurrentPath
    {
        get
        {
            lock (Sync)
                return Environment.GetEnvironmentVariable(EnvironmentVariable);
        }
    }

    public static string SetPath(string path, bool append)
    {
        lock (Sync)
        {
            var current = Environment.GetEnvironmentVariable(EnvironmentVariable);
            var updated = append && !string.IsNullOrEmpty(current)
                ? $"{current};{path}"
                : path;
            Environment.SetEnvironmentVariable(EnvironmentVariable, updated);
            return updated;
        }
    }

    public static string AddEntry(string entry)
    {
        lock (Sync)
        {
            var current = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (current is not null && ContainsEntry(current, entry))
                return current;

            var updated = string.IsNullOrEmpty(current) ? entry : $"{current};{entry}";
            Environment.SetEnvironmentVariable(EnvironmentVariable, updated);
            return updated;
        }
    }

    public static string GetEffectivePath(string tracePath)
        => GetSnapshot(tracePath).EffectivePath;

    public static SymbolPathSnapshot GetSnapshot(string tracePath)
    {
        lock (Sync)
        {
            var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return new SymbolPathSnapshot(
                configured,
                SymbolPathDefaults.BuildEffectivePath(configured, tracePath));
        }
    }

    private static bool ContainsEntry(string path, string entry)
    {
        foreach (var part in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(part.Trim(), entry, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

internal sealed record SymbolPathSnapshot(
    string? ConfiguredPath,
    string EffectivePath);
