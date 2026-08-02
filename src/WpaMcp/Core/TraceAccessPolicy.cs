using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WpaMcp.Core;

internal sealed class TraceAccessException : Exception
{
    internal TraceAccessException(string code, string message)
        : base(message)
    {
        TraceAccessErrorProjection.RequireReviewed(code);
        Code = code;
        ToolFailureCaptureContext.Record(this);
    }

    internal string Code { get; }
}

internal readonly record struct TraceSourceHandleIdentity(
    string FinalPath,
    uint VolumeSerialNumber,
    ulong FileId,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc);

internal sealed class ValidatedTraceSource : IAsyncDisposable
{
    private SafeFileHandle? _handle;

    internal ValidatedTraceSource(
        SafeFileHandle handle,
        TraceSourceHandleIdentity identity,
        string extension)
    {
        _handle = handle;
        Identity = identity;
        Extension = extension;
    }

    internal TraceSourceHandleIdentity Identity { get; }
    internal string Extension { get; }

    internal TraceSourceHandleIdentity CaptureCurrentIdentity()
    {
        var handle = Volatile.Read(ref _handle)
            ?? throw new ObjectDisposedException(nameof(ValidatedTraceSource));
        var info = WindowsTraceFile.GetIdentity(handle);
        return new TraceSourceHandleIdentity(
            Identity.FinalPath,
            info.VolumeSerialNumber,
            info.FileId,
            info.Length,
            info.CreationTimeUtc,
            info.LastWriteTimeUtc);
    }

    internal async ValueTask<long> CopyToAsync(
        Stream destination,
        IncrementalHash hash,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(hash);
        var handle = Volatile.Read(ref _handle)
            ?? throw new ObjectDisposedException(nameof(ValidatedTraceSource));
        var rented = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long offset = 0;
            while (true)
            {
                var read = await RandomAccess.ReadAsync(
                    handle,
                    rented.AsMemory(0, 1024 * 1024),
                    offset,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return offset;
                var next = checked(offset + read);
                if (next > maxBytes)
                {
                    throw new TraceAccessException(
                        "trace_too_large",
                        "The trace exceeds the configured input-size limit.");
                }
                hash.AppendData(rented, 0, read);
                await destination.WriteAsync(
                    rented.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
                offset = next;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _handle, null)?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The only component authorized to turn an untrusted source path into an owned
/// file handle. Path normalization is used for syntax; authorization is repeated
/// against the final path obtained from the opened file handle.
/// </summary>
internal sealed class TraceAccessPolicy : IDisposable
{
    private readonly TraceRuntimeOptions _options;
    private readonly RootHandle[] _roots;
    private int _disposed;

    internal TraceAccessPolicy(TraceRuntimeOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Secure trace loading requires Windows file handles.");

        _options = options.ValidatePure();
        var roots = new List<RootHandle>(_options.AllowedRoots.Count);
        try
        {
            foreach (var configuredRoot in _options.AllowedRoots)
            {
                RejectRawNamespace(configuredRoot);
                RejectReparseAncestry(configuredRoot, includeLeaf: true);
                var handle = WindowsTraceFile.OpenDirectory(configuredRoot);
                var finalPath = NormalizeFinalPath(WindowsTraceFile.GetFinalPath(handle));
                if (IsUncOrDevicePath(finalPath))
                {
                    handle.Dispose();
                    throw Denied("trace_root_not_local");
                }
                roots.Add(new RootHandle(handle, finalPath));
            }
        }
        catch
        {
            foreach (var root in roots)
                root.Handle.Dispose();
            throw;
        }
        _roots = roots.ToArray();
    }

    internal long MaxInputTraceBytes => _options.MaxInputTraceBytes;
    internal string ArtifactRoot => _options.ArtifactRoot;

    internal ValueTask<ValidatedTraceSource> OpenAsync(
        string rawPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);

        RejectRawNamespace(rawPath);
        if (!Path.IsPathFullyQualified(rawPath))
            throw Denied("trace_path_not_absolute");
        RejectTraversalSegments(rawPath);
        RejectAlternateDataStream(rawPath);
        var extension = Path.GetExtension(rawPath);
        if (!extension.Equals(".etl", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".etlx", StringComparison.OrdinalIgnoreCase))
        {
            throw Denied("trace_extension_not_allowed");
        }

        // This bounded metadata walk is deliberately before any artifact-root write.
        RejectReparseAncestry(rawPath, includeLeaf: true);

        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(
                rawPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (WindowsTraceFile.GetFileType(handle) != WindowsTraceFile.FileTypeDisk)
                throw Denied("trace_source_not_disk_file");

            var finalPath = NormalizeFinalPath(WindowsTraceFile.GetFinalPath(handle));
            if (IsUncOrDevicePath(finalPath) || !IsWithinCurrentRoot(finalPath))
                throw Denied("trace_path_outside_allowed_roots");

            var info = WindowsTraceFile.GetIdentity(handle);
            if ((info.FileAttributes & FileAttributes.Directory) != 0 ||
                (info.FileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Denied("trace_source_not_regular_file");
            }
            if (info.Length > _options.MaxInputTraceBytes)
            {
                throw new TraceAccessException(
                    "trace_too_large",
                    "The trace exceeds the configured input-size limit.");
            }

            var identity = new TraceSourceHandleIdentity(
                finalPath,
                info.VolumeSerialNumber,
                info.FileId,
                info.Length,
                info.CreationTimeUtc,
                info.LastWriteTimeUtc);
            var result = new ValidatedTraceSource(
                handle,
                identity,
                extension.ToLowerInvariant());
            handle = null;
            return ValueTask.FromResult(result);
        }
        catch (TraceAccessException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw Denied("trace_source_open_denied");
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var root in _roots)
            root.Handle.Dispose();
    }

    private bool IsWithinCurrentRoot(string finalSourcePath)
    {
        foreach (var root in _roots)
        {
            var currentRoot = NormalizeFinalPath(WindowsTraceFile.GetFinalPath(root.Handle));
            if (IsPathWithin(currentRoot, finalSourcePath))
                return true;
        }
        return false;
    }

    private static bool IsPathWithin(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return false;
        return candidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectRawNamespace(string path)
    {
        if (IsUncOrDevicePath(path))
            throw Denied("trace_path_namespace_denied");
    }

    private static bool IsUncOrDevicePath(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal) ||
        path.StartsWith("\\??\\", StringComparison.Ordinal) ||
        path.StartsWith("\\?\\", StringComparison.Ordinal) ||
        path.StartsWith("\\.\\", StringComparison.Ordinal);

    private static void RejectTraversalSegments(string path)
    {
        foreach (var segment in path.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                throw Denied("trace_path_traversal_denied");
        }
    }

    private static void RejectAlternateDataStream(string path)
    {
        var firstColon = path.IndexOf(':');
        if (firstColon != 1 || path.IndexOf(':', firstColon + 1) >= 0)
            throw Denied("trace_alternate_data_stream_denied");
    }

    private static void RejectReparseAncestry(string path, bool includeLeaf)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Denied("trace_path_invalid");
        }

        var root = Path.GetPathRoot(fullPath)
            ?? throw Denied("trace_path_invalid");
        var relative = fullPath[root.Length..];
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var count = includeLeaf ? components.Length : Math.Max(0, components.Length - 1);
        for (var index = 0; index < count; index++)
        {
            current = Path.Combine(current, components[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Denied("trace_path_metadata_denied");
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw Denied("trace_reparse_path_denied");
        }
    }

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            return "\\\\" + path[8..];
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }

    private static TraceAccessException Denied(string detail) =>
        new("trace_access_denied", $"Trace access policy rejected the source ({detail}).");

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(TraceAccessPolicy));
    }

    private sealed record RootHandle(SafeFileHandle Handle, string InitialFinalPath);
}

internal static class WindowsTraceFile
{
    internal const uint FileTypeDisk = 0x0001;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    internal static SafeFileHandle OpenDirectory(
        string path,
        bool allowDeleteSharing = true)
    {
        var handle = CreateFileW(
            path,
            0,
            FileShareRead | FileShareWrite |
            (allowDeleteSharing ? FileShareDelete : 0),
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return handle;
    }

    internal static string GetFinalPath(SafeFileHandle handle)
    {
        var required = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (required == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new char[checked((int)required + 1)];
        var written = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (written == 0 || written >= buffer.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new string(buffer, 0, checked((int)written));
    }

    internal static uint GetFileType(SafeFileHandle handle) => GetFileTypeNative(handle);

    internal static WindowsFileIdentity GetIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return new WindowsFileIdentity(
            (FileAttributes)info.FileAttributes,
            info.VolumeSerialNumber,
            fileId,
            length,
            FromFileTime(info.CreationTime),
            FromFileTime(info.LastWriteTime));
    }

    private static DateTime FromFileTime(
        System.Runtime.InteropServices.ComTypes.FILETIME value)
    {
        var raw = ((long)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
        return DateTime.FromFileTimeUtc(raw);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] char[]? filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static extern uint GetFileTypeNative(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}

internal readonly record struct WindowsFileIdentity(
    FileAttributes FileAttributes,
    uint VolumeSerialNumber,
    ulong FileId,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc);
