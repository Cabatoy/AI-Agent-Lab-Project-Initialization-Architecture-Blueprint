using Xunit;

public sealed class SyntheticFixtureLayoutBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);

    private static SandboxProbeManifest Manifest() =>
        RootlessPodmanSandboxPreflight.CreateManifest(
            "sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64),
            new SandboxResourceLimits(50000, 100000, 134217728, 32, 10, 65536),
            Now.AddMinutes(-1), Now.AddMinutes(2));

    [Fact]
    public void ProducesDistinctManifestBoundRelativeDirectories()
    {
        var a = Manifest();
        var b = Manifest();
        var first = SyntheticFixtureLayoutBuilder.Build(a, "fixture", Now);
        var second = SyntheticFixtureLayoutBuilder.Build(b, "fixture", Now);
        Assert.Equal("probes/" + a.ProbeId.ToString("N"), first.RelativeProbeRoot);
        Assert.Equal(first.RelativeProbeRoot + "/input", first.RelativeInputDirectory);
        Assert.Equal(first.RelativeProbeRoot + "/output", first.RelativeOutputDirectory);
        Assert.Equal(first.RelativeInputDirectory + "/fixture", first.RelativeInputFile);
        Assert.NotEqual(first.RelativeProbeRoot, second.RelativeProbeRoot);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/file")]
    [InlineData("nested\\file")]
    [InlineData("/etc/passwd")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("fixture.txt")]
    [InlineData("UPPER")]
    [InlineData("")]
    public void RejectsUnsafeFixtureNames(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            SyntheticFixtureLayoutBuilder.Build(Manifest(), name, Now));
    }

    [Fact]
    public void RejectsTamperedManifest()
    {
        var manifest = Manifest();
        Assert.Throws<InvalidOperationException>(() =>
            SyntheticFixtureLayoutBuilder.Build(
                manifest with { WritableOutputRoot = "../output" }, "fixture", Now));
    }

    [Fact]
    public void RejectsExpiredManifest()
    {
        var manifest = Manifest();
        Assert.Throws<InvalidOperationException>(() =>
            SyntheticFixtureLayoutBuilder.Build(manifest, "fixture", manifest.ExpiresAtUtc));
    }

    [Fact]
    public void RejectsAgentExecutionFlag()
    {
        var manifest = Manifest();
        Assert.Throws<InvalidOperationException>(() =>
            SyntheticFixtureLayoutBuilder.Build(
                manifest with { AgentCodeExecutionAllowed = true }, "fixture", Now));
    }
}
