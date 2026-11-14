using Xunit;

public sealed class SyntheticPodmanCommandBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);

    private static SandboxProbeManifest Manifest() =>
        RootlessPodmanSandboxPreflight.CreateManifest(
            "sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64),
            new SandboxResourceLimits(50000, 100000, 134217728, 32, 10, 65536),
            Now.AddMinutes(-1), Now.AddMinutes(2));

    [Fact]
    public void UsesFixedExecutableAndRestrictiveArguments()
    {
        var command = SyntheticPodmanCommandBuilder.Build(Manifest(), Now);
        Assert.Equal("/usr/bin/podman", command.Executable);
        Assert.Contains("--network=none", command.Arguments);
        Assert.Contains("--read-only", command.Arguments);
        Assert.Contains("--cap-drop=ALL", command.Arguments);
        Assert.Contains("--security-opt=no-new-privileges", command.Arguments);
        Assert.Contains("--userns=private", command.Arguments);
        Assert.Contains("--user=65534:65534", command.Arguments);
        Assert.Contains("--pids-limit=32", command.Arguments);
        Assert.Contains("--memory=134217728", command.Arguments);
        Assert.Contains("--cpu-period=100000", command.Arguments);
        Assert.Contains("--cpu-quota=50000", command.Arguments);
        Assert.Contains("--pull=never", command.Arguments);
        Assert.Contains("localhost/hic-synthetic-fixture@" + Manifest().ImageDigest, command.Arguments);
        Assert.DoesNotContain(command.Arguments, arg =>
            arg.StartsWith("--volume", StringComparison.Ordinal) ||
            arg.StartsWith("--mount", StringComparison.Ordinal) ||
            arg.StartsWith("--env", StringComparison.Ordinal) ||
            arg.StartsWith("--privileged", StringComparison.Ordinal) ||
            arg.StartsWith("--network=host", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsTamperedManifest()
    {
        var manifest = Manifest();
        Assert.Throws<InvalidOperationException>(() =>
            SyntheticPodmanCommandBuilder.Build(
                manifest with { Limits = manifest.Limits with { Pids = 1000 } }, Now));
    }

    [Fact]
    public void RejectsExpiredManifest()
    {
        var manifest = Manifest();
        Assert.Throws<InvalidOperationException>(() =>
            SyntheticPodmanCommandBuilder.Build(manifest, manifest.ExpiresAtUtc));
    }

    [Fact]
    public void RejectsAgentExecutionFlag()
    {
        var manifest = Manifest();
        Assert.Throws<InvalidOperationException>(() =>
            SyntheticPodmanCommandBuilder.Build(
                manifest with { AgentCodeExecutionAllowed = true }, Now));
    }

    [Fact]
    public void NeverIncludesHostCredentialsOrInputPaths()
    {
        var command = SyntheticPodmanCommandBuilder.Build(Manifest(), Now);
        var rendered = string.Join(" ", command.Arguments);
        Assert.DoesNotContain("/home/", rendered);
        Assert.DoesNotContain("/var/run/", rendered);
        Assert.DoesNotContain("AWS_", rendered);
        Assert.DoesNotContain("SSH_", rendered);
        Assert.DoesNotContain("--env", rendered);
    }
}
