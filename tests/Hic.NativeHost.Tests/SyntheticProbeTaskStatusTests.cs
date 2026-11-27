using System.Text.Json;
using Xunit;

public sealed class SyntheticProbeTaskStatusTests
{
    private static SyntheticProbeStagingResult Result(bool verified) =>
        new(Guid.NewGuid(), "sha256:" + new string('a', 64), verified,
            AgentExecutionAllowed: false,
            Status: verified ? "staged-input-verified" : "staged-input-unverified");

    [Fact]
    public void VerifiedStagingIsStillBlockedFromExecution()
    {
        var source = Result(true);
        var status = SyntheticProbeTaskStatusProjector.FromStagingResult(source);
        Assert.Equal(source.ProbeId, status.ProbeId);
        Assert.Equal(source.ManifestDigest, status.ManifestDigest);
        Assert.Equal("synthetic-staging", status.Phase);
        Assert.Equal("staging-verified", status.State);
        Assert.True(status.InputVerified);
        Assert.False(status.RuntimeIsolationVerified);
        Assert.False(status.AgentExecutionAllowed);
        Assert.Contains("runtime-isolation-unverified", status.BlockingReasons);
        Assert.Contains("agent-execution-disabled", status.BlockingReasons);
    }

    [Fact]
    public void UnverifiedStagingReportsBlockingReason()
    {
        var status = SyntheticProbeTaskStatusProjector.FromStagingResult(Result(false));
        Assert.Equal("staging-blocked", status.State);
        Assert.False(status.InputVerified);
        Assert.Contains("staged-input-unverified", status.BlockingReasons);
        Assert.False(status.AgentExecutionAllowed);
    }

    [Fact]
    public void RejectsForgedExecutionPermissionAndInconsistentStatus()
    {
        var source = Result(true);
        Assert.Throws<InvalidOperationException>(() =>
            SyntheticProbeTaskStatusProjector.FromStagingResult(
                source with { AgentExecutionAllowed = true }));
        Assert.Throws<InvalidOperationException>(() =>
            SyntheticProbeTaskStatusProjector.FromStagingResult(
                source with { Status = "execution-authorized" }));
        Assert.Throws<InvalidOperationException>(() =>
            SyntheticProbeTaskStatusProjector.FromStagingResult(
                source with { InputVerified = false }));
    }

    [Fact]
    public void StatusCanBeSerializedWithoutLeakingFixtureBytes()
    {
        var status = SyntheticProbeTaskStatusProjector.FromStagingResult(Result(true));
        var json = JsonSerializer.Serialize(status);
        Assert.Contains("runtime-isolation-unverified", json);
        Assert.Contains("AgentExecutionAllowed", json);
        Assert.DoesNotContain("fixture", json, StringComparison.OrdinalIgnoreCase);
    }
}
