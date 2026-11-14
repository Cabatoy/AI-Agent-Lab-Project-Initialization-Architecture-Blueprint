using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Linux-only fd-relative private staging root creation. No shell, Podman, mounts,
/// symlink traversal or cleanup. Caller supplies a trusted, pre-opened directory fd.
/// Ownership of the returned fd stays with the caller. Fail closed on any OS error.
/// </summary>
internal static class LinuxSyntheticStagingRoot
{
    private const int O_RDONLY = 0;
    private const int O_DIRECTORY = 0x10000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_CLOEXEC = 0x80000;
    private const int O_EXCL = 0x80;
    private const int O_CREAT = 0x40;
    private const int O_WRONLY = 1;

    [DllImport("libc", SetLastError = true, EntryPoint = "mkdirat")]
    private static extern int MkdirAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "openat")]
    private static extern int OpenAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "unlinkat")]
    private static extern int UnlinkAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Linux staging only.");
    }

    private static string Segment(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 ||
            !(name[0] is >= 'a' and <= 'z' or >= '0' and <= '9') ||
            name.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
            throw new ArgumentException("Unsafe staging segment.", nameof(name));
        return name;
    }

    private static SafeFileHandle OpenDirectory(SafeFileHandle parent, string name)
    {
        var fd = OpenAt(parent.DangerousGetHandle().ToInt32(), Segment(name),
            O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Staging directory open rejected.");
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    /// <summary>
    /// Creates a new, private probe directory. No pre-existing directory is accepted.
    /// The parent must already be a trusted directory fd and must not be attacker writable.
    /// </summary>
    internal static SafeFileHandle CreateProbeDirectory(SafeFileHandle trustedParent, Guid probeId)
    {
        RequireLinux();
        ArgumentNullException.ThrowIfNull(trustedParent);
        if (trustedParent.IsInvalid || trustedParent.IsClosed || probeId == Guid.Empty)
            throw new ArgumentException("Invalid staging parent or probe ID.");
        var name = probeId.ToString("N");
        if (MkdirAt(trustedParent.DangerousGetHandle().ToInt32(), name, 0x1C0) != 0) // 0700
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Probe directory creation rejected.");
        try
        {
            return OpenDirectory(trustedParent, name);
        }
        catch
        {
            // Only the exact newly created empty directory; never recursive deletion.
            UnlinkAt(trustedParent.DangerousGetHandle().ToInt32(), name, 0x200); // AT_REMOVEDIR
            throw;
        }
    }

    /// <summary>Creates a private child directory and returns its no-follow handle.</summary>
    internal static SafeFileHandle CreateChildDirectory(SafeFileHandle parent, string name)
    {
        RequireLinux();
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.IsInvalid || parent.IsClosed) throw new ArgumentException("Invalid parent.");
        name = Segment(name);
        if (MkdirAt(parent.DangerousGetHandle().ToInt32(), name, 0x1C0) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Child directory creation rejected.");
        return OpenDirectory(parent, name);
    }

    /// <summary>Exclusively writes a bounded fixture file through a directory fd.</summary>
    internal static void WriteFixture(SafeFileHandle inputDirectory, string name, ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        RequireLinux();
        ArgumentNullException.ThrowIfNull(inputDirectory);
        if (inputDirectory.IsInvalid || inputDirectory.IsClosed || maximumBytes < 0 ||
            bytes.Length > maximumBytes) throw new ArgumentException("Invalid fixture bound or directory.");
        name = Segment(name);
        var fd = OpenAt(inputDirectory.DangerousGetHandle().ToInt32(), name,
            O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0x180); // 0600
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Fixture creation rejected.");
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        using var stream = new FileStream(handle, FileAccess.Write);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
    /// <summary>
    /// Binds the staging directory name to the manifest probe ID before any creation.
    /// The trusted parent directory must be opened and verified by the caller.
    /// </summary>
    internal static SafeFileHandle CreateManifestProbeDirectory(
        SafeFileHandle trustedParent,
        SandboxProbeManifest manifest,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireValidOfflineManifest(manifest, nowUtc);
        return CreateProbeDirectory(trustedParent, manifest.ProbeId);
    }

    /// <summary>
    /// In-process creation receipt. Issued only after exclusive mkdirat and
    /// no-follow open. Holds the original directory handle, not a path string.
    /// This is not durable, transferable or sufficient for deletion.
    /// </summary>
    internal sealed class CreatedProbeReceipt : IDisposable
    {
        private readonly SafeFileHandle directory;
        private bool disposed;

        internal CreatedProbeReceipt(Guid probeId, SafeFileHandle directory)
        {
            ProbeId = probeId;
            this.directory = directory;
        }

        internal Guid ProbeId { get; }

        /// <summary>
        /// Inspects a child relative to the originally created probe handle.
        /// A replacement of the probe's directory entry cannot redirect this
        /// lookup to a different probe. No deletion or ownership attestation.
        /// </summary>
        internal SafeFileHandle OpenChildForInspection(string name)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CreatedProbeReceipt));
            return OpenChildDirectoryForInspection(directory, name);
        }

        internal bool IsEmptyForCleanupInspection()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CreatedProbeReceipt));
            return IsEmptyDirectoryForCleanup(directory);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            directory.Dispose();
        }
    }

    /// <summary>
    /// Issues a non-transferable in-process receipt from actual creation,
    /// never from a caller-supplied path or claimed ownership metadata.
    /// </summary>
    internal static CreatedProbeReceipt CreateManifestProbeWithReceipt(
        SafeFileHandle trustedParent,
        SandboxProbeManifest manifest,
        DateTimeOffset nowUtc)
    {
        var directory = CreateManifestProbeDirectory(trustedParent, manifest, nowUtc);
        return new CreatedProbeReceipt(manifest.ProbeId, directory);
    }

    private static void RequireValidOfflineManifest(
        SandboxProbeManifest manifest, DateTimeOffset nowUtc)
    {
        var report = RootlessPodmanSandboxPreflight.Evaluate(manifest, null, null, nowUtc);
        if (report.AgentCodeExecutionAllowed ||
            report.SanitizedReasons.Count != 1 ||
            report.SanitizedReasons[0] != "runtime-evidence-unavailable")
            throw new InvalidOperationException("Manifest invalid or expired.");
    }

    /// <summary>
    /// Writes only a fixture whose bytes match the immutable manifest input digest.
    /// This is NOT an authorization or filesystem ownership check.
    /// </summary>
    internal static void WriteManifestFixture(
        SafeFileHandle inputDirectory,
        string name,
        ReadOnlySpan<byte> bytes,
        int maximumBytes,
        SandboxProbeManifest manifest,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireValidOfflineManifest(manifest, nowUtc);

        if (maximumBytes < 0 || bytes.Length > maximumBytes)
            throw new ArgumentException("Fixture exceeds allowed size.", nameof(bytes));

        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, manifest.StagedInputDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("Fixture SHA-256 does not match manifest.");

        WriteFixture(inputDirectory, name, bytes, maximumBytes);
    }

    /// <summary>
    /// Opens an existing child for inspection only. Never follows the final
    /// path component if it is a symlink; never creates or deletes anything.
    /// This does not establish ownership or authorize cleanup.
    /// </summary>
    internal static SafeFileHandle OpenChildDirectoryForInspection(
        SafeFileHandle trustedParent, string name)
    {
        RequireLinux();
        ArgumentNullException.ThrowIfNull(trustedParent);
        if (trustedParent.IsInvalid || trustedParent.IsClosed)
            throw new ArgumentException("Invalid parent directory.", nameof(trustedParent));
        return OpenDirectory(trustedParent, name);
    }

    /// <summary>
    /// Non-destructive cleanup admission: a directory must be empty before a
    /// future trusted cleanup worker can remove it. This method never deletes.
    /// Rejects unexpected files, subdirectories and symlinks (including dangling).
    /// The caller must independently prove directory ownership and avoid races.
    /// </summary>
    internal static bool IsEmptyDirectoryForCleanup(SafeFileHandle directory)
    {
        RequireLinux();
        ArgumentNullException.ThrowIfNull(directory);
        if (directory.IsInvalid || directory.IsClosed)
            throw new ArgumentException("Invalid directory handle.", nameof(directory));

        // Enumerate via /proc/self/fd to stay bound to the open directory,
        // rather than resolving a caller-provided path.
        var fd = directory.DangerousGetHandle().ToInt32();
        var fdPath = "/proc/self/fd/" + fd.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var enumerator = Directory.EnumerateFileSystemEntries(fdPath).GetEnumerator();
        return !enumerator.MoveNext();
    }

}
