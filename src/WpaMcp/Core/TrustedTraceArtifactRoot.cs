using System.Buffers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WpaMcp.Core;

/// <summary>
/// Server-owned artifact boundary. The root is non-reparse, ACL-protected for
/// the current user/system/administrators, and retained by an opened directory
/// handle so every later object can be checked against its current final path.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TrustedTraceArtifactRoot : IDisposable
{
    private SafeFileHandle? _handle;

    internal TrustedTraceArtifactRoot(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The owned artifact store requires Windows.");
        RejectNamespace(configuredPath);
        if (!System.IO.Path.IsPathFullyQualified(configuredPath))
            throw new ArgumentException("The artifact root must be an absolute local path.");

        var fullPath = System.IO.Path.GetFullPath(configuredPath);
        RejectExistingReparseAncestry(fullPath);
        Directory.CreateDirectory(fullPath);
        ProtectDirectory(fullPath);
        RejectExistingReparseAncestry(fullPath);

        var handle = WindowsTraceFile.OpenDirectory(
            fullPath,
            allowDeleteSharing: false);
        try
        {
            var finalPath = NormalizeFinalPath(WindowsTraceFile.GetFinalPath(handle));
            RejectNamespace(finalPath);
            var expectedPath = System.IO.Path.GetFullPath(fullPath).TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            if (!finalPath.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar)
                .Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new TraceAccessException(
                    "trace_artifact_boundary_violation",
                    "The artifact root final path differs from its configured local boundary.");
            }
            Path = finalPath.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            _handle = handle;
            handle = null!;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal string Path { get; }

    internal string GetCurrentFinalPath()
    {
        var handle = Volatile.Read(ref _handle)
            ?? throw new ObjectDisposedException(nameof(TrustedTraceArtifactRoot));
        return NormalizeFinalPath(WindowsTraceFile.GetFinalPath(handle))
            .TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
    }

    internal bool ContainsFinalPath(string candidate)
    {
        var root = GetCurrentFinalPath();
        return candidate.StartsWith(
            root + System.IO.Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    internal OwnedTraceArtifactPin Pin(string expectedPath)
    {
        var handle = File.OpenHandle(
            expectedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess);
        try
        {
            var finalPath = NormalizeFinalPath(WindowsTraceFile.GetFinalPath(handle));
            if (!ContainsFinalPath(finalPath))
            {
                throw new TraceAccessException(
                    "trace_artifact_boundary_violation",
                    "The trace artifact escaped the owned store boundary.");
            }
            var identity = WindowsTraceFile.GetIdentity(handle);
            if ((identity.FileAttributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new TraceAccessException(
                    "trace_artifact_invalid",
                    "The trace artifact is not a regular owned file.");
            }

            var pin = new OwnedTraceArtifactPin(handle, finalPath, identity);
            handle = null!;
            return pin;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();

    private static void ProtectDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var info = new DirectoryInfo(path);
        var existing = info.GetAccessControl(AccessControlSections.Owner);
        var existingOwner = existing.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (existingOwner is null || !existingOwner.Equals(currentUser))
        {
            throw new UnauthorizedAccessException(
                "The artifact root must be owned by the current Windows user.");
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, currentUser);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        info.SetAccessControl(security);

        var verified = info.GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        var owner = verified.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !owner.Equals(currentUser) ||
            !verified.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException(
                "The artifact root ownership or protected ACL could not be verified.");
        }

        var expectedIdentities = new HashSet<SecurityIdentifier>
        {
            currentUser,
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        var rules = verified.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (rules.Length != expectedIdentities.Count || rules.Any(rule =>
                rule.IdentityReference is not SecurityIdentifier sid ||
                !expectedIdentities.Contains(sid) ||
                rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & FileSystemRights.FullControl) !=
                    FileSystemRights.FullControl))
        {
            throw new UnauthorizedAccessException(
                "The artifact root ACL contains an unexpected principal or access mode.");
        }
    }

    private static void AddFullControl(
        DirectorySecurity security,
        SecurityIdentifier identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static void RejectExistingReparseAncestry(string fullPath)
    {
        var root = System.IO.Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("The artifact root is invalid.");
        var relative = fullPath[root.Length..];
        var current = root;
        foreach (var segment in relative.Split(
                     [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
                break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new TraceAccessException(
                    "trace_artifact_reparse_denied",
                    "The artifact root contains a reparse component.");
            }
        }
    }

    private static void RejectNamespace(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("\\??\\", StringComparison.Ordinal) ||
            path.StartsWith("\\?\\", StringComparison.Ordinal) ||
            path.StartsWith("\\.\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("The artifact root must not use UNC or device syntax.");
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
}

internal sealed class OwnedTraceArtifactPin : IDisposable
{
    private SafeFileHandle? _handle;

    internal OwnedTraceArtifactPin(
        SafeFileHandle handle,
        string finalPath,
        WindowsFileIdentity identity)
    {
        _handle = handle;
        FinalPath = finalPath;
        Identity = identity;
    }

    internal string FinalPath { get; }
    internal WindowsFileIdentity Identity { get; }

    internal async Task<string> ComputeSha256Async(
        CancellationToken cancellationToken)
    {
        var handle = Volatile.Read(ref _handle)
            ?? throw new ObjectDisposedException(nameof(OwnedTraceArtifactPin));
        if (WindowsTraceFile.GetIdentity(handle) != Identity)
        {
            throw new TraceAccessException(
                "trace_artifact_changed",
                "The pinned trace artifact changed before content validation.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var rented = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long offset = 0;
            while (offset < Identity.Length)
            {
                var requested = (int)Math.Min(rented.Length, Identity.Length - offset);
                var read = await RandomAccess.ReadAsync(
                    handle,
                    rented.AsMemory(0, requested),
                    offset,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new TraceAccessException(
                        "trace_artifact_changed",
                        "The pinned trace artifact ended before its manifest length.");
                }
                hash.AppendData(rented, 0, read);
                offset = checked(offset + read);
            }

            var trailing = await RandomAccess.ReadAsync(
                handle,
                rented.AsMemory(0, 1),
                offset,
                cancellationToken).ConfigureAwait(false);
            if (trailing != 0 || WindowsTraceFile.GetIdentity(handle) != Identity)
            {
                throw new TraceAccessException(
                    "trace_artifact_changed",
                    "The pinned trace artifact changed during content validation.");
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal void VerifyUnchanged()
    {
        var handle = Volatile.Read(ref _handle)
            ?? throw new ObjectDisposedException(nameof(OwnedTraceArtifactPin));
        if (WindowsTraceFile.GetIdentity(handle) != Identity)
        {
            throw new TraceAccessException(
                "trace_artifact_changed",
                "The pinned trace artifact changed before backend publication.");
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
}
