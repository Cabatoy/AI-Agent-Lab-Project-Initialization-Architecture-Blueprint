using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Xunit;

public sealed class LinuxSyntheticStagingRootTests
{
    // Test-only directory fd: File.OpenHandle is for regular files on Linux.
    // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, with read-only access.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static SafeFileHandle OpenTestDirectory(string path)
    {
        const int O_DIRECTORY = 0x10000;
        const int O_NOFOLLOW = 0x20000;
        const int O_CLOEXEC = 0x80000;
        var fd = Open(path, O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot open test staging directory.");
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    [Fact]
    public void CreatesPrivateFdRelativeDirectoriesAndFixture()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var probe = Guid.NewGuid();
            using var probeFd = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, probe);
            using var input = LinuxSyntheticStagingRoot.CreateChildDirectory(probeFd, "input");
            using var output = LinuxSyntheticStagingRoot.CreateChildDirectory(probeFd, "output");
            LinuxSyntheticStagingRoot.WriteFixture(input, "fixture", "ok"u8, 16);
            Assert.Equal("ok", File.ReadAllText(Path.Combine(root, probe.ToString("N"), "input", "fixture")));
            Assert.True(Directory.Exists(Path.Combine(root, probe.ToString("N"), "output")));
            Assert.ThrowsAny<Exception>(() =>
                LinuxSyntheticStagingRoot.WriteFixture(input, "fixture", "other"u8, 16));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RejectsTraversalAndOversizedFixture()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            using var probe = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, Guid.NewGuid());
            Assert.Throws<ArgumentException>(() =>
                LinuxSyntheticStagingRoot.CreateChildDirectory(probe, "../escape"));
            Assert.Throws<ArgumentException>(() =>
                LinuxSyntheticStagingRoot.WriteFixture(probe, "fixture", "too-big"u8, 2));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExistingProbeDirectoryIsNotReused()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var id = Guid.NewGuid();
            using var first = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, id);
            Assert.ThrowsAny<Exception>(() => LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static SandboxProbeManifest ManifestFor(byte[] bytes, DateTimeOffset now) =>
        RootlessPodmanSandboxPreflight.CreateManifest(
            "sha256:" + new string('a', 64),
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            new SandboxResourceLimits(50000, 100000, 134217728, 32, 10, 65536),
            now.AddMinutes(-1), now.AddMinutes(1));

    [Fact]
    public void ManifestFixtureWritesMatchingBytes()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var probeId = Guid.NewGuid();
            using var probe = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, probeId);
            using var input = LinuxSyntheticStagingRoot.CreateChildDirectory(probe, "input");
            var now = DateTimeOffset.UtcNow;
            var bytes = "verified fixture"u8.ToArray();
            var manifest = ManifestFor(bytes, now);
            LinuxSyntheticStagingRoot.WriteManifestFixture(input, "fixture", bytes, 128, manifest, now);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, probeId.ToString("N"), "input", "fixture")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ManifestFixtureRejectsMismatchWithoutCreatingFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var probeId = Guid.NewGuid();
            using var probe = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, probeId);
            using var input = LinuxSyntheticStagingRoot.CreateChildDirectory(probe, "input");
            var now = DateTimeOffset.UtcNow;
            var manifest = ManifestFor("expected"u8.ToArray(), now);
            Assert.Throws<InvalidOperationException>(() =>
                LinuxSyntheticStagingRoot.WriteManifestFixture(input, "fixture", "tampered"u8, 128, manifest, now));
            Assert.False(File.Exists(Path.Combine(root, probeId.ToString("N"), "input", "fixture")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ManifestFixtureRejectsExpiredManifest()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            using var probe = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, Guid.NewGuid());
            using var input = LinuxSyntheticStagingRoot.CreateChildDirectory(probe, "input");
            var now = DateTimeOffset.UtcNow;
            var bytes = "fixture"u8.ToArray();
            var manifest = ManifestFor(bytes, now);
            Assert.Throws<InvalidOperationException>(() =>
                LinuxSyntheticStagingRoot.WriteManifestFixture(input, "fixture", bytes, 128, manifest, manifest.ExpiresAtUtc));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ManifestProbeDirectoryUsesManifestId()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var now = DateTimeOffset.UtcNow;
            var manifest = ManifestFor("fixture"u8.ToArray(), now);
            using var probe = LinuxSyntheticStagingRoot.CreateManifestProbeDirectory(parent, manifest, now);
            Assert.True(Directory.Exists(Path.Combine(root, manifest.ProbeId.ToString("N"))));
            Assert.ThrowsAny<Exception>(() =>
                LinuxSyntheticStagingRoot.CreateManifestProbeDirectory(parent, manifest, now));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExpiredManifestCannotCreateProbeDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var now = DateTimeOffset.UtcNow;
            var manifest = ManifestFor("fixture"u8.ToArray(), now);
            Assert.Throws<InvalidOperationException>(() =>
                LinuxSyntheticStagingRoot.CreateManifestProbeDirectory(parent, manifest, manifest.ExpiresAtUtc));
            Assert.False(Directory.Exists(Path.Combine(root, manifest.ProbeId.ToString("N"))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TamperedManifestCannotCreateProbeDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var now = DateTimeOffset.UtcNow;
            var manifest = ManifestFor("fixture"u8.ToArray(), now);
            var tampered = manifest with { ProbeId = Guid.NewGuid() };
            Assert.Throws<InvalidOperationException>(() =>
                LinuxSyntheticStagingRoot.CreateManifestProbeDirectory(parent, tampered, now));
            Assert.False(Directory.Exists(Path.Combine(root, tampered.ProbeId.ToString("N"))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CleanupAdmissionRejectsUnexpectedFileAndKeepsIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var probeId = Guid.NewGuid();
            using var probe = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, probeId);
            Assert.True(LinuxSyntheticStagingRoot.IsEmptyDirectoryForCleanup(probe));
            using var input = LinuxSyntheticStagingRoot.CreateChildDirectory(probe, "input");
            Assert.False(LinuxSyntheticStagingRoot.IsEmptyDirectoryForCleanup(probe));
            LinuxSyntheticStagingRoot.WriteFixture(input, "fixture", "preserve"u8, 32);
            Assert.False(LinuxSyntheticStagingRoot.IsEmptyDirectoryForCleanup(input));
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(root, probeId.ToString("N"), "input", "fixture")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CleanupAdmissionRejectsSymlinkWithoutFollowingIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var probeId = Guid.NewGuid();
            using var probe = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, probeId);
            var link = Path.Combine(root, probeId.ToString("N"), "outside");
            File.CreateSymbolicLink(link, "/tmp/does-not-exist-hic-sentinel");
            Assert.False(LinuxSyntheticStagingRoot.IsEmptyDirectoryForCleanup(probe));
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InspectionHandleRejectsSymlinkToExternalDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var probeId = Guid.NewGuid();
            using var probe = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, probeId);
            var link = Path.Combine(root, probeId.ToString("N"), "external");
            Directory.CreateSymbolicLink(link, "/tmp");
            Assert.ThrowsAny<Exception>(() =>
                LinuxSyntheticStagingRoot.OpenChildDirectoryForInspection(probe, "external"));
            Assert.False(LinuxSyntheticStagingRoot.IsEmptyDirectoryForCleanup(probe));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InspectionHandleRejectsTraversalAndAcceptsRealDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            using var probe = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, Guid.NewGuid());
            using var created = LinuxSyntheticStagingRoot.CreateChildDirectory(probe, "input");
            using var reopened = LinuxSyntheticStagingRoot.OpenChildDirectoryForInspection(probe, "input");
            Assert.True(LinuxSyntheticStagingRoot.IsEmptyDirectoryForCleanup(reopened));
            Assert.Throws<ArgumentException>(() =>
                LinuxSyntheticStagingRoot.OpenChildDirectoryForInspection(probe, "../tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CreationReceiptIsBoundToCreatedProbeAndDisposalRevokesInspection()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var now = DateTimeOffset.UtcNow;
            var manifest = ManifestFor("fixture"u8.ToArray(), now);
            var receipt = LinuxSyntheticStagingRoot.CreateManifestProbeWithReceipt(parent, manifest, now);
            Assert.Equal(manifest.ProbeId, receipt.ProbeId);
            Assert.True(receipt.IsEmptyForCleanupInspection());
            receipt.Dispose();
            Assert.Throws<ObjectDisposedException>(() => receipt.IsEmptyForCleanupInspection());
            Assert.True(Directory.Exists(Path.Combine(root, manifest.ProbeId.ToString("N"))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CreationReceiptCannotBeIssuedForExistingProbe()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var now = DateTimeOffset.UtcNow;
            var manifest = ManifestFor("fixture"u8.ToArray(), now);
            using var receipt = LinuxSyntheticStagingRoot.CreateManifestProbeWithReceipt(parent, manifest, now);
            Assert.ThrowsAny<Exception>(() =>
                LinuxSyntheticStagingRoot.CreateManifestProbeWithReceipt(parent, manifest, now));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReceiptInspectsOnlyItsOriginalProbeAfterDirectoryEntryReplacement()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var now = DateTimeOffset.UtcNow;
            var manifest = ManifestFor("fixture"u8.ToArray(), now);
            using var receipt = LinuxSyntheticStagingRoot.CreateManifestProbeWithReceipt(parent, manifest, now);
            var original = Path.Combine(root, manifest.ProbeId.ToString("N"));
            var moved = Path.Combine(root, "moved-original");
            Directory.Move(original, moved);
            Directory.CreateDirectory(original);
            Directory.CreateDirectory(Path.Combine(original, "replacement"));
            Directory.CreateDirectory(Path.Combine(moved, "original-child"));

            using var originalChild = receipt.OpenChildForInspection("original-child");
            Assert.True(LinuxSyntheticStagingRoot.IsEmptyDirectoryForCleanup(originalChild));
            Assert.ThrowsAny<Exception>(() => receipt.OpenChildForInspection("replacement"));
            Assert.False(receipt.IsEmptyForCleanupInspection());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReceiptRejectsChildInspectionAfterDisposal()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenTestDirectory(root);
            var now = DateTimeOffset.UtcNow;
            var manifest = ManifestFor("fixture"u8.ToArray(), now);
            var receipt = LinuxSyntheticStagingRoot.CreateManifestProbeWithReceipt(parent, manifest, now);
            receipt.Dispose();
            Assert.Throws<ObjectDisposedException>(() => receipt.OpenChildForInspection("input"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

}
