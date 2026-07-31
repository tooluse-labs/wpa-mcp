namespace WpaMcp.Core;

public sealed class SymbolService
{
    private readonly object _lock = new();
    private string? _currentPath;

    public SymbolService()
    {
        // Pull initial value from env var so first call to GetPath returns it.
        _currentPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
    }

    public string? CurrentPath
    {
        get
        {
            lock (_lock)
            {
                _currentPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
                return _currentPath;
            }
        }
    }

    public string DefaultCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WpaMcp", "Symbols");

    public void SetPath(string path, bool append)
    {
        lock (_lock)
        {
            var current = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
            _currentPath = append && !string.IsNullOrEmpty(current)
                ? $"{current};{path}"
                : path;
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", _currentPath);
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
            var current = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
            if (current != null && PathContainsEntry(current, entry))
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
