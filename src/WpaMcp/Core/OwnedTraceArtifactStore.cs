using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace WpaMcp.Core;

internal sealed record OwnedTraceArtifact(
    string TracePath,
    string ArtifactKey,
    string SourceSha256,
    long SourceLength,
    string EtlxSha256,
    long EtlxLength);

internal sealed record OwnedTraceArtifactManifest(
    int SchemaVersion,
    string ArtifactKey,
    string SourceSha256,
    long SourceLength,
    string EtlxSha256,
    long EtlxLength,
    string TraceEventVersion,
    string ConversionOptionsVersion);

/// <summary>
/// Copies an already validated handle snapshot into a private store and publishes
/// only a content-addressed ETLX object. No conversion path points at caller-owned
/// directories.
/// </summary>
internal sealed class OwnedTraceArtifactStore : IDisposable
{
    internal const string ConversionOptionsVersion = "wpamcp-owned-etlx-v1";
    internal const string TemporarySpaceAssurance =
        "release_blocked:retained_quota_only;single_materialization_checkpoint_budget;opaque_converter_transient_peak_unproven";
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly Lazy<TrustedTraceArtifactRoot> _trustedRoot;
    private readonly string _configuredRoot;
    private readonly long _maxInputBytes;
    private readonly long _maxRetainedBytes;
    private readonly long _maxTemporaryCheckpointBytes;
    private readonly int _maxObjects;
    private readonly TimeSpan _retentionTtl;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string, TraceLog> _openOrConvert;
    private readonly Func<CancellationToken, ValueTask>? _beforeSnapshot;
    private readonly object _pinGate = new();
    private readonly Dictionary<string, int> _activePins = new(StringComparer.Ordinal);
    private long _snapshotCopyCount;
    private long _conversionCount;

    internal OwnedTraceArtifactStore(
        string configuredRoot,
        long maxInputBytes,
        long maxStoreBytes,
        int maxObjects,
        Func<string, TraceLog>? openOrConvert = null,
        Func<CancellationToken, ValueTask>? beforeSnapshot = null,
        Func<string, TrustedTraceArtifactRoot>? createTrustedRoot = null,
        TimeSpan? retentionTtl = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The owned artifact store requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        if (maxInputBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxInputBytes));
        if (maxStoreBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStoreBytes));
        if (maxObjects <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxObjects));
        var effectiveRetentionTtl = retentionTtl ??
            WpaMcp.TraceRuntimeOptions.DefaultArtifactRetentionTtl;
        if (effectiveRetentionTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionTtl));
        _configuredRoot = configuredRoot;
        _trustedRoot = new Lazy<TrustedTraceArtifactRoot>(
            () =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException();
                return createTrustedRoot is null
                    ? new TrustedTraceArtifactRoot(_configuredRoot)
                    : createTrustedRoot(_configuredRoot);
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
        _maxInputBytes = maxInputBytes;
        // This option is a retained-object quota. Until a separately configured
        // worker/filesystem quota exists, reuse its numeric value only as a
        // fail-closed checkpoint budget for the one serialized temp operation.
        // It is not a combined or hard physical-root byte guarantee.
        _maxRetainedBytes = maxStoreBytes;
        _maxTemporaryCheckpointBytes = maxStoreBytes;
        _maxObjects = maxObjects;
        _retentionTtl = effectiveRetentionTtl;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _openOrConvert = openOrConvert ?? (static path => TraceLog.OpenOrConvert(path));
        _beforeSnapshot = beforeSnapshot;
    }

    internal long SnapshotCopyCount => Interlocked.Read(ref _snapshotCopyCount);
    internal long ConversionCount => Interlocked.Read(ref _conversionCount);
    internal bool ArtifactRootCreated =>
        OperatingSystem.IsWindows() && _trustedRoot.IsValueCreated;

    private string Root
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
            try
            {
                return _trustedRoot.Value.Path;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new TraceAccessException(
                    "trace_access_denied",
                    "The configured trace artifact root could not be initialized securely.");
            }
        }
    }

    internal async Task<OwnedTraceArtifact> GetOrCreateAsync(
        ValidatedTraceSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        // Source policy validation has completed before the first write below.
        var root = Root;
        Directory.CreateDirectory(root);
        var locksRoot = Path.Combine(root, "locks");
        Directory.CreateDirectory(locksRoot);
        var tmpRoot = Path.Combine(root, "tmp");
        Directory.CreateDirectory(tmpRoot);
        await using var materializationLock = await AcquireObjectLockAsync(
            Path.Combine(locksRoot, "materialization.lock"),
            cancellationToken).ConfigureAwait(false);
        try
        {
            ScavengeTemporaryOperations(tmpRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TraceAccessException(
                "trace_materialization_failed",
                "Stale temporary trace materialization state could not be removed safely.");
        }
        var operationRoot = Path.Combine(tmpRoot, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operationRoot);

        try
        {
            if (_beforeSnapshot is not null)
                await _beforeSnapshot(cancellationToken).ConfigureAwait(false);
            var snapshotPath = Path.Combine(operationRoot, "input" + source.Extension);
            string sourceHash;
            long sourceLength;
            await using (var snapshot = new FileStream(
                             snapshotPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                sourceLength = await source.CopyToAsync(
                    snapshot,
                    hash,
                    _maxInputBytes,
                    cancellationToken).ConfigureAwait(false);
                await snapshot.FlushAsync(cancellationToken).ConfigureAwait(false);
                snapshot.Flush(flushToDisk: true);
                sourceHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            Interlocked.Increment(ref _snapshotCopyCount);
            EnforceTemporaryQuota(operationRoot);

            var finalIdentity = source.CaptureCurrentIdentity();
            if (sourceLength != source.Identity.Length ||
                finalIdentity != source.Identity)
            {
                throw new TraceAccessException(
                    "trace_source_changed",
                    "The opened trace changed while its immutable snapshot was created.");
            }

            var traceEventVersion = typeof(TraceLog).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            var artifactKey = ComputeArtifactKey(
                sourceHash,
                sourceLength,
                traceEventVersion,
                ConversionOptionsVersion);
            var objectParent = Path.Combine(root, "objects", artifactKey[..2]);
            var objectRoot = Path.Combine(objectParent, artifactKey);
            var tracePath = Path.Combine(objectRoot, "trace.etlx");
            var manifestPath = Path.Combine(objectRoot, "manifest.json");

            await using var objectLock = await AcquireObjectLockAsync(
                Path.Combine(locksRoot, artifactKey + ".lock"),
                cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(objectRoot) &&
                IsRetentionExpired(objectRoot) &&
                !IsPinned(artifactKey))
            {
                // TTL applies only to independently retained, unpinned objects.
                // A live trace handle owns a pin and therefore cannot be invalidated
                // by a background or subsequent materialization request.
                Directory.Delete(objectRoot, recursive: true);
            }

            var existing = await TryReadValidAsync(
                manifestPath,
                tracePath,
                artifactKey,
                sourceHash,
                sourceLength,
                traceEventVersion,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                TouchObject(objectRoot);
                await EnforceRetentionAsync(
                    artifactKey,
                    objectRoot,
                    cancellationToken).ConfigureAwait(false);
                return existing;
            }

            if (Directory.Exists(objectRoot))
            {
                // This object is wholly server-owned and the exclusive object lock
                // proves no publisher can currently use it.
                Directory.Delete(objectRoot, recursive: true);
            }

            var stagingObject = Path.Combine(operationRoot, "object");
            Directory.CreateDirectory(stagingObject);
            var stagingTrace = Path.Combine(stagingObject, "trace.etlx");
            if (source.Extension.Equals(".etl", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _conversionCount);
                using (var converted = _openOrConvert(snapshotPath))
                {
                    _ = converted.SessionDuration;
                }
                var derived = Path.ChangeExtension(snapshotPath, ".etlx");
                if (!File.Exists(derived))
                {
                    throw new TraceAccessException(
                        "trace_conversion_failed",
                        "Trace conversion did not produce a validated ETLX artifact.");
                }
                File.Move(derived, stagingTrace);
            }
            else
            {
                File.Move(snapshotPath, stagingTrace);
                using var validated = _openOrConvert(stagingTrace);
                _ = validated.SessionDuration;
            }
            EnforceTemporaryQuota(operationRoot);

            await FlushFileAsync(stagingTrace, cancellationToken).ConfigureAwait(false);
            var etlxLength = new FileInfo(stagingTrace).Length;
            var etlxHash = await ComputeFileHashAsync(
                stagingTrace,
                cancellationToken).ConfigureAwait(false);
            var manifest = new OwnedTraceArtifactManifest(
                SchemaVersion: 1,
                artifactKey,
                sourceHash,
                sourceLength,
                etlxHash,
                etlxLength,
                traceEventVersion,
                ConversionOptionsVersion);
            var stagingManifest = Path.Combine(stagingObject, "manifest.json");
            await WriteManifestAsync(
                stagingManifest,
                manifest,
                cancellationToken).ConfigureAwait(false);
            EnforceTemporaryQuota(operationRoot);

            Directory.CreateDirectory(objectParent);
            Directory.Move(stagingObject, objectRoot);
            TouchObject(objectRoot);
            await EnforceRetentionAsync(
                artifactKey,
                objectRoot,
                cancellationToken).ConfigureAwait(false);
            return new OwnedTraceArtifact(
                tracePath,
                artifactKey,
                sourceHash,
                sourceLength,
                etlxHash,
                etlxLength);
        }
        catch (TraceAccessException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new TraceAccessException(
                "trace_materialization_failed",
                "The trace could not be published in the owned artifact store.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(operationRoot))
                    Directory.Delete(operationRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed best-effort cleanup does not change the publication result.
                // The next serialized materialization retries stale cleanup before
                // it creates or writes a new operation directory.
            }
        }
    }

    internal async Task<OwnedTraceArtifactLease> AcquireLeaseAsync(
        OwnedTraceArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Artifact pinning requires Windows.");
        ArgumentNullException.ThrowIfNull(artifact);
        var lockPath = Path.Combine(Root, "locks", artifact.ArtifactKey + ".lock");
        await using var objectLock = await AcquireObjectLockAsync(
            lockPath,
            cancellationToken).ConfigureAwait(false);
        var objectRoot = Path.GetDirectoryName(artifact.TracePath)!;
        if (Directory.Exists(objectRoot) &&
            IsRetentionExpired(objectRoot) &&
            !IsPinned(artifact.ArtifactKey))
        {
            Directory.Delete(objectRoot, recursive: true);
            throw new TraceAccessException(
                "trace_artifact_expired",
                "The retained trace artifact exceeded its idle retention TTL.");
        }
        OwnedTraceArtifactPin pin;
        try
        {
            pin = _trustedRoot.Value.Pin(artifact.TracePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TraceAccessException(
                "trace_artifact_expired",
                "The retained trace artifact is no longer available.");
        }
        try
        {
            if (pin.Identity.Length != artifact.EtlxLength)
            {
                throw new TraceAccessException(
                    "trace_artifact_changed",
                    "The owned trace artifact length no longer matches its manifest.");
            }
            // Validate through the exact held, read-only, non-delete-share handle.
            // Reopening FinalPath here would reintroduce a path-replacement TOCTOU.
            var actualHash = await pin.ComputeSha256Async(
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, artifact.EtlxSha256, StringComparison.Ordinal))
            {
                throw new TraceAccessException(
                    "trace_artifact_changed",
                    "The owned trace artifact content no longer matches its manifest.");
            }
            pin.VerifyUnchanged();
            lock (_pinGate)
            {
                _activePins.TryGetValue(artifact.ArtifactKey, out var count);
                _activePins[artifact.ArtifactKey] = checked(count + 1);
            }
            TouchObject(Path.GetDirectoryName(pin.FinalPath)!);
            return new OwnedTraceArtifactLease(
                artifact,
                pin,
                () => ReleasePin(artifact.ArtifactKey));
        }
        catch
        {
            pin.Dispose();
            throw;
        }
    }

    private async Task EnforceRetentionAsync(
        string currentKey,
        string currentObjectRoot,
        CancellationToken cancellationToken)
    {
        var objectsRoot = Path.Combine(Root, "objects");
        if (!Directory.Exists(objectsRoot))
            return;

        var objects = Directory.EnumerateDirectories(objectsRoot)
            .SelectMany(prefix => Directory.EnumerateDirectories(prefix))
            .Select(path =>
            {
                return new RetainedObject(
                    Path.GetFileName(path),
                    path,
                    GetDirectoryBytes(path),
                    Directory.GetLastWriteTimeUtc(path));
            })
            .ToList();
        long totalBytes = 0;
        foreach (var item in objects)
            totalBytes = checked(totalBytes + item.Bytes);
        var now = _utcNow();

        foreach (var candidate in objects
                     .Where(item => !string.Equals(
                         item.Key,
                         currentKey,
                         StringComparison.Ordinal))
                     .OrderBy(item => item.LastAccessUtc)
                     .ThenBy(item => item.Key, StringComparer.Ordinal)
                     .ToArray())
        {
            var expired = IsRetentionExpired(candidate.LastAccessUtc, now);
            if (!expired &&
                totalBytes <= _maxRetainedBytes && objects.Count <= _maxObjects)
                break;
            if (IsPinned(candidate.Key))
                continue;

            await using var objectLock = await AcquireObjectLockAsync(
                Path.Combine(Root, "locks", candidate.Key + ".lock"),
                cancellationToken).ConfigureAwait(false);
            if (IsPinned(candidate.Key) || !Directory.Exists(candidate.Path))
                continue;
            Directory.Delete(candidate.Path, recursive: true);
            totalBytes = checked(totalBytes - candidate.Bytes);
            objects.Remove(candidate);
        }

        if (totalBytes > _maxRetainedBytes || objects.Count > _maxObjects)
        {
            if (!IsPinned(currentKey) && Directory.Exists(currentObjectRoot))
                Directory.Delete(currentObjectRoot, recursive: true);
            throw new TraceReferenceException(
                "budget_exceeded",
                "The owned trace artifact store quota is exhausted.",
                detailCode: "trace_artifact_store_quota_exceeded");
        }
    }

    private bool IsPinned(string artifactKey)
    {
        lock (_pinGate)
            return _activePins.TryGetValue(artifactKey, out var count) && count != 0;
    }

    private void ReleasePin(string artifactKey)
    {
        lock (_pinGate)
        {
            if (!_activePins.TryGetValue(artifactKey, out var count) || count <= 0)
                throw new InvalidOperationException("Artifact pin count underflow.");
            if (count == 1)
                _activePins.Remove(artifactKey);
            else
                _activePins[artifactKey] = count - 1;
        }
    }

    private void TouchObject(string objectRoot) =>
        Directory.SetLastWriteTimeUtc(objectRoot, _utcNow().UtcDateTime);

    private bool IsRetentionExpired(string objectRoot) =>
        IsRetentionExpired(
            Directory.GetLastWriteTimeUtc(objectRoot),
            _utcNow());

    private bool IsRetentionExpired(
        DateTime lastAccessUtc,
        DateTimeOffset now) =>
        now.UtcDateTime - lastAccessUtc > _retentionTtl;

    private void EnforceTemporaryQuota(string operationRoot)
    {
        if (GetDirectoryBytes(operationRoot) <= _maxTemporaryCheckpointBytes)
            return;

        throw new TraceReferenceException(
            "budget_exceeded",
            "The owned trace materialization temporary-space quota was exceeded.",
            detailCode: "trace_artifact_temporary_quota_exceeded");
    }

    private static long GetDirectoryBytes(string directory)
    {
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(
                     directory,
                     "*",
                     SearchOption.AllDirectories))
        {
            bytes = checked(bytes + new FileInfo(file).Length);
        }
        return bytes;
    }

    private static void ScavengeTemporaryOperations(string tmpRoot)
    {
        // The cross-process materialization lock is held by the caller. Therefore
        // no directory below tmp can belong to a live conforming operation.
        foreach (var directory in Directory.EnumerateDirectories(tmpRoot))
            Directory.Delete(directory, recursive: true);
        foreach (var file in Directory.EnumerateFiles(tmpRoot))
            File.Delete(file);
    }

    private static async Task<OwnedTraceArtifact?> TryReadValidAsync(
        string manifestPath,
        string tracePath,
        string expectedKey,
        string expectedSourceHash,
        long expectedSourceLength,
        string expectedTraceEventVersion,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath) || !File.Exists(tracePath))
            return null;

        OwnedTraceArtifactManifest? manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<OwnedTraceArtifactManifest>(
                stream,
                ManifestJson,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (manifest is null ||
            manifest.SchemaVersion != 1 ||
            !string.Equals(manifest.ArtifactKey, expectedKey, StringComparison.Ordinal) ||
            !string.Equals(manifest.SourceSha256, expectedSourceHash, StringComparison.Ordinal) ||
            manifest.SourceLength != expectedSourceLength ||
            !string.Equals(
                manifest.TraceEventVersion,
                expectedTraceEventVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.ConversionOptionsVersion,
                ConversionOptionsVersion,
                StringComparison.Ordinal))
        {
            return null;
        }

        var info = new FileInfo(tracePath);
        info.Refresh();
        if (!info.Exists || info.Length != manifest.EtlxLength)
            return null;
        var actualHash = await ComputeFileHashAsync(
            tracePath,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, manifest.EtlxSha256, StringComparison.Ordinal))
            return null;

        return new OwnedTraceArtifact(
            tracePath,
            expectedKey,
            expectedSourceHash,
            expectedSourceLength,
            manifest.EtlxSha256,
            manifest.EtlxLength);
    }

    private static async Task<FileStream> AcquireObjectLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (attempt < 600)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new TraceReferenceException(
                    "budget_exceeded",
                    "The owned trace artifact operation contention budget was exhausted.",
                    detailCode: "trace_artifact_lock_budget_exceeded");
            }
        }
    }

    private static string ComputeArtifactKey(
        string sourceHash,
        long sourceLength,
        string traceEventVersion,
        string conversionOptionsVersion)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{sourceHash}:{sourceLength}:{traceEventVersion}:{conversionOptionsVersion}");
        return Convert.ToHexStringLower(SHA256.HashData(material));
    }

    private static async Task<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task FlushFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteManifestAsync(
        string path,
        OwnedTraceArtifactManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(
            stream,
            manifest,
            ManifestJson,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows() && _trustedRoot.IsValueCreated)
            _trustedRoot.Value.Dispose();
    }

    private sealed record RetainedObject(
        string Key,
        string Path,
        long Bytes,
        DateTime LastAccessUtc);
}

internal sealed class OwnedTraceArtifactLease : IDisposable
{
    private LeaseState? _state;

    internal OwnedTraceArtifactLease(
        OwnedTraceArtifact artifact,
        OwnedTraceArtifactPin pin,
        Action release)
    {
        Artifact = artifact;
        _state = new LeaseState(pin, release);
    }

    internal OwnedTraceArtifact Artifact { get; }
    internal string TracePath => GetState().Pin.FinalPath;

    internal void VerifyUnchanged() => GetState().Pin.VerifyUnchanged();

    public void Dispose()
    {
        var state = Interlocked.Exchange(ref _state, null);
        if (state is null)
            return;
        try
        {
            state.Pin.Dispose();
        }
        finally
        {
            state.Release();
        }
    }

    private LeaseState GetState() =>
        Volatile.Read(ref _state)
        ?? throw new ObjectDisposedException(nameof(OwnedTraceArtifactLease));

    private sealed record LeaseState(OwnedTraceArtifactPin Pin, Action Release);
}
