using Xunit;

public sealed class SyntheticStagingOwnershipPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);

    private static SandboxProbeManifest Manifest() =>
        RootlessPodmanSandboxPreflight.CreateManifest(
            "sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64),
            new SandboxResourceLimits(50000, 100000, 134217728, 32, 10, 65536),
            Now.AddMinutes(-1), Now.AddMinutes(2));

    private static SyntheticStagingOwnership Receipt(SandboxProbeManifest manifest, SyntheticFixtureLayout layout) =>
        new(manifest.ProbeId, manifest.ManifestDigest, layout.RelativeProbeRoot, new string('a', 64));

    [Fact]
    public void ExactOwnedLayoutPassesOfflineAdmissionOnly()
    {
        var manifest = Manifest();
        var layout = SyntheticFixtureLayoutBuilder.Build(manifest, "fixture", Now);
        Assert.True(SyntheticStagingOwnershipPolicy.MayAttemptCleanup(manifest, layout, Receipt(manifest, layout), Now));
    }

    [Fact]
    public void UnownedProbeIsRejected()
    {
        var manifest = Manifest();
        var layout = SyntheticFixtureLayoutBuilder.Build(manifest, "fixture", Now);
        Assert.False(SyntheticStagingOwnershipPolicy.MayAttemptCleanup(
            manifest, layout, Receipt(manifest, layout) with { ProbeId = Guid.NewGuid() }, Now));
    }

    [Fact]
    public void TamperedRootIsRejected()
    {
        var manifest = Manifest();
        var layout = SyntheticFixtureLayoutBuilder.Build(manifest, "fixture", Now);
        Assert.False(SyntheticStagingOwnershipPolicy.MayAttemptCleanup(
            manifest, layout with { RelativeProbeRoot = "probes/other" }, Receipt(manifest, layout), Now));
    }

    [Fact]
    public void ForgedInputTraversalIsRejected()
    {
        var manifest = Manifest();
        var layout = SyntheticFixtureLayoutBuilder.Build(manifest, "fixture", Now);
        Assert.False(SyntheticStagingOwnershipPolicy.MayAttemptCleanup(
            manifest, layout with { RelativeInputFile = layout.RelativeInputDirectory + "/../output" },
            Receipt(manifest, layout), Now));
    }

    [Fact]
    public void MissingOrInvalidOwnershipTokenIsRejected()
    {
        var manifest = Manifest();
        var layout = SyntheticFixtureLayoutBuilder.Build(manifest, "fixture", Now);
        Assert.False(SyntheticStagingOwnershipPolicy.MayAttemptCleanup(manifest, layout, null, Now));
        Assert.False(SyntheticStagingOwnershipPolicy.MayAttemptCleanup(
            manifest, layout, Receipt(manifest, layout) with { OwnershipToken = "invalid" }, Now));
    }

    [Fact]
    public void ExpiredManifestIsRejected()
    {
        var manifest = Manifest();
        var layout = SyntheticFixtureLayoutBuilder.Build(manifest, "fixture", Now);
        Assert.False(SyntheticStagingOwnershipPolicy.MayAttemptCleanup(
            manifest, layout, Receipt(manifest, layout), manifest.ExpiresAtUtc));
    }
}
