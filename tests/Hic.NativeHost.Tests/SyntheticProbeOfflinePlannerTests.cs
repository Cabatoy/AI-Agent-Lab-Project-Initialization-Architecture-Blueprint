using Xunit;

public sealed class SyntheticProbeOfflinePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);

    private static SandboxProbeManifest Manifest() =>
        RootlessPodmanSandboxPreflight.CreateManifest(
            "sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64),
            new SandboxResourceLimits(50000, 100000, 134217728, 32, 10, 65536),
            Now.AddMinutes(-1), Now.AddMinutes(2));

    private static SyntheticProbeAuthorization Approval(SandboxProbeManifest manifest) =>
        new(manifest.ProbeId, manifest.ManifestDigest, manifest.ExpiresAtUtc, true);

    [Fact]
    public void ComposesOfflinePlanWithoutExecutingAnything()
    {
        var manifest = Manifest();
        var plan = new SyntheticProbeOfflinePlanner().Prepare(
            manifest, Approval(manifest), "fixture", new string('a', 64), Now);
        Assert.Equal(manifest.ProbeId, plan.ProbeId);
        Assert.Equal(manifest.ManifestDigest, plan.ManifestDigest);
        Assert.Equal("/usr/bin/podman", plan.Command.Executable);
        Assert.Equal("probes/" + manifest.ProbeId.ToString("N"), plan.Layout.RelativeProbeRoot);
        Assert.Equal(plan.Layout.RelativeProbeRoot, plan.Ownership.RelativeProbeRoot);
        Assert.Contains("--network=none", plan.Command.Arguments);
        Assert.False(manifest.AgentCodeExecutionAllowed);
    }

    [Fact]
    public void ReusingAuthorizationOnSamePlannerIsRejected()
    {
        var manifest = Manifest();
        var planner = new SyntheticProbeOfflinePlanner();
        planner.Prepare(manifest, Approval(manifest), "fixture", new string('a', 64), Now);
        Assert.Throws<InvalidOperationException>(() =>
            planner.Prepare(manifest, Approval(manifest), "fixture", new string('a', 64), Now));
    }

    [Fact]
    public void InvalidFixtureDoesNotConsumeAuthorization()
    {
        var manifest = Manifest();
        var planner = new SyntheticProbeOfflinePlanner();
        Assert.Throws<ArgumentException>(() =>
            planner.Prepare(manifest, Approval(manifest), "../escape", new string('a', 64), Now));
        var plan = planner.Prepare(manifest, Approval(manifest), "fixture", new string('a', 64), Now);
        Assert.Equal(manifest.ProbeId, plan.ProbeId);
    }

    [Fact]
    public void InvalidOwnershipDoesNotConsumeAuthorization()
    {
        var manifest = Manifest();
        var planner = new SyntheticProbeOfflinePlanner();
        Assert.Throws<InvalidOperationException>(() =>
            planner.Prepare(manifest, Approval(manifest), "fixture", "bad", Now));
        Assert.Equal(manifest.ProbeId, planner.Prepare(
            manifest, Approval(manifest), "fixture", new string('a', 64), Now).ProbeId);
    }

    [Fact]
    public void MismatchedApprovalIsRejected()
    {
        var manifest = Manifest();
        var planner = new SyntheticProbeOfflinePlanner();
        Assert.Throws<InvalidOperationException>(() =>
            planner.Prepare(manifest, Approval(manifest) with { ProbeId = Guid.NewGuid() },
                "fixture", new string('a', 64), Now));
    }

    [Fact]
    public void ExpiredManifestIsRejected()
    {
        var manifest = Manifest();
        Assert.Throws<InvalidOperationException>(() =>
            new SyntheticProbeOfflinePlanner().Prepare(
                manifest, Approval(manifest), "fixture", new string('a', 64), manifest.ExpiresAtUtc));
    }
}
