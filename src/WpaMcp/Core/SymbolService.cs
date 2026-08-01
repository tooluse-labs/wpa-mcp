namespace WpaMcp.Core;

public sealed class SymbolService
{
    public string? CurrentPath => SymbolPathState.CurrentPath;

    public string DefaultCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WpaMcp", "Symbols");

    public void SetPath(string path, bool append)
        => SymbolPathState.SetPath(path, append);

    public void AddServer(string url, string? cacheDir)
    {
        var cache = cacheDir ?? DefaultCacheDir;
        Directory.CreateDirectory(cache);
        var entry = $"SRV*{cache}*{url}";
        SymbolPathState.AddEntry(entry);
    }
}
