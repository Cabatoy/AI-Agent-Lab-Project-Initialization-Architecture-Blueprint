using System.Text.Json;
using Xunit;

public sealed class SyntheticProbeTaskEnvelopeTests
{
    private static SandboxProbeManifest Manifest(DateTimeOffset now) =>
        RootlessPodmanSandboxPreflight.CreateManifest(
            "sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64),
            new SandboxResourceLimits(50000, 100000, 134217728, 32, 10, 65536),
            now.AddMinutes(-1), now.AddMinutes(1));

    private static SyntheticProbeStagingResult Staging(SandboxProbeManifest manifest, bool verified) =>
        new(manifest.ProbeId, manifest.ManifestDigest, verified, false,
            verified ? "staged-input-verified" : "staged-input-unverified");

    [Fact]
    public void VerifiedStagingIsCorrelatedToTaskWithoutExecutionAuthorization()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = Manifest(now);
        var taskId = Guid.NewGuid();
        var envelope = SyntheticProbeTaskEnvelopeBuilder.Build(taskId, Staging(manifest, true), now);
        Assert.True(SyntheticProbeTaskEnvelopeBuilder.MatchesTaskAndManifest(envelope, taskId, manifest));
        Assert.Equal("staging-verified", envelope.State);
        Assert.False(envelope.RuntimeIsolationVerified);
        Assert.False(envelope.AgentExecutionAllowed);
        Assert.Equal(taskId, envelope.TaskId);
    }

    [Fact]
    public void BlockedStagingRetainsExplicitBlockingReasons()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = Manifest(now);
        var taskId = Guid.NewGuid();
        var envelope = SyntheticProbeTaskEnvelopeBuilder.Build(taskId, Staging(manifest, false), now);
        Assert.True(SyntheticProbeTaskEnvelopeBuilder.MatchesTaskAndManifest(envelope, taskId, manifest));
        Assert.Equal("staging-blocked", envelope.State);
        Assert.Contains("staged-input-unverified", envelope.BlockingReasons);
    }

    [Fact]
    public void ForeignTaskAndManifestCannotReuseObservation()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = Manifest(now);
        var taskId = Guid.NewGuid();
        var envelope = SyntheticProbeTaskEnvelopeBuilder.Build(taskId, Staging(manifest, true), now);
        Assert.False(SyntheticProbeTaskEnvelopeBuilder.MatchesTaskAndManifest(envelope, Guid.NewGuid(), manifest));
        Assert.False(SyntheticProbeTaskEnvelopeBuilder.MatchesTaskAndManifest(envelope, taskId, Manifest(now)));
    }

    [Fact]
    public void ForgedRuntimePermissionAndInconsistentStateAreRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = Manifest(now);
        var taskId = Guid.NewGuid();
        var envelope = SyntheticProbeTaskEnvelopeBuilder.Build(taskId, Staging(manifest, true), now);
        Assert.False(SyntheticProbeTaskEnvelopeBuilder.MatchesTaskAndManifest(
            envelope with { AgentExecutionAllowed = true }, taskId, manifest));
        Assert.False(SyntheticProbeTaskEnvelopeBuilder.MatchesTaskAndManifest(
            envelope with { RuntimeIsolationVerified = true }, taskId, manifest));
        Assert.False(SyntheticProbeTaskEnvelopeBuilder.MatchesTaskAndManifest(
            envelope with { State = "execution-authorized" }, taskId, manifest));
        Assert.Throws<InvalidOperationException>(() => SyntheticProbeTaskEnvelopeBuilder.Build(
            taskId, Staging(manifest, true) with { AgentExecutionAllowed = true }, now));
    }

    [Fact]
    public void EnvelopeRoundTripsAsReadOnlyJson()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = Manifest(now);
        var taskId = Guid.NewGuid();
        var original = SyntheticProbeTaskEnvelopeBuilder.Build(taskId, Staging(manifest, true), now);
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<SyntheticProbeTaskEnvelope>(json);
        Assert.True(SyntheticProbeTaskEnvelopeBuilder.MatchesTaskAndManifest(restored, taskId, manifest));
        Assert.DoesNotContain("ChallengeNonce", json);
    }
}
