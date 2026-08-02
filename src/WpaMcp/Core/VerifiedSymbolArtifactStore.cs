using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Win32.SafeHandles;

namespace WpaMcp.Core;

internal interface ILocalPdbIdentityVerifier
{
    ValueTask<string?> VerifyFormatAsync(
        string immutableSnapshotPath,
        TraceModulePdbIdentity expectedIdentity,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exact direct-file verifier. Empty SymbolPath plus OpenSymbolFile confines TraceEvent
/// to the supplied snapshot and cannot initiate a server lookup.
/// </summary>
internal sealed class TraceEventLocalPdbIdentityVerifier : ILocalPdbIdentityVerifier
{
    public ValueTask<string?> VerifyFormatAsync(
        string immutableSnapshotPath,
        TraceModulePdbIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new SymbolReader(TextWriter.Null, string.Empty);
        var module = reader.OpenSymbolFile(immutableSnapshotPath);
        if (module is null)
            return ValueTask.FromResult<string?>(null);

        var age = module is NativeSymbolModule native ? native.PdbAge : 1;
        var format = module is NativeSymbolModule ? "windows-pdb" : "portable-pdb";
        return ValueTask.FromResult(
            module.PdbGuid == expectedIdentity.PdbSignature && age == expectedIdentity.PdbAge
                ? format
                : null);
    }
}

internal interface IReadableVerifiedSymbolArtifactPin : IVerifiedSymbolArtifactPin
{
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}

internal interface ITrustedSymbolArtifactRoot : IDisposable
{
    string Path { get; }

    bool ContainsFinalPath(string candidate);
}

internal sealed class ProductionTrustedSymbolArtifactRoot : ITrustedSymbolArtifactRoot
{
    private readonly TrustedTraceArtifactRoot _inner;

    internal ProductionTrustedSymbolArtifactRoot(string configuredPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The private verified symbol store requires Windows.");
        _inner = new TrustedTraceArtifactRoot(configuredPath);
    }

    public string Path
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The private verified symbol store requires Windows.");
            return _inner.Path;
        }
    }

    public bool ContainsFinalPath(string candidate)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The private verified symbol store requires Windows.");
        return _inner.ContainsFinalPath(candidate);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The private verified symbol store requires Windows.");
        _inner.Dispose();
    }
}

/// <summary>
/// Copies an approved local candidate into a private content-addressed store, verifies
/// the copied object, and returns a read pin. It has no network or environment access.
/// </summary>
internal sealed class LocalVerifiedSymbolArtifactStore : IVerifiedSymbolArtifactStore, IDisposable
{
    private const int DefaultMaxArtifactCount = 4_096;
    private const long DefaultMaxStoreBytes = 16L * 1024 * 1024 * 1024;

    private readonly object _storeGate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAccessUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ITrustedSymbolArtifactRoot _trustedRoot;
    private readonly string _storeRoot;
    private readonly long _maxArtifactBytes;
    private readonly long _maxStoreBytes;
    private readonly int _maxArtifactCount;
    private readonly ILocalPdbIdentityVerifier _verifier;

    public LocalVerifiedSymbolArtifactStore(
        string storeRoot,
        long maxArtifactBytes,
        ILocalPdbIdentityVerifier verifier,
        long maxStoreBytes = DefaultMaxStoreBytes,
        int maxArtifactCount = DefaultMaxArtifactCount,
        ITrustedSymbolArtifactRoot? trustedRoot = null)
    {
        if (string.IsNullOrWhiteSpace(storeRoot) || !Path.IsPathFullyQualified(storeRoot))
            throw new ArgumentException("A fully qualified private symbol store root is required.", nameof(storeRoot));
        if (maxArtifactBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxArtifactBytes));
        if (maxStoreBytes < maxArtifactBytes)
            throw new ArgumentOutOfRangeException(nameof(maxStoreBytes));
        if (maxArtifactCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxArtifactCount));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));

        var normalizedStoreRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeRoot));
        if (normalizedStoreRoot.StartsWith("\\\\", StringComparison.Ordinal)
            || normalizedStoreRoot.StartsWith("\\??\\", StringComparison.Ordinal)
            || (OperatingSystem.IsWindows()
                && normalizedStoreRoot.AsSpan(Math.Min(2, normalizedStoreRoot.Length)).Contains(':')))
        {
            throw new ArgumentException(
                "The private symbol store must be a local non-device path without an alternate stream.",
                nameof(storeRoot));
        }
        // Reuse the server-owned root boundary: non-reparse ancestry, protected ACL
        // and owner, retained no-delete-share directory handle, and final-path checks.
        _trustedRoot = trustedRoot ?? new ProductionTrustedSymbolArtifactRoot(normalizedStoreRoot);
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(_trustedRoot.Path),
                normalizedStoreRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            _trustedRoot.Dispose();
            throw new ArgumentException(
                "The trusted symbol-store root does not match the configured root.",
                nameof(trustedRoot));
        }
        _storeRoot = _trustedRoot.Path;
        _maxArtifactBytes = maxArtifactBytes;
        _maxStoreBytes = maxStoreBytes;
        _maxArtifactCount = maxArtifactCount;
    }

    public async ValueTask<IVerifiedSymbolArtifactPin?> TryVerifyAndPinLocalAsync(
        ApprovedLocalSymbolCandidate candidate,
        TraceModulePdbIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        var sourcePath = candidate.GetValidatedPath();

        FileStream source;
        try
        {
            if (ContainsReparsePoint(candidate.ApprovedRoot, sourcePath))
                return null;
            var attributes = File.GetAttributes(sourcePath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return null;
            source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (ContainsReparsePoint(candidate.ApprovedRoot, sourcePath)
                || !OpenedHandleIsWithinApprovedRoot(
                    source.SafeFileHandle,
                    candidate.ApprovedRoot))
            {
                source.Dispose();
                return null;
            }
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException
                or IOException)
        {
            return null;
        }

        await using (source.ConfigureAwait(false))
        {
            if (source.Length <= 0 || source.Length > _maxArtifactBytes)
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            var tempRoot = Path.Combine(_storeRoot, "tmp");
            EnsureTrustedDirectory(tempRoot);
            var tempPath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.pdb");
            try
            {
                string hash;
                long length;
                await using (var destination = new FileStream(
                                 tempPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    EnsureTrustedRegularFile(destination);
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024];
                    length = 0;
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        length = checked(length + read);
                        if (length > _maxArtifactBytes)
                            return null;
                        hasher.AppendData(buffer, 0, read);
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                }

                var format = await _verifier.VerifyFormatAsync(
                    tempPath,
                    expectedIdentity,
                    cancellationToken).ConfigureAwait(false);
                if (format is null)
                    return null;

                var objectDirectory = Path.Combine(_storeRoot, "objects", hash[..2]);
                EnsureTrustedDirectory(Path.Combine(_storeRoot, "objects"));
                EnsureTrustedDirectory(objectDirectory);
                var objectPath = Path.Combine(objectDirectory, hash + ".pdb");
                lock (_storeGate)
                {
                    if (!File.Exists(objectPath))
                        EnsurePublicationCapacity(length, objectPath);
                    try
                    {
                        File.Move(tempPath, objectPath);
                    }
                    catch (IOException) when (File.Exists(objectPath))
                    {
                        // A content-address collision is reusable only after independently
                        // re-verifying the already-published object below.
                    }
                    _lastAccessUtc[objectPath] = DateTimeOffset.UtcNow;
                }

                var pinStream = new FileStream(
                    objectPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
                EnsureTrustedRegularFile(pinStream);
                if (pinStream.Length != length)
                {
                    await pinStream.DisposeAsync().ConfigureAwait(false);
                    throw new SymbolContextException(
                        SymbolContextFailure.ArtifactVerificationFailed,
                        "The published symbol artifact length changed before it could be pinned.");
                }

                var publishedHash = await ComputeHashAsync(
                    pinStream.SafeFileHandle,
                    length,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(publishedHash, hash, StringComparison.Ordinal))
                {
                    await pinStream.DisposeAsync().ConfigureAwait(false);
                    throw new SymbolContextException(
                        SymbolContextFailure.ArtifactVerificationFailed,
                        "The published symbol artifact content identity changed before it could be pinned.");
                }

                var publishedFormat = await _verifier.VerifyFormatAsync(
                    objectPath,
                    expectedIdentity,
                    cancellationToken).ConfigureAwait(false);
                if (publishedFormat is null)
                {
                    await pinStream.DisposeAsync().ConfigureAwait(false);
                    throw new SymbolContextException(
                        SymbolContextFailure.ArtifactVerificationFailed,
                        "The published symbol artifact no longer matches its PDB identity.");
                }

                return new FileArtifactPin(
                    pinStream,
                    new VerifiedSymbolArtifactIdentity(
                        hash,
                        length,
                        expectedIdentity.PdbName,
                        expectedIdentity.PdbSignature,
                        expectedIdentity.PdbAge,
                        publishedFormat));
            }
            finally
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // The private orphan is not published or referenced. Store-wide
                    // reconciliation may remove it later.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public void Dispose()
        => _trustedRoot.Dispose();

    private void EnsureTrustedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw StoreBoundaryFailure();
        using var handle = WindowsTraceFile.OpenDirectory(path);
        if (!ContainsTrustedFinalPath(
                NormalizeFinalPath(WindowsTraceFile.GetFinalPath(handle))))
            throw StoreBoundaryFailure();
    }

    private void EnsureTrustedRegularFile(FileStream stream)
    {
        var finalPath = NormalizeFinalPath(
            WindowsTraceFile.GetFinalPath(stream.SafeFileHandle));
        var identity = WindowsTraceFile.GetIdentity(stream.SafeFileHandle);
        if (!ContainsTrustedFinalPath(finalPath) ||
            (identity.FileAttributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            stream.Dispose();
            throw StoreBoundaryFailure();
        }
    }

    private void EnsurePublicationCapacity(long incomingLength, string targetPath)
    {
        var objectsRoot = Path.Combine(_storeRoot, "objects");
        var objects = EnumerateTrustedObjects(objectsRoot)
            .Where(path => !string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => new StoreObject(
                path,
                new FileInfo(path).Length,
                _lastAccessUtc.GetValueOrDefault(
                    path,
                    new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero))))
            .OrderBy(static item => item.LastAccessUtc)
            .ThenBy(static item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        long totalBytes = objects.Sum(static item => item.Length);
        var totalCount = objects.Count;
        foreach (var candidate in objects)
        {
            if (totalBytes + incomingLength <= _maxStoreBytes &&
                totalCount + 1 <= _maxArtifactCount)
            {
                break;
            }

            try
            {
                // Active pins use FileShare.Read without delete sharing, so an in-use
                // immutable context is never evicted by LRU reconciliation.
                File.Delete(candidate.Path);
                totalBytes -= candidate.Length;
                totalCount--;
                _lastAccessUtc.Remove(candidate.Path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A pinned/busy object remains charged to the quota.
            }
        }

        if (totalBytes + incomingLength > _maxStoreBytes ||
            totalCount + 1 > _maxArtifactCount)
        {
            throw new SymbolContextException(
                SymbolContextFailure.QuotaExceeded,
                "The private verified-symbol store quota is exhausted by pinned or retained artifacts.");
        }
    }

    private IReadOnlyList<string> EnumerateTrustedObjects(string objectsRoot)
    {
        if (!Directory.Exists(objectsRoot))
            return [];
        EnsureTrustedDirectory(objectsRoot);
        var files = new List<string>();
        foreach (var shard in Directory.EnumerateDirectories(objectsRoot))
        {
            if ((File.GetAttributes(shard) & FileAttributes.ReparsePoint) != 0)
                throw StoreBoundaryFailure();
            EnsureTrustedDirectory(shard);
            foreach (var path in Directory.EnumerateFiles(shard, "*.pdb", SearchOption.TopDirectoryOnly))
            {
                using var handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.RandomAccess);
                var identity = WindowsTraceFile.GetIdentity(handle);
                if (!ContainsTrustedFinalPath(
                        NormalizeFinalPath(WindowsTraceFile.GetFinalPath(handle))) ||
                    (identity.FileAttributes &
                     (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw StoreBoundaryFailure();
                }
                files.Add(path);
            }
        }
        return files;
    }

    private static SymbolContextException StoreBoundaryFailure() => new(
        SymbolContextFailure.ArtifactVerificationFailed,
        "The private verified-symbol store boundary changed or contains a reparse object.");

    private bool ContainsTrustedFinalPath(string path)
        => _trustedRoot.ContainsFinalPath(path);

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            return "\\\\" + path[8..];
        return path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path[4..]
            : path;
    }

    private sealed record StoreObject(
        string Path,
        long Length,
        DateTimeOffset LastAccessUtc);

    private static bool ContainsReparsePoint(string approvedRoot, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedRoot));
        for (var current = new DirectoryInfo(Path.GetDirectoryName(candidatePath)!);
             current is not null;
             current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(current.FullName),
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // GetValidatedPath already proved lexical containment. Failing to encounter the
        // approved root here means filesystem ancestry changed or was inaccessible.
        return true;
    }

    private static bool OpenedHandleIsWithinApprovedRoot(
        SafeFileHandle sourceHandle,
        string approvedRoot)
    {
        // wpa-mcp is a Windows ETW server. On another OS, do not weaken authorization
        // to lexical containment; local preparation remains unavailable fail-closed.
        if (!OperatingSystem.IsWindows())
            return false;

        using var rootHandle = NativeMethods.CreateFileW(
            approvedRoot,
            desiredAccess: 0,
            FileShare.ReadWrite | FileShare.Delete,
            securityAttributes: IntPtr.Zero,
            FileMode.Open,
            NativeMethods.FileFlagBackupSemantics,
            templateFile: IntPtr.Zero);
        if (rootHandle.IsInvalid)
            return false;

        var sourceFinalPath = NativeMethods.TryGetFinalPath(sourceHandle);
        var rootFinalPath = NativeMethods.TryGetFinalPath(rootHandle);
        if (sourceFinalPath is null || rootFinalPath is null)
            return false;

        rootFinalPath = Path.TrimEndingDirectorySeparator(rootFinalPath);
        return sourceFinalPath.StartsWith(
            rootFinalPath + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<string> ComputeHashAsync(
        SafeFileHandle handle,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < expectedLength)
        {
            var requested = (int)Math.Min(buffer.Length, expectedLength - offset);
            var read = await RandomAccess.ReadAsync(
                handle,
                buffer.AsMemory(0, requested),
                offset,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
            offset = checked(offset + read);
        }
        if (offset != expectedLength)
            throw new EndOfStreamException("The pinned symbol artifact ended before its verified length.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class FileArtifactPin : IReadableVerifiedSymbolArtifactPin
    {
        private FileStream? _pin;

        public FileArtifactPin(
            FileStream pin,
            VerifiedSymbolArtifactIdentity identity)
        {
            _pin = pin;
            Identity = identity;
        }

        public VerifiedSymbolArtifactIdentity Identity { get; }

        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pin = Volatile.Read(ref _pin);
            try
            {
                // The private object was fully hash- and PDB-verified before this pin
                // was returned. Its retained handle denies write and delete sharing,
                // so rehashing a multi-GB artifact on every query adds no integrity
                // signal. Liveness and exact handle length are sufficient here.
                return ValueTask.FromResult(
                    pin is not null &&
                    pin.CanRead &&
                    !pin.SafeFileHandle.IsClosed &&
                    !pin.SafeFileHandle.IsInvalid &&
                    pin.Length == Identity.ByteLength);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                return ValueTask.FromResult(false);
            }
        }

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pin = Volatile.Read(ref _pin);
            if (pin is null)
                throw new ObjectDisposedException(nameof(FileArtifactPin));

            // The reader addresses the already-pinned file object, not the cache path,
            // so a path replacement can never upgrade an old context.
            Stream reader = new PinnedHandleReadStream(
                pin.SafeFileHandle,
                Identity.ByteLength);
            return ValueTask.FromResult(reader);
        }

        public async ValueTask DisposeAsync()
        {
            var pin = Interlocked.Exchange(ref _pin, null);
            if (pin is not null)
                await pin.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class PinnedHandleReadStream(
        SafeFileHandle handle,
        long length) : Stream
    {
        private SafeFileHandle? _handle = handle;
        private long _position;

        public override bool CanRead => _handle is not null;

        public override bool CanSeek => _handle is not null;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value is >= 0 && value <= length
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            var available = (int)Math.Min(count, length - _position);
            if (available <= 0)
                return 0;
            var read = RandomAccess.Read(Current, buffer.AsSpan(offset, available), _position);
            _position = checked(_position + read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var available = (int)Math.Min(buffer.Length, length - _position);
            if (available <= 0)
                return 0;
            var read = await RandomAccess.ReadAsync(
                Current,
                buffer[..available],
                _position,
                cancellationToken).ConfigureAwait(false);
            _position = checked(_position + read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = target;
            return target;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _handle = null;
            base.Dispose(disposing);
        }

        private SafeFileHandle Current
            => Volatile.Read(ref _handle)
               ?? throw new ObjectDisposedException(nameof(PinnedHandleReadStream));

        private static void ValidateBuffer(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
                throw new ArgumentException("The requested buffer range is invalid.");
        }
    }

    private static class NativeMethods
    {
        internal const uint FileFlagBackupSemantics = 0x02000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        internal static string? TryGetFinalPath(SafeFileHandle handle)
        {
            var capacity = 512;
            while (capacity <= 32_768)
            {
                var buffer = new StringBuilder(capacity);
                var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags: 0);
                if (length == 0)
                    return null;
                if (length < buffer.Capacity)
                    return NormalizeFinalPath(buffer.ToString());
                capacity = checked((int)length + 1);
            }
            return null;
        }

        private static string NormalizeFinalPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string localPrefix = @"\\?\";
            if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
                return @"\\" + path[uncPrefix.Length..];
            return path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
                ? path[localPrefix.Length..]
                : path;
        }
    }
}
