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
        SetPath(entry, append: true);
    }
}
