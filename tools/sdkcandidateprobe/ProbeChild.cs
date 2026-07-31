using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SdkCandidateProbe;

internal sealed class ProbeChild : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderrTask;
    private readonly string _auditPath;
    private readonly Dictionary<string, int> _progressNotificationsByToken = new(StringComparer.Ordinal);

    private ProbeChild(Process process, string requestedPath, string requestedSha256, string processPath, Architecture processArchitecture, string auditPath)
    {
        _process = process;
        _stderrTask = process.StandardError.ReadToEndAsync();
        RequestedPath = requestedPath;
        RequestedSha256Before = requestedSha256;
        ProcessPath = processPath;
        ProcessArchitecture = processArchitecture;
        _auditPath = auditPath;
    }

    public int ProgressNotificationCount { get; private set; }

    public int GetProgressNotificationCount(string token) =>
        _progressNotificationsByToken.TryGetValue(token, out var count) ? count : 0;

    public int ProcessId => _process.Id;

    public string RequestedPath { get; }

    public string RequestedSha256Before { get; }

    public string ProcessPath { get; }

    public Architecture ProcessArchitecture { get; }

    public static Task<ProbeChild> StartAsync(string hostCommand, string protocolRevision, string protocolProfile)
    {
        var requestedPath = Path.GetFullPath(hostCommand);
        var requestedSha256 = Sha256(requestedPath);
        var auditPath = Path.Combine(Path.GetTempPath(), $"sdkcandidateprobe-audit-{Guid.NewGuid():N}.json");
        var managedAssembly = string.Equals(Path.GetExtension(requestedPath), ".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = managedAssembly ? ResolveDotNetHost() : requestedPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (managedAssembly)
        {
            startInfo.ArgumentList.Add(requestedPath);
        }
        startInfo.ArgumentList.Add("--serve");
        startInfo.ArgumentList.Add("--protocol-revision");
        startInfo.ArgumentList.Add(protocolRevision);
        startInfo.ArgumentList.Add("--protocol-profile");
        startInfo.ArgumentList.Add(protocolProfile);
        startInfo.ArgumentList.Add("--audit-path");
        startInfo.ArgumentList.Add(auditPath);
        var process = new Process { StartInfo = startInfo };
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new InvalidOperationException($"Probe child did not start: {startInfo.FileName}");
            string processPath;
            try { processPath = process.MainModule?.FileName ?? startInfo.FileName; }
            catch { processPath = startInfo.FileName; }
            return Task.FromResult(new ProbeChild(process, requestedPath, requestedSha256, processPath, GetProcessArchitecture(process), auditPath));
        }
        catch
        {
            if (started)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
            }
            process.Dispose();
            File.Delete(auditPath);
            throw;
        }
    }

    internal static string ResolveDotNetHost() =>
        Environment.GetEnvironmentVariable("WPAMCP_DOTNET_HOST")
        ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? "dotnet";

    public async Task<JsonNode> SendRequestAsync(
        JsonObject request,
        string expectedId,
        bool crlf,
        CancellationToken cancellationToken) =>
        await SendRequestAsync(request, expectedId, crlf, cancellationToken, progressObserved: null).ConfigureAwait(false);

    public async Task<JsonNode> SendRawRequestAsync(
        ReadOnlyMemory<byte> frame,
        string expectedIdJson,
        CancellationToken cancellationToken)
    {
        await _process.StandardInput.BaseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        var canonicalExpectedId = JsonNode.Parse(expectedIdJson)?.ToJsonString()
            ?? throw new JsonException("Expected request id was empty JSON.");
        return await ReadResponseAsync(canonicalExpectedId, cancellationToken, progressObserved: null).ConfigureAwait(false);
    }

    private async Task<JsonNode> SendRequestAsync(
        JsonObject request,
        string expectedId,
        bool crlf,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? progressObserved)
    {
        await WriteAsync(request, crlf, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(JsonSerializer.Serialize(expectedId), cancellationToken, progressObserved).ConfigureAwait(false);
    }

    private async Task<JsonNode> ReadResponseAsync(
        string expectedIdJson,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? progressObserved)
    {
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Probe child closed stdout before responding.");
            var message = JsonNode.Parse(line) ?? throw new JsonException("Probe child emitted an empty JSON value.");
            if (message["method"]?.GetValue<string>() == "notifications/progress")
            {
                ProgressNotificationCount++;
                var token = message["params"]?["progressToken"]?.GetValue<string>();
                if (token is not null)
                {
                    _progressNotificationsByToken[token] = GetProgressNotificationCount(token) + 1;
                }
                progressObserved?.TrySetResult(true);
                continue;
            }

            if (message["id"]?.ToJsonString() == expectedIdJson)
            {
                return message;
            }
        }
    }

    public async Task<CancellationProbeResult> SendCancellableRequestAsync(
        JsonObject request,
        JsonObject cancellationNotification,
        string expectedId,
        TimeSpan stageTimeout,
        CancellationToken cancellationToken)
    {
        var progressObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var localWaiter = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var responseTask = SendRequestAsync(request, expectedId, crlf: false, localWaiter.Token, progressObserved);

        using (var progressTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            progressTimeout.CancelAfter(stageTimeout);
            await progressObserved.Task.WaitAsync(progressTimeout.Token).ConfigureAwait(false);
        }

        await SendNotificationAsync(cancellationNotification, crlf: false, cancellationToken).ConfigureAwait(false);
        var responseObserved = await Task.WhenAny(
            responseTask,
            Task.Delay(stageTimeout, cancellationToken)).ConfigureAwait(false) == responseTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (responseObserved)
        {
            await responseTask.ConfigureAwait(false);
        }
        else
        {
            localWaiter.Cancel();
            try
            {
                await responseTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (localWaiter.IsCancellationRequested)
            {
            }
        }

        return new CancellationProbeResult(true, responseObserved);
    }

    public Task SendNotificationAsync(JsonObject notification, bool crlf, CancellationToken cancellationToken) =>
        WriteAsync(notification, crlf, cancellationToken);

    private async Task WriteAsync(JsonObject message, bool crlf, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + (crlf ? "\r\n" : "\n"));
        await _process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseInputAndWaitAsync(CancellationToken cancellationToken)
    {
        _process.StandardInput.Close();
        using var childTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        childTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _process.WaitForExitAsync(childTimeout.Token).ConfigureAwait(false);
        if (_process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Probe child exited {_process.ExitCode}: {await _stderrTask.ConfigureAwait(false)}");
        }
    }

    public Task<string> ReadStandardErrorAsync() => _stderrTask;

    public ProbeChildAudit ReadAudit()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(_auditPath));
        return new ProbeChildAudit(
            document.RootElement.GetProperty("handlerInvocationCount").GetInt32(),
            document.RootElement.GetProperty("handlerCancellationObservationCount").GetInt32());
    }

    public string GetRequestedSha256After() => Sha256(RequestedPath);

    public string GetProcessSha256() => Sha256(ProcessPath);

    public ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.StandardInput.Close();
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5_000);
        }

        _process.Dispose();
        File.Delete(_auditPath);
        return ValueTask.CompletedTask;
    }

    internal static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static Architecture GetProcessArchitecture(Process process)
    {
        if (!IsWow64Process2(process.Handle, out var processMachine, out var nativeMachine))
        {
            throw new InvalidOperationException($"Unable to query probe child architecture: {Marshal.GetLastWin32Error()}.");
        }
        var machine = processMachine == 0 ? nativeMachine : processMachine;
        return machine switch
        {
            0x8664 => Architecture.X64,
            0xaa64 => Architecture.Arm64,
            0x014c => Architecture.X86,
            _ => throw new InvalidOperationException($"Unsupported probe child machine 0x{machine:x4}."),
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}

internal sealed record CancellationProbeResult(bool ProgressObserved, bool ResponseObserved);

internal sealed record ProbeChildAudit(int HandlerInvocationCount, int HandlerCancellationObservationCount);
