using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WpaMcp.Cli;

internal static class SelfUpdateCommand
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/tooluse-labs/wpa-mcp/releases/latest";
    private const string BundleAssetName = "wpa-mcp-win-x64.zip";
    private const string EvidenceAssetName = "release-evidence.v1.json";
    private const long MaxBundleBytes = 512L * 1024 * 1024;
    private const long MaxEvidenceBytes = 1024 * 1024;
    private const int UpdateBlockedExitCode = 3;
    private const string StopRunningArgument = "--stop-running";

    internal static bool IsInvocation(string[] args) =>
        args.Length > 0 && args[0] is "update" or "--update";

    internal static bool TryParseArguments(
        IReadOnlyList<string> args,
        out bool stopRunning)
    {
        stopRunning = false;
        if (args.Count == 1 && args[0] is "update" or "--update")
            return true;
        if (args.Count == 2 &&
            args[0] is "update" or "--update" &&
            string.Equals(args[1], StopRunningArgument, StringComparison.Ordinal))
        {
            stopRunning = true;
            return true;
        }

        return false;
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out var stopRunning))
        {
            Console.Error.WriteLine(
                "usage: wpa-mcp.exe update [--stop-running]");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("wpa-mcp update is supported only on Windows.");
            return 1;
        }

        string? workingRoot = null;
        var handedOff = false;
        try
        {
            var executablePath = RequireInstalledExecutable();
            var executableDirectory = Path.GetDirectoryName(executablePath)!;
            var installRoot = Directory.GetParent(executableDirectory)?.FullName
                ?? throw new InvalidOperationException(
                    "The installed executable directory must have a parent bundle directory.");
            var running = InspectRunningInstances(executablePath);
            if (running.UninspectablePids.Count > 0)
            {
                Console.Error.WriteLine(BuildUninspectableProcessMessage(
                    executablePath,
                    running.UninspectablePids));
                return UpdateBlockedExitCode;
            }
            if (running.BlockingPids.Count > 0 && !stopRunning)
            {
                Console.Error.WriteLine(BuildBlockingProcessMessage(
                    executablePath,
                    running.BlockingPids));
                return UpdateBlockedExitCode;
            }
            if (running.BlockingPids.Count > 0)
            {
                Console.Error.WriteLine(
                    $"[update] {StopRunningArgument} authorized termination of " +
                    $"{running.BlockingPids.Count} exact-path instance(s) after the " +
                    "new release is verified.");
                Console.Error.WriteLine(
                    "[update] Their in-memory trace and symbol state will be lost; " +
                    "MCP client processes will not be terminated.");
            }

            var currentVersionText = CurrentVersion();
            var currentVersion = ParseVersion(currentVersionText, "installed version");

            using var client = CreateHttpClient(currentVersionText);
            Console.Error.WriteLine("[update] Checking the latest GitHub release...");
            var release = await GetLatestReleaseAsync(client).ConfigureAwait(false);
            var latestVersion = ParseVersion(release.Version, "release version");
            if (latestVersion <= currentVersion)
            {
                Console.WriteLine(
                    $"wpa-mcp is already up to date ({currentVersionText}).");
                return 0;
            }

            workingRoot = Path.Combine(
                Path.GetTempPath(),
                "wpa-mcp-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingRoot);
            var evidencePath = Path.Combine(workingRoot, EvidenceAssetName);
            var bundlePath = Path.Combine(workingRoot, BundleAssetName);
            var stagedRoot = Path.Combine(workingRoot, "bundle");

            Console.Error.WriteLine(
                $"[update] Downloading and verifying {release.TagName}...");
            await DownloadAssetAsync(
                client, release.Evidence, evidencePath, MaxEvidenceBytes).ConfigureAwait(false);
            var expectedHash = await ReadExpectedBundleHashAsync(
                evidencePath, release).ConfigureAwait(false);
            var apiHash = ParseAssetDigest(release.Bundle.Digest);
            if (apiHash is not null &&
                !string.Equals(apiHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "GitHub's asset digest does not match the immutable release evidence.");
            }

            await DownloadAssetAsync(
                client, release.Bundle, bundlePath, MaxBundleBytes).ConfigureAwait(false);
            var actualHash = ComputeSha256(bundlePath);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Downloaded bundle SHA-256 mismatch: expected {expectedHash}, " +
                    $"received {actualHash}.");
            }

            Directory.CreateDirectory(stagedRoot);
            ZipFile.ExtractToDirectory(bundlePath, stagedRoot);
            var stagedExecutable = Path.Combine(stagedRoot, "bin", "wpa-mcp.exe");
            RequireBundleFile(stagedExecutable, "bin\\wpa-mcp.exe");
            RequireBundleFile(
                Path.Combine(stagedRoot, "native", "amd64", "msdia140.dll"),
                "native\\amd64\\msdia140.dll");
            RequireBundleFile(
                Path.Combine(stagedRoot, "native", "amd64", "KernelTraceControl.dll"),
                "native\\amd64\\KernelTraceControl.dll");

            var reportedVersion = await ReadReportedVersionAsync(
                stagedExecutable).ConfigureAwait(false);
            var expectedVersionOutput = $"WpaMcp {release.Version}";
            if (!string.Equals(
                    reportedVersion, expectedVersionOutput, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Staged executable reported '{reportedVersion}'; " +
                    $"expected '{expectedVersionOutput}'.");
            }

            await SelfUpdateApplyCommand.StartVerifiedApplyAsync(
                stagedExecutable,
                executablePath,
                installRoot,
                workingRoot,
                expectedHash,
                release.Version,
                stopRunning).ConfigureAwait(false);
            handedOff = true;

            Console.WriteLine(
                $"wpa-mcp {release.Version} is verified and staged. " +
                "The built-in updater will replace the installed bundle after this process exits.");
            Console.WriteLine(
                $"Update status log: {Path.Combine(installRoot, ".wpa-mcp-update.log")}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"wpa-mcp update failed: {exception.Message}");
            return 1;
        }
        finally
        {
            if (!handedOff && workingRoot is not null)
                TryDeleteDirectory(workingRoot);
        }
    }

    private static string RequireInstalledExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("The current executable path is unavailable.");

        var fullPath = Path.GetFullPath(processPath);
        if (!string.Equals(
                Path.GetFileName(fullPath), "wpa-mcp.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Self-update must be run from the installed wpa-mcp.exe, not a dotnet or " +
                "development host. Run the one-line installer once, then retry.");
        }

        return fullPath;
    }

    private static RunningInstanceInspection InspectRunningInstances(
        string executablePath)
    {
        var blockingPids = new List<int>();
        var uninspectablePids = new List<int>();
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                var processId = process.Id;
                if (processId == Environment.ProcessId)
                    continue;

                try
                {
                    var candidatePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(candidatePath))
                        throw new InvalidOperationException("The process path is unavailable.");
                    if (PathsEqual(candidatePath, executablePath))
                        blockingPids.Add(processId);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException)
                {
                    if (IsProcessAlive(processId))
                        uninspectablePids.Add(processId);
                }
            }
        }

        return new RunningInstanceInspection(
            blockingPids.Distinct().Order().ToArray(),
            uninspectablePids.Distinct().Order().ToArray());
    }

    internal static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static string BuildBlockingProcessMessage(
        string executablePath,
        IReadOnlyList<int> blockingPids) =>
        string.Join(
            Environment.NewLine,
            "[update] Update blocked by running wpa-mcp instances.",
            $"Executable: {executablePath}",
            $"Blocking PIDs: {string.Join(", ", blockingPids)}",
            "Updating requires these MCP server instances to exit. Their in-memory " +
            "trace and symbol state will be lost.",
            "Close the associated MCP clients and retry:",
            "  wpa-mcp.exe update",
            "Or explicitly terminate only instances running this exact executable:",
            $"  wpa-mcp.exe update {StopRunningArgument}",
            "No process was terminated and no installed files were changed.");

    private static string BuildUninspectableProcessMessage(
        string executablePath,
        IReadOnlyList<int> processIds) =>
        string.Join(
            Environment.NewLine,
            "[update] Update blocked because running wpa-mcp process paths could not " +
            "be inspected safely.",
            $"Executable: {executablePath}",
            $"Uninspectable PIDs: {string.Join(", ", processIds)}",
            "Close those processes or rerun from a context that can inspect them.",
            "No process was terminated and no installed files were changed.");

    private static string CurrentVersion() =>
        typeof(SelfUpdateCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(SelfUpdateCommand).Assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("The installed version is unavailable.");

    private static HttpClient CreateHttpClient(string currentVersion)
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("wpa-mcp/" + currentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static async Task<ReleaseBundle> GetLatestReleaseAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            LatestReleaseApi,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var root = document.RootElement;

        if (root.GetProperty("draft").GetBoolean() ||
            root.GetProperty("prerelease").GetBoolean())
        {
            throw new InvalidDataException("GitHub returned a non-stable latest release.");
        }

        var tagName = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tagName) || !tagName.StartsWith('v'))
            throw new InvalidDataException("The latest release has an invalid tag.");
        var releaseVersion = tagName[1..];
        _ = ParseVersion(releaseVersion, "release tag");

        var assets = root.GetProperty("assets");
        return new ReleaseBundle(
            tagName,
            releaseVersion,
            RequireAsset(assets, BundleAssetName),
            RequireAsset(assets, EvidenceAssetName));
    }

    private static ReleaseAsset RequireAsset(JsonElement assets, string name)
    {
        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(
                    asset.GetProperty("name").GetString(), name, StringComparison.Ordinal))
            {
                continue;
            }

            var urlText = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Release asset '{name}' has an invalid URL.");
            }

            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var digestElement) &&
                         digestElement.ValueKind == JsonValueKind.String
                ? digestElement.GetString()
                : null;
            return new ReleaseAsset(url, size, digest);
        }

        throw new InvalidDataException($"The latest release is missing '{name}'.");
    }

    private static async Task DownloadAssetAsync(
        HttpClient client,
        ReleaseAsset asset,
        string destination,
        long maximumBytes)
    {
        if (asset.Size <= 0 || asset.Size > maximumBytes)
        {
            throw new InvalidDataException(
                $"Release asset size {asset.Size} is outside the accepted range.");
        }

        using var response = await client.GetAsync(
            asset.Url,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != asset.Size)
        {
            throw new InvalidDataException(
                $"Release asset length changed from {asset.Size} to {contentLength} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > maximumBytes || total > asset.Size)
                throw new InvalidDataException("Release asset exceeded its declared size.");
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }

        if (total != asset.Size)
        {
            throw new InvalidDataException(
                $"Release asset was truncated: expected {asset.Size}, received {total} bytes.");
        }
    }

    private static async Task<string> ReadExpectedBundleHashAsync(
        string evidencePath,
        ReleaseBundle release)
    {
        var bytes = await File.ReadAllBytesAsync(evidencePath).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetString() != "release-evidence.v1" ||
            root.GetProperty("version").GetString() != release.Version ||
            root.GetProperty("ref").GetString() != $"refs/tags/{release.TagName}")
        {
            throw new InvalidDataException(
                "Release evidence does not match the selected immutable release.");
        }

        var hash = root.GetProperty("assets").GetProperty("zipSha256").GetString();
        if (!IsSha256(hash))
            throw new InvalidDataException("Release evidence contains an invalid ZIP SHA-256.");
        return hash!.ToLowerInvariant();
    }

    private static string? ParseAssetDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(digest[prefix.Length..]))
        {
            throw new InvalidDataException("GitHub returned an invalid asset digest.");
        }

        return digest[prefix.Length..].ToLowerInvariant();
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }
        return true;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static Version ParseVersion(string value, string source)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith('v'))
            normalized = normalized[1..];
        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
            normalized = normalized[..suffixIndex];
        if (!Version.TryParse(normalized, out var version) || version.Build < 0)
            throw new InvalidDataException($"The {source} '{value}' is not semantic x.y.z.");
        return version;
    }

    private static void RequireBundleFile(string path, string relativePath)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"Release bundle is missing {relativePath}.");
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
            ?? throw new InvalidOperationException("Could not start the staged executable.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidDataException("The staged executable version check timed out.");
        }

        var output = (await standardOutput.ConfigureAwait(false)).Trim();
        var error = (await standardError.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"The staged executable version check failed with exit {process.ExitCode}: " +
                error);
        }
        return output;
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
            // The failed command has already reported its primary error.
        }
    }

    private sealed record ReleaseAsset(Uri Url, long Size, string? Digest);

    private sealed record ReleaseBundle(
        string TagName,
        string Version,
        ReleaseAsset Bundle,
        ReleaseAsset Evidence);

    private sealed record RunningInstanceInspection(
        IReadOnlyList<int> BlockingPids,
        IReadOnlyList<int> UninspectablePids);

}
