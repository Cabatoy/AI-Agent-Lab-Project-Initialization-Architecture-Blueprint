using System.Text.Json;
using Xunit;

public sealed class SyntheticStagingObservationProjectionTests
{
    private static (Guid Task, Guid Probe, string Digest, SyntheticStagingObservation Observation) Valid()
    {
        var task = Guid.NewGuid();
        var probe = Guid.NewGuid();
        var digest = "sha256:" + new string('a', 64);
        return (task, probe, digest, new SyntheticStagingObservation(
            task, probe, digest, "synthetic-staging", "staging-verified",
            true, false, false,
            new[] { "runtime-isolation-unverified", "agent-execution-disabled" },
            DateTimeOffset.UtcNow));
    }

    private static SyntheticStagingTaskView? Read(
        SyntheticStagingObservation observation, Guid task, Guid probe, string digest) =>
        SyntheticStagingObservationProjection.TryRead(
            JsonSerializer.Serialize(observation), task, probe, digest);

    [Fact]
    public void ValidObservationProjectsReadOnlyBlockedExecution()
    {
        var (task, probe, digest, observation) = Valid();
        var view = Read(observation, task, probe, digest);
        Assert.NotNull(view);
        Assert.Equal("staging-verified", view.State);
        Assert.True(view.InputVerified);
        Assert.False(view.AgentExecutionAllowed);
    }

    [Fact]
    public void ForeignTaskProbeAndManifestAreRejected()
    {
        var (task, probe, digest, observation) = Valid();
        Assert.Null(Read(observation, Guid.NewGuid(), probe, digest));
        Assert.Null(Read(observation, task, Guid.NewGuid(), digest));
        Assert.Null(Read(observation, task, probe, "sha256:" + new string('b', 64)));
    }

    [Fact]
    public void ForgedExecutionAndRuntimeEvidenceAreRejected()
    {
        var (task, probe, digest, observation) = Valid();
        Assert.Null(Read(observation with { AgentExecutionAllowed = true }, task, probe, digest));
        Assert.Null(Read(observation with { RuntimeIsolationVerified = true }, task, probe, digest));
        Assert.Null(Read(observation with { State = "execution-authorized" }, task, probe, digest));
    }

    [Fact]
    public void MalformedOversizedAndContradictoryObservationsAreRejected()
    {
        var (task, probe, digest, observation) = Valid();
        Assert.Null(SyntheticStagingObservationProjection.TryRead("{", task, probe, digest));
        Assert.Null(SyntheticStagingObservationProjection.TryRead(new string('x', 8193), task, probe, digest));
        Assert.Null(Read(observation with { InputVerified = false }, task, probe, digest));
        Assert.Null(Read(observation with { BlockingReasons = Array.Empty<string>() }, task, probe, digest));
    }

    [Fact]
    public void BlockedStagingRequiresExplicitInputReason()
    {
        var (task, probe, digest, observation) = Valid();
        var blocked = observation with { State = "staging-blocked", InputVerified = false };
        Assert.Null(Read(blocked, task, probe, digest));
        blocked = blocked with { BlockingReasons = new[]
            { "staged-input-unverified", "runtime-isolation-unverified", "agent-execution-disabled" } };
        var view = Read(blocked, task, probe, digest);
        Assert.NotNull(view);
        Assert.Equal("staging-blocked", view.State);
        Assert.False(view.AgentExecutionAllowed);
    }
    [Fact]
    public void InvalidManifestDigestAndMalformedBlockingReasonsAreRejected()
    {
        var (task, probe, digest, observation) = Valid();
        Assert.Null(Read(observation, task, probe, "sha256:" + new string('g', 64)));
        Assert.Null(Read(observation, task, probe, "sha256:" + new string('A', 64)));
        Assert.Null(Read(observation, task, probe, "sha256:" + new string('a', 63)));
        Assert.Null(Read(observation with { BlockingReasons = new[]
            { "runtime-isolation-unverified", "agent-execution-disabled", "" } }, task, probe, digest));
        Assert.Null(Read(observation with { BlockingReasons = new[]
            { "runtime-isolation-unverified", "agent-execution-disabled", new string('x', 129) } }, task, probe, digest));
    }
}
