using Microsoft.Win32.SafeHandles;
using Xunit;

public sealed class LinuxSyntheticStagingRootTests
{
    [Fact]
    public void CreatesPrivateFdRelativeDirectoriesAndFixture()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var parent = File.OpenHandle(root, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
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
            using var parent = File.OpenHandle(root, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
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
            using var parent = File.OpenHandle(root, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var id = Guid.NewGuid();
            using var first = LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, id);
            Assert.ThrowsAny<Exception>(() => LinuxSyntheticStagingRoot.CreateProbeDirectory(parent, id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
