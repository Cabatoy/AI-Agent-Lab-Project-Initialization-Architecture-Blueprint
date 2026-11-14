using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Xunit;

public sealed class SyntheticProbeStagingLifecycleTests
{
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static SafeFileHandle OpenParent(string path)
    {
        var fd = Open(path, 0x10000 | 0x20000 | 0x80000);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static SandboxProbeManifest Manifest(byte[] bytes, DateTimeOffset now) =>
        RootlessPodmanSandboxPreflight.CreateManifest(
            "sha256:" + new string('a', 64),
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            new SandboxResourceLimits(50000, 100000, 134217728, 32, 10, 65536),
            now.AddMinutes(-1), now.AddMinutes(1));

    [Fact]
    public void LifecycleStagesAndVerifiesWithoutAuthorizingExecution()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-lifecycle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenParent(root);
            var now = DateTimeOffset.UtcNow;
            var bytes = "synthetic-fixture"u8.ToArray();
            var manifest = Manifest(bytes, now);
            var (result, receipt) = SyntheticProbeStagingLifecycle.PrepareAndVerify(
                parent, manifest, bytes, 128, now);
            using (receipt)
            {
                Assert.Equal(manifest.ProbeId, result.ProbeId);
                Assert.Equal(manifest.ManifestDigest, result.ManifestDigest);
                Assert.True(result.InputVerified);
                Assert.False(result.AgentExecutionAllowed);
                Assert.Equal("staged-input-verified", result.Status);
                Assert.True(receipt.VerifyStagedInput(manifest, 128, now));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LifecycleRejectsTamperingBeforeCreatingProbe()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-lifecycle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenParent(root);
            var now = DateTimeOffset.UtcNow;
            var manifest = Manifest("expected"u8.ToArray(), now);
            Assert.Throws<InvalidOperationException>(() =>
                SyntheticProbeStagingLifecycle.PrepareAndVerify(
                    parent, manifest, "tampered"u8.ToArray(), 128, now));
            Assert.False(Directory.Exists(Path.Combine(root, manifest.ProbeId.ToString("N"))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LifecycleRejectsExpiredManifestWithoutCreatingProbe()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-lifecycle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = OpenParent(root);
            var now = DateTimeOffset.UtcNow;
            var bytes = "fixture"u8.ToArray();
            var manifest = Manifest(bytes, now);
            Assert.Throws<InvalidOperationException>(() =>
                SyntheticProbeStagingLifecycle.PrepareAndVerify(
                    parent, manifest, bytes, 128, manifest.ExpiresAtUtc));
            Assert.False(Directory.Exists(Path.Combine(root, manifest.ProbeId.ToString("N"))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
