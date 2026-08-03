using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WpaMcp.Cli;

internal static class SelfUpdateApplyCommand
{
    private const string ApplyArgument = "--apply-update";
    private const string CleanupArgument = "--cleanup-update";
    private const string HandoffFileName = "apply-update.v1.json";
    private const string HandoffSchemaVersion = "apply-update.v1";
    private const string EvidenceAssetName = "release-evidence.v1.json";
    private const string BundleAssetName = "wpa-mcp-win-x64.zip";
    private const string DenyPolicy = "deny";
    private const string TerminatePolicy = "terminate";
    private const long MaximumHandoffBytes = 64 * 1024;
    private const long MaximumEvidenceBytes = 1024 * 1024;
    private const int OperationAttempts = 5;

    private static readonly JsonSerializerOptions HandoffJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    internal static bool IsInvocation(string[] args) =>
        args.Length > 0 && args[0] is ApplyArgument or CleanupArgument;

    internal static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The built-in update helper is supported only on Windows.");
            return 1;
        }

        if (args.Length == 2 && args[0] == ApplyArgument)
            return await RunApplyAsync(args[1]).ConfigureAwait(false);
        if (args.Length == 4 &&
            args[0] == CleanupArgument &&
            int.TryParse(args[2], out var parentPid) &&
            long.TryParse(args[3], out var parentStartTimeUtcTicks))
        {
            return await RunCleanupAsync(
                args[1],
                parentPid,
                parentStartTimeUtcTicks).ConfigureAwait(false);
        }

        Console.Error.WriteLine("Invalid internal update-helper invocation.");
        return 2;
    }

    internal static async Task StartVerifiedApplyAsync(
        string stagedExecutable,
        string targetExecutable,
        string installRoot,
        string stageRoot,
        string expectedHash,
        string expectedVersion,
        bool stopRunning)
    {
        var normalizedStageRoot = RequireSafeStageRoot(stageRoot);
        var expectedStagedExecutable = Path.Combine(
            normalizedStageRoot,
            "bundle",
            "bin",
            "wpa-mcp.exe");
        if (!PathsEqual(stagedExecutable, expectedStagedExecutable))
            throw new InvalidOperationException("The staged update helper path is invalid.");

        using var parent = Process.GetCurrentProcess();
        var handoff = new ApplyUpdateHandoff(
            SchemaVersion: HandoffSchemaVersion,
            ParentPid: parent.Id,
            ParentStartTimeUtcTicks: parent.StartTime.ToUniversalTime().Ticks,
            TargetExecutable: Path.GetFullPath(targetExecutable),
            InstallRoot: Path.GetFullPath(installRoot),
            StageRoot: normalizedStageRoot,
            ExpectedZipSha256: expectedHash,
            ExpectedVersion: expectedVersion,
            RunningProcessPolicy: stopRunning ? TerminatePolicy : DenyPolicy);
        var handoffPath = Path.Combine(normalizedStageRoot, HandoffFileName);
        await using (var stream = new FileStream(
                         handoffPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 16 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    handoff,
                    HandoffJsonOptions)
                .ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        var startInfo = CreateApplyHelperStartInfo(stagedExecutable, handoffPath);
        using var helper = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the built-in update helper.");
    }

    internal static ProcessStartInfo CreateApplyHelperStartInfo(
        string stagedExecutable,
        string handoffPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(stagedExecutable),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(stagedExecutable))!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(ApplyArgument);
        startInfo.ArgumentList.Add(Path.GetFullPath(handoffPath));
        return startInfo;
    }

    internal static bool IsSafeUpdateStageRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parent = Directory.GetParent(fullPath)?.FullName;
            if (parent is null || !PathsEqual(parent, Path.GetTempPath()))
                return false;
            var name = Path.GetFileName(fullPath);
            const string prefix = "wpa-mcp-update-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                name.Length != prefix.Length + 32)
            {
                return false;
            }

            return name[prefix.Length..].All(Uri.IsHexDigit);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static async Task<int> RunApplyAsync(string handoffPath)
    {
        UpdateLog? log = null;
        ApplyPlan? plan = null;
        try
        {
            var handoff = await ReadHandoffAsync(handoffPath).ConfigureAwait(false);
            plan = ValidateHandoff(handoffPath, handoff);
            log = new UpdateLog(plan.LogPath, reset: true);
            log.Info(
                $"Applying wpa-mcp {plan.ExpectedVersion} with running-process " +
                $"policy {plan.RunningProcessPolicy}.");

            await ValidateVerifiedPayloadAsync(plan).ConfigureAwait(false);
            await WaitForProcessIdentityExitAsync(
                    plan.ParentPid,
                    plan.ParentStartTimeUtcTicks,
                    TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);
            await ApplyBundleAsync(plan, log).ConfigureAwait(false);
            log.Info($"wpa-mcp was updated successfully to {plan.ExpectedVersion}.");
            try
            {
                StartCleanupHelper(plan.TargetExecutable, plan.StageRoot);
            }
            catch (Exception cleanupException)
            {
                log.Warning(
                    "The update succeeded, but staging cleanup could not be started: " +
                    cleanupException.Message +
                    $". Staging remains at: {plan.StageRoot}");
            }
            return 0;
        }
        catch (Exception exception)
        {
            var message = $"wpa-mcp update failed: {exception.Message}";
            if (log is not null)
            {
                log.Error(message);
                if (plan is not null)
                    log.Error($"Verified staging files remain at: {plan.StageRoot}");
            }
            else
            {
                Console.Error.WriteLine(message);
            }
            return 1;
        }
    }

    private static async Task<int> RunCleanupAsync(
        string stageRoot,
        int parentPid,
        long parentStartTimeUtcTicks)
    {
        try
        {
            var normalizedStageRoot = RequireSafeStageRoot(stageRoot);
            await WaitForProcessIdentityExitAsync(
                    parentPid,
                    parentStartTimeUtcTicks,
                    TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);
            for (var attempt = 1; attempt <= OperationAttempts; attempt++)
            {
                try
                {
                    if (Directory.Exists(normalizedStageRoot))
                        Directory.Delete(normalizedStageRoot, recursive: true);
                    return 0;
                }
                catch (Exception exception) when (
                    attempt < OperationAttempts &&
                    exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            TryAppendInstalledLog($"Staging cleanup failed: {exception.Message}");
            return 1;
        }

        return 1;
    }

    private static async Task<ApplyUpdateHandoff> ReadHandoffAsync(string handoffPath)
    {
        var fullPath = Path.GetFullPath(handoffPath);
        var stageRoot = RequireSafeStageRoot(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("The update handoff has no parent directory."));
        if (!PathsEqual(fullPath, Path.Combine(stageRoot, HandoffFileName)))
            throw new InvalidDataException("The update handoff path is invalid.");
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumHandoffBytes ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The update handoff file is invalid.");
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync<ApplyUpdateHandoff>(
                   stream,
                   HandoffJsonOptions)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("The update handoff is empty.");
    }

    private static ApplyPlan ValidateHandoff(
        string handoffPath,
        ApplyUpdateHandoff handoff)
    {
        if (handoff.SchemaVersion != HandoffSchemaVersion ||
            handoff.ParentPid <= 0 ||
            handoff.ParentStartTimeUtcTicks <= 0 ||
            !IsSha256(handoff.ExpectedZipSha256) ||
            !TryParseVersion(handoff.ExpectedVersion) ||
            handoff.RunningProcessPolicy is not (DenyPolicy or TerminatePolicy))
        {
            throw new InvalidDataException("The update handoff contract is invalid.");
        }

        var stageRoot = RequireSafeStageRoot(handoff.StageRoot);
        if (!PathsEqual(stageRoot, Path.GetDirectoryName(Path.GetFullPath(handoffPath))!))
            throw new InvalidDataException("The update handoff stage root does not match its path.");
        var installRoot = Path.GetFullPath(handoff.InstallRoot);
        var targetExecutable = Path.GetFullPath(handoff.TargetExecutable);
        var expectedTarget = Path.Combine(installRoot, "bin", "wpa-mcp.exe");
        if (!PathsEqual(targetExecutable, expectedTarget) || !File.Exists(targetExecutable))
            throw new InvalidDataException("The update target is outside the bundle layout.");

        var stagedExecutable = Path.Combine(stageRoot, "bundle", "bin", "wpa-mcp.exe");
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) ||
            !PathsEqual(processPath, stagedExecutable))
        {
            throw new InvalidDataException(
                "The apply helper is not running from the verified staged executable.");
        }

        return new ApplyPlan(
            handoff.ParentPid,
            handoff.ParentStartTimeUtcTicks,
            targetExecutable,
            installRoot,
            stageRoot,
            stagedExecutable,
            handoff.ExpectedZipSha256.ToLowerInvariant(),
            handoff.ExpectedVersion,
            handoff.RunningProcessPolicy,
            Path.Combine(installRoot, ".wpa-mcp-update.log"));
    }

    private static async Task ValidateVerifiedPayloadAsync(ApplyPlan plan)
    {
        var currentVersion = CurrentVersion();
        if (!string.Equals(currentVersion, plan.ExpectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Apply helper version '{currentVersion}' does not match " +
                $"'{plan.ExpectedVersion}'.");
        }

        var evidencePath = Path.Combine(plan.StageRoot, EvidenceAssetName);
        var evidenceInfo = new FileInfo(evidencePath);
        if (!evidenceInfo.Exists || evidenceInfo.Length is <= 0 or > MaximumEvidenceBytes)
            throw new InvalidDataException("Release evidence is missing or oversized.");
        var bytes = await File.ReadAllBytesAsync(evidencePath).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(bytes))
        {
            var root = document.RootElement;
            var evidenceHash = root.GetProperty("assets").GetProperty("zipSha256").GetString();
            if (root.GetProperty("schemaVersion").GetString() != "release-evidence.v1" ||
                root.GetProperty("version").GetString() != plan.ExpectedVersion ||
                root.GetProperty("ref").GetString() != $"refs/tags/v{plan.ExpectedVersion}" ||
                !string.Equals(
                    evidenceHash,
                    plan.ExpectedZipSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Release evidence does not match the apply handoff.");
            }
        }

        var bundlePath = Path.Combine(plan.StageRoot, BundleAssetName);
        var actualHash = ComputeSha256(bundlePath);
        if (!string.Equals(
                actualHash,
                plan.ExpectedZipSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The staged ZIP hash changed after verification.");
        }

        RequireFile(plan.StagedExecutable, "bin\\wpa-mcp.exe");
        RequireFile(
            Path.Combine(plan.StageRoot, "bundle", "native", "amd64", "msdia140.dll"),
            "native\\amd64\\msdia140.dll");
        RequireFile(
            Path.Combine(
                plan.StageRoot,
                "bundle",
                "native",
                "amd64",
                "KernelTraceControl.dll"),
            "native\\amd64\\KernelTraceControl.dll");
    }

    private static async Task ApplyBundleAsync(ApplyPlan plan, UpdateLog log)
    {
        var terminate = plan.RunningProcessPolicy == TerminatePolicy;
        var token = Guid.NewGuid().ToString("N");
        var stagedNative = Path.Combine(plan.StageRoot, "bundle", "native", "amd64");
        var targetNative = Path.Combine(plan.InstallRoot, "native", "amd64");
        var newExecutable = $"{plan.TargetExecutable}.new-{token}";
        var oldExecutable = $"{plan.TargetExecutable}.old-{token}";
        var newNative = $"{targetNative}.new-{token}";
        var oldNative = $"{targetNative}.old-{token}";
        var markerPath = Path.Combine(plan.InstallRoot, ".wpa-mcp-win-x64.sha256");
        var targetNativeExisted = Directory.Exists(targetNative);
        var markerExisted = File.Exists(markerPath);
        var markerBytes = markerExisted ? await File.ReadAllBytesAsync(markerPath) : null;
        var oldExecutableMoved = false;
        var oldNativeMoved = false;
        var newNativeInstalled = false;

        try
        {
            await EnsureTargetAvailableAsync(
                    plan.TargetExecutable,
                    terminate,
                    log)
                .ConfigureAwait(false);
            File.Copy(plan.StagedExecutable, newExecutable, overwrite: false);
            Directory.CreateDirectory(newNative);
            if (targetNativeExisted)
                CopyTopLevelFiles(targetNative, newNative, overwrite: false);
            CopyTopLevelFiles(stagedNative, newNative, overwrite: true);

            await MoveTargetExecutableAsync(
                    plan.TargetExecutable,
                    oldExecutable,
                    terminate,
                    log)
                .ConfigureAwait(false);
            oldExecutableMoved = true;
            if (targetNativeExisted)
            {
                await MoveDirectoryWithRetryAsync(targetNative, oldNative)
                    .ConfigureAwait(false);
                oldNativeMoved = true;
            }
            await MoveDirectoryWithRetryAsync(newNative, targetNative).ConfigureAwait(false);
            newNativeInstalled = true;
            await MoveFileWithRetryAsync(newExecutable, plan.TargetExecutable)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    markerPath,
                    plan.ExpectedZipSha256 + Environment.NewLine,
                    Encoding.ASCII)
                .ConfigureAwait(false);

            var reportedVersion = await ReadReportedVersionAsync(plan.TargetExecutable)
                .ConfigureAwait(false);
            if (!string.Equals(
                    reportedVersion,
                    $"WpaMcp {plan.ExpectedVersion}",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Updated executable reported '{reportedVersion}'.");
            }

            await DeleteFileWithRetryAsync(oldExecutable).ConfigureAwait(false);
            if (oldNativeMoved)
                await DeleteDirectoryWithRetryAsync(oldNative).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Exception? rollbackFailure = null;
            try
            {
                if (oldExecutableMoved)
                {
                    await DeleteFileWithRetryAsync(plan.TargetExecutable).ConfigureAwait(false);
                    if (File.Exists(oldExecutable))
                    {
                        await MoveFileWithRetryAsync(oldExecutable, plan.TargetExecutable)
                            .ConfigureAwait(false);
                    }
                }
                if (oldNativeMoved)
                {
                    await DeleteDirectoryWithRetryAsync(targetNative).ConfigureAwait(false);
                    if (Directory.Exists(oldNative))
                    {
                        await MoveDirectoryWithRetryAsync(oldNative, targetNative)
                            .ConfigureAwait(false);
                    }
                }
                else if (newNativeInstalled && !targetNativeExisted)
                {
                    await DeleteDirectoryWithRetryAsync(targetNative).ConfigureAwait(false);
                }

                if (markerExisted && markerBytes is not null)
                    await File.WriteAllBytesAsync(markerPath, markerBytes).ConfigureAwait(false);
                else if (File.Exists(markerPath))
                    File.Delete(markerPath);
            }
            catch (Exception rollbackException)
            {
                rollbackFailure = rollbackException;
            }

            TryDeleteFile(newExecutable);
            TryDeleteDirectory(newNative);
            if (rollbackFailure is not null)
            {
                throw new InvalidOperationException(
                    $"{exception.Message} Rollback also failed: {rollbackFailure.Message}",
                    exception);
            }
            throw;
        }
    }

    private static async Task EnsureTargetAvailableAsync(
        string targetExecutable,
        bool terminate,
        UpdateLog log)
    {
        for (var attempt = 1; attempt <= OperationAttempts; attempt++)
        {
            var inspection = InspectRunningInstances(targetExecutable);
            if (inspection.UninspectablePids.Count > 0)
            {
                throw new InvalidOperationException(
                    "Cannot safely inspect running wpa-mcp PIDs: " +
                    string.Join(", ", inspection.UninspectablePids) + ".");
            }
            if (inspection.BlockingPids.Count == 0)
                return;
            if (!terminate)
            {
                throw new InvalidOperationException(
                    "Update blocked by running exact-path PIDs: " +
                    string.Join(", ", inspection.BlockingPids) +
                    ". Close their MCP clients or rerun with --stop-running.");
            }

            log.Info(
                "Terminating exact-path wpa-mcp PIDs: " +
                string.Join(", ", inspection.BlockingPids) + ".");
            foreach (var processId in inspection.BlockingPids)
                await TerminateExactProcessAsync(processId, targetExecutable).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }

        var finalInspection = InspectRunningInstances(targetExecutable);
        if (finalInspection.UninspectablePids.Count > 0 ||
            finalInspection.BlockingPids.Count > 0)
        {
            throw new InvalidOperationException(
                "MCP clients repeatedly restarted or retained the installed executable.");
        }
    }

    private static async Task TerminateExactProcessAsync(
        int processId,
        string targetExecutable)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            var candidatePath = ReadProcessPath(process)
                ?? throw new InvalidOperationException(
                    $"Could not revalidate exact-path PID {processId}.");
            if (!PathsEqual(candidatePath, targetExecutable))
                return;
            process.Kill(entireProcessTree: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Timed out waiting for exact-path PID {processId} to exit.");
            }
        }
    }

    private static async Task MoveTargetExecutableAsync(
        string source,
        string destination,
        bool terminate,
        UpdateLog log)
    {
        for (var attempt = 1; attempt <= OperationAttempts; attempt++)
        {
            await EnsureTargetAvailableAsync(source, terminate, log).ConfigureAwait(false);
            try
            {
                File.Move(source, destination, overwrite: false);
                return;
            }
            catch (Exception exception) when (
                attempt < OperationAttempts &&
                exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
        }
    }

    private static RunningProcessInspection InspectRunningInstances(string targetExecutable)
    {
        var blockingPids = new List<int>();
        var uninspectablePids = new List<int>();
        var processName = Path.GetFileNameWithoutExtension(targetExecutable);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                    continue;
                var candidatePath = ReadProcessPath(process);
                if (candidatePath is null)
                {
                    if (IsProcessAlive(process.Id))
                        uninspectablePids.Add(process.Id);
                }
                else if (PathsEqual(candidatePath, targetExecutable))
                {
                    blockingPids.Add(process.Id);
                }
            }
        }

        return new RunningProcessInspection(
            blockingPids.Distinct().Order().ToArray(),
            uninspectablePids.Distinct().Order().ToArray());
    }

    private static string? ReadProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitForProcessIdentityExitAsync(
        int processId,
        long expectedStartTimeUtcTicks,
        TimeSpan timeoutValue)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            if (process.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks)
                return;
            using var timeout = new CancellationTokenSource(timeoutValue);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Process {processId} did not exit within {timeoutValue.TotalSeconds:0} seconds.");
            }
        }
    }

    private static async Task MoveFileWithRetryAsync(string source, string destination)
    {
        for (var attempt = 1; attempt <= OperationAttempts; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: false);
                return;
            }
            catch (Exception exception) when (
                attempt < OperationAttempts &&
                exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }
    }

    private static async Task MoveDirectoryWithRetryAsync(
        string source,
        string destination)
    {
        for (var attempt = 1; attempt <= OperationAttempts; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (
                attempt < OperationAttempts &&
                exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteFileWithRetryAsync(string path)
    {
        if (!File.Exists(path))
            return;
        for (var attempt = 1; attempt <= OperationAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception exception) when (
                attempt < OperationAttempts &&
                exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        if (!Directory.Exists(path))
            return;
        for (var attempt = 1; attempt <= OperationAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < OperationAttempts &&
                exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }
    }

    private static void CopyTopLevelFiles(string source, string destination, bool overwrite)
    {
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite);
    }

    private static async Task<string> ReadReportedVersionAsync(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not validate the installed executable.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidDataException("Updated executable validation timed out.");
        }

        var output = (await outputTask.ConfigureAwait(false)).Trim();
        var error = (await errorTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"Updated executable validation failed with exit {process.ExitCode}: {error}");
        }
        return output;
    }

    private static void StartCleanupHelper(string installedExecutable, string stageRoot)
    {
        using var parent = Process.GetCurrentProcess();
        var startInfo = new ProcessStartInfo
        {
            FileName = installedExecutable,
            WorkingDirectory = Path.GetDirectoryName(installedExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(CleanupArgument);
        startInfo.ArgumentList.Add(stageRoot);
        startInfo.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(
            parent.StartTime.ToUniversalTime().Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        using var cleanup = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start staging cleanup.");
    }

    private static string RequireSafeStageRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsSafeUpdateStageRoot(fullPath) || !Directory.Exists(fullPath))
            throw new InvalidDataException("The update staging root is invalid.");
        if (new DirectoryInfo(fullPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The update staging root cannot be a reparse point.");
        return fullPath;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static string CurrentVersion() =>
        typeof(SelfUpdateApplyCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(SelfUpdateApplyCommand).Assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("The apply-helper version is unavailable.");

    private static bool TryParseVersion(string value) =>
        Version.TryParse(value.Split(['-', '+'], 2)[0], out var version) &&
        version.Build >= 0;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void RequireFile(string path, string relativePath)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"Release bundle is missing {relativePath}.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryAppendInstalledLog(string message)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            var bin = processPath is null ? null : Path.GetDirectoryName(processPath);
            var installRoot = bin is null ? null : Directory.GetParent(bin)?.FullName;
            if (installRoot is not null)
            {
                File.AppendAllText(
                    Path.Combine(installRoot, ".wpa-mcp-update.log"),
                    $"{DateTime.UtcNow:o} [ERROR] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private sealed class UpdateLog
    {
        private readonly string _path;

        public UpdateLog(string path, bool reset)
        {
            _path = path;
            if (reset)
            {
                File.WriteAllText(
                    _path,
                    $"{DateTime.UtcNow:o} [INFO] Built-in update helper started.{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }

        public void Info(string message) => Write("INFO", message, error: false);

        public void Warning(string message) => Write("WARN", message, error: false);

        public void Error(string message) => Write("ERROR", message, error: true);

        private void Write(string level, string message, bool error)
        {
            try
            {
                File.AppendAllText(
                    _path,
                    $"{DateTime.UtcNow:o} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
            }

            if (error)
                Console.Error.WriteLine(message);
            else
                Console.Out.WriteLine(message);
        }
    }

    private sealed record ApplyUpdateHandoff(
        string SchemaVersion,
        int ParentPid,
        long ParentStartTimeUtcTicks,
        string TargetExecutable,
        string InstallRoot,
        string StageRoot,
        string ExpectedZipSha256,
        string ExpectedVersion,
        string RunningProcessPolicy);

    private sealed record ApplyPlan(
        int ParentPid,
        long ParentStartTimeUtcTicks,
        string TargetExecutable,
        string InstallRoot,
        string StageRoot,
        string StagedExecutable,
        string ExpectedZipSha256,
        string ExpectedVersion,
        string RunningProcessPolicy,
        string LogPath);

    private sealed record RunningProcessInspection(
        IReadOnlyList<int> BlockingPids,
        IReadOnlyList<int> UninspectablePids);
}
