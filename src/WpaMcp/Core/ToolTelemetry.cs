using System.Security.Cryptography;
using System.Text.Json;

namespace WpaMcp.Core;

internal sealed class ToolTelemetry : IDisposable
{
    private readonly byte[] _sessionSalt;
    private readonly object _lock = new();
    private readonly TextWriter? _writer;
    private bool _disposed;

    public ToolTelemetry(ToolTelemetryOptions options, byte[] sessionSalt, TextWriter? writer = null)
    {
        Options = options;
        _sessionSalt = sessionSalt;

        if (!options.Enabled)
            return;

        _writer = writer ?? OpenWriter(options);
    }

    public ToolTelemetryOptions Options { get; }

    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public bool Enabled => Options.Enabled;

    public static ToolTelemetry CreateFromEnvironment()
        => new(ToolTelemetryOptions.FromEnvironment(), RandomNumberGenerator.GetBytes(32));

    public string HashArguments(object? arguments)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(arguments, ToolTelemetryJson.Options);
        using var hmac = new HMACSHA256(_sessionSalt);
        return Convert.ToHexString(hmac.ComputeHash(bytes)).ToLowerInvariant();
    }

    public void RecordToolCall(
        string toolName,
        object? arguments,
        TimeSpan latency,
        int? responseBytes,
        bool error,
        TraceCacheCallSnapshot cache)
    {
        if (!Enabled)
            return;

        Write(new
        {
            event_type = "tool_call",
            session_id = SessionId,
            tool_name = toolName,
            argument_hash = HashArguments(arguments),
            latency_ms = Math.Round(latency.TotalMilliseconds, 3),
            response_bytes = responseBytes,
            error,
            cache_hit = cache.CacheHit,
            cache_hits = cache.Hits,
            cache_misses = cache.Misses,
            timestamp_utc = DateTimeOffset.UtcNow,
        });
    }

    public void RecordToolsListPayload(ToolListPayloadStats stats)
    {
        if (!Enabled)
            return;

        Write(new
        {
            event_type = "tools_list_payload",
            session_id = SessionId,
            tool_count = stats.ToolCount,
            payload_bytes = stats.PayloadBytes,
            max_payload_bytes = stats.MaxPayloadBytes,
            timestamp_utc = DateTimeOffset.UtcNow,
        });
    }

    public void RecordPromptInvocation(string method)
    {
        if (!Enabled)
            return;

        Write(new
        {
            event_type = "prompt_invocation",
            session_id = SessionId,
            method,
            timestamp_utc = DateTimeOffset.UtcNow,
        });
    }

    private void Write(object entry)
    {
        if (_writer is null)
            return;

        var line = JsonSerializer.Serialize(entry, ToolTelemetryJson.Options);
        lock (_lock)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    private static TextWriter OpenWriter(ToolTelemetryOptions options)
    {
        if (options.Destination == ToolTelemetryDestination.Stderr)
            return Console.Error;

        var path = options.FilePath ?? ToolTelemetryOptions.DefaultLogPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_writer is not null && !ReferenceEquals(_writer, Console.Error))
            _writer.Dispose();

        _disposed = true;
    }
}

internal sealed record ToolTelemetryOptions(
    bool Enabled,
    ToolTelemetryDestination Destination,
    string? FilePath)
{
    public static ToolTelemetryOptions Disabled { get; } = new(false, ToolTelemetryDestination.File, null);

    public static ToolTelemetryOptions FromEnvironment(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        var enabled = IsEnabled(getEnv("WPAMCP_TELEMETRY"));
        if (!enabled)
            return Disabled;

        var destination = string.Equals(getEnv("WPAMCP_TELEMETRY_DEST"), "stderr", StringComparison.OrdinalIgnoreCase)
            ? ToolTelemetryDestination.Stderr
            : ToolTelemetryDestination.File;

        return new ToolTelemetryOptions(
            true,
            destination,
            string.IsNullOrWhiteSpace(getEnv("WPAMCP_TELEMETRY_FILE")) ? null : getEnv("WPAMCP_TELEMETRY_FILE"));
    }

    public static string DefaultLogPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WpaMcp",
            "Logs",
            $"telemetry-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");

    private static bool IsEnabled(string? value)
        => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

internal enum ToolTelemetryDestination
{
    File,
    Stderr,
}

internal static class ToolTelemetryJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
