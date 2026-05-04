namespace WprMcp.Core;

public sealed class SymbolService
{
    private readonly object _lock = new();

    public SymbolService()
    {
        // Pull initial value from env var so first call to GetPath returns it.
        CurrentPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
    }

    public string? CurrentPath { get; private set; }

    public string DefaultCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WprMcp", "Symbols");

    public void SetPath(string path, bool append)
    {
        lock (_lock)
        {
            CurrentPath = append && !string.IsNullOrEmpty(CurrentPath)
                ? $"{CurrentPath};{path}"
                : path;
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", CurrentPath);
        }
    }

    public void AddServer(string url, string? cacheDir)
    {
        var cache = cacheDir ?? DefaultCacheDir;
        Directory.CreateDirectory(cache);
        var entry = $"SRV*{cache}*{url}";
        // Reentrant lock: SetPath also takes _lock; .NET's Monitor allows the same thread
        // to re-enter, so the dedupe check + append are atomic against concurrent callers.
        lock (_lock)
        {
            if (CurrentPath != null && PathContainsEntry(CurrentPath, entry))
                return;
            SetPath(entry, append: true);
        }
    }

    private static bool PathContainsEntry(string path, string entry)
    {
        foreach (var part in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(part.Trim(), entry, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
