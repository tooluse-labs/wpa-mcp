using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SdkCandidateProbe;

public sealed record ProbeRuntimeIdentity(
    int ProcessId,
    string ProcessPath,
    string ProcessPathSha256,
    string EntryAssemblyPath,
    string EntryAssemblySha256,
    string OsPlatform,
    string OsArchitecture,
    string ProcessArchitecture,
    string RuntimeIdentifier,
    bool Is64BitOperatingSystem,
    bool Is64BitProcess,
    string LoadedHostFxrPath,
    string LoadedHostFxrSha256,
    string LoadedHostPolicyPath,
    string LoadedHostPolicySha256);

public sealed record ProbeOutput(string Value, int Invocation, ProbeRuntimeIdentity RuntimeIdentity);

public sealed record DelegatedProbeOutput(
    string Value,
    int Invocation,
    bool InnerTextObserved,
    bool InnerStructuredObserved,
    bool? PreservedIsError,
    ProbeRuntimeIdentity? RuntimeIdentity);

public sealed class SdkProbeTool
{
    private int _invocationCount;
    private int _cancellationObservationCount;

    public int InvocationCount => Volatile.Read(ref _invocationCount);

    public int CancellationObservationCount => Volatile.Read(ref _cancellationObservationCount);

    internal McpServerTool CreateDelegatingTool()
    {
        var typedTool = McpServerTool.Create(
            new Func<string, CancellationToken, IProgress<ProgressNotificationValue>?, Task<ProbeOutput>>(EchoAsync),
            new McpServerToolCreateOptions
            {
                Name = "sdk_probe_echo",
                Description = "Echoes a value through the public MCP SDK tool binder.",
                UseStructuredContent = true,
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false,
            });
        return new ProbeDelegatingTool(typedTool);
    }

    public async Task<ProbeOutput> EchoAsync(
        string value,
        CancellationToken cancellationToken,
        IProgress<ProgressNotificationValue>? progress)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        cancellationToken.ThrowIfCancellationRequested();

        var invocation = Interlocked.Increment(ref _invocationCount);
        progress?.Report(new ProgressNotificationValue
        {
            Progress = 1,
            Total = 1,
            Message = "sdk-probe-progress",
        });
        if (string.Equals(value, "cancel", StringComparison.Ordinal))
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationObservationCount);
                throw;
            }
        }

        return new ProbeOutput(value, invocation, CreateRuntimeIdentity());
    }

    private static ProbeRuntimeIdentity CreateRuntimeIdentity()
    {
        var processPath = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath is unavailable."));
        var entryAssemblyPath = Path.GetFullPath(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Entry assembly path is unavailable."));
        using var process = Process.GetCurrentProcess();
        var loadedModules = process.Modules.Cast<ProcessModule>().ToArray();
        static string RequireModulePath(ProcessModule[] modules, string moduleName) => Path.GetFullPath(
            modules.Single(module => string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)).FileName);
        var loadedHostFxrPath = RequireModulePath(loadedModules, "hostfxr.dll");
        var loadedHostPolicyPath = RequireModulePath(loadedModules, "hostpolicy.dll");
        return new ProbeRuntimeIdentity(
            Environment.ProcessId,
            processPath,
            Sha256(processPath),
            entryAssemblyPath,
            Sha256(entryAssemblyPath),
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Other",
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.RuntimeIdentifier,
            Environment.Is64BitOperatingSystem,
            Environment.Is64BitProcess,
            loadedHostFxrPath,
            Sha256(loadedHostFxrPath),
            loadedHostPolicyPath,
            Sha256(loadedHostPolicyPath));
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

internal sealed class ProbeDelegatingTool(McpServerTool innerTool) : DelegatingMcpServerTool(innerTool)
{
    public bool SawTextContent { get; private set; }

    public bool SawStructuredContent { get; private set; }

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var result = await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        SawTextContent = result.Content.OfType<TextContentBlock>().Any();
        SawStructuredContent = result.StructuredContent.HasValue;
        var inner = result.StructuredContent.HasValue
            ? result.StructuredContent.Value.Deserialize<ProbeOutput>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : null;
        var preservedIsError = result.IsError;

        result.Content =
        [
            new TextContentBlock { Text = "delegated-text" },
        ];
        result.StructuredContent = JsonSerializer.SerializeToElement(
            new DelegatedProbeOutput(
                "delegated-structured",
                inner?.Invocation ?? 0,
                SawTextContent,
                SawStructuredContent,
                preservedIsError,
                inner?.RuntimeIdentity),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return result;
    }
}
