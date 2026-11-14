using Xunit;

public sealed class SyntheticProbeAdmissionGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);

    private static SandboxProbeManifest Manifest() =>
        RootlessPodmanSandboxPreflight.CreateManifest(
            "sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64),
            new SandboxResourceLimits(50000, 100000, 134217728, 32, 10, 65536),
            Now.AddMinutes(-1),
            Now.AddMinutes(2));

    private static SyntheticProbeAuthorization Authorization(SandboxProbeManifest manifest) =>
        new(manifest.ProbeId, manifest.ManifestDigest, manifest.ExpiresAtUtc, true);

    [Fact]
    public void ExactAuthorizationIsSingleUse()
    {
        var manifest = Manifest();
        var gate = new SyntheticProbeAdmissionGate();
        Assert.True(gate.TryConsume(manifest, Authorization(manifest), Now));
        Assert.False(gate.TryConsume(manifest, Authorization(manifest), Now));
    }

    [Fact]
    public void MissingApprovalFailsClosed()
    {
        var manifest = Manifest();
        var gate = new SyntheticProbeAdmissionGate();
        Assert.False(gate.TryConsume(manifest, null, Now));
        Assert.False(gate.TryConsume(manifest, Authorization(manifest) with { OperatorApproved = false }, Now));
        Assert.True(gate.TryConsume(manifest, Authorization(manifest), Now));
    }

    [Fact]
    public void WrongProbeAndDigestFailClosedWithoutConsuming()
    {
        var manifest = Manifest();
        var gate = new SyntheticProbeAdmissionGate();
        Assert.False(gate.TryConsume(manifest, Authorization(manifest) with { ProbeId = Guid.NewGuid() }, Now));
        Assert.False(gate.TryConsume(manifest, Authorization(manifest) with { ManifestDigest = "sha256:" + new string('0', 64) }, Now));
        Assert.True(gate.TryConsume(manifest, Authorization(manifest), Now));
    }

    [Fact]
    public void ExpiredOrAlteredManifestFailsClosed()
    {
        var manifest = Manifest();
        var gate = new SyntheticProbeAdmissionGate();
        Assert.False(gate.TryConsume(manifest, Authorization(manifest), manifest.ExpiresAtUtc));
        Assert.False(gate.TryConsume(manifest with { AgentCodeExecutionAllowed = true }, Authorization(manifest), Now));
        Assert.False(gate.TryConsume(manifest with { Limits = manifest.Limits with { Pids = 999 } }, Authorization(manifest), Now));
        Assert.False(gate.TryConsume(manifest, Authorization(manifest) with { ExpiresAtUtc = Now.AddSeconds(-1) }, Now));
        Assert.True(gate.TryConsume(manifest, Authorization(manifest), Now));
    }

    [Fact]
    public void GateDoesNotAuthorizeAgentExecution()
    {
        var manifest = Manifest();
        var gate = new SyntheticProbeAdmissionGate();
        Assert.True(gate.TryConsume(manifest, Authorization(manifest), Now));
        var report = RootlessPodmanSandboxPreflight.Evaluate(manifest, null, null, Now);
        Assert.False(report.AgentCodeExecutionAllowed);
        Assert.Equal("unverified", report.Status);
    }
}
