namespace WpaMcp;

internal sealed record McpServerOptions(
    string[] HostArgs,
    string? SymbolPath,
    int? CacheSize)
{
    public static McpServerOptions Parse(string[] args)
    {
        var hostArgs = new List<string>();
        string? symbolPath = null;
        int? cacheSize = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--symbol-path":
                    symbolPath = RequireValue(args, ref i, "--symbol-path");
                    break;

                case "--cache-size":
                    var rawCacheSize = RequireValue(args, ref i, "--cache-size");
                    if (!int.TryParse(rawCacheSize, out var parsedCacheSize) || parsedCacheSize <= 0)
                        throw new ArgumentException("--cache-size must be a positive integer.");
                    cacheSize = parsedCacheSize;
                    break;

                default:
                    hostArgs.Add(args[i]);
                    break;
            }
        }

        return new(hostArgs.ToArray(), symbolPath, cacheSize);
    }

    public void ApplyToEnvironment()
    {
        if (!string.IsNullOrEmpty(SymbolPath))
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", SymbolPath);

        if (CacheSize is not null)
            Environment.SetEnvironmentVariable("WPAMCP_CACHE_SIZE", CacheSize.Value.ToString());
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"{option} requires a value.");

        index++;
        return args[index];
    }
}
