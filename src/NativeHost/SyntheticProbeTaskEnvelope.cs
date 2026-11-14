/// <summary>
/// Read-only correlation envelope for transporting an offline staging observation
/// alongside a Control Plane task. Never mutates task state, claims execution,
/// or promotes staging verification to runtime isolation evidence.
/// </summary>
internal sealed record SyntheticProbeTaskEnvelope(
    Guid TaskId,
    Guid ProbeId,
    string ManifestDigest,
    string Phase,
    string State,
    bool InputVerified,
    bool RuntimeIsolationVerified,
    bool AgentExecutionAllowed,
    IReadOnlyList<string> BlockingReasons,
    DateTimeOffset ObservedAtUtc);

internal static class SyntheticProbeTaskEnvelopeBuilder
{
    internal static SyntheticProbeTaskEnvelope Build(
        Guid taskId, SyntheticProbeStagingResult staging, DateTimeOffset observedAtUtc)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("Task ID is required.", nameof(taskId));
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Observation time must be UTC.", nameof(observedAtUtc));
        var status = SyntheticProbeTaskStatusProjector.FromStagingResult(staging);
        return new SyntheticProbeTaskEnvelope(
            taskId, status.ProbeId, status.ManifestDigest, status.Phase, status.State,
            status.InputVerified, status.RuntimeIsolationVerified,
            AgentExecutionAllowed: false, status.BlockingReasons.ToArray(), observedAtUtc);
    }

    internal static bool MatchesTaskAndManifest(
        SyntheticProbeTaskEnvelope? envelope, Guid taskId, SandboxProbeManifest? manifest)
    {
        if (envelope is null || manifest is null || taskId == Guid.Empty)
            return false;
        return envelope.TaskId == taskId &&
            envelope.ProbeId == manifest.ProbeId &&
            string.Equals(envelope.ManifestDigest, manifest.ManifestDigest, StringComparison.Ordinal) &&
            envelope.Phase == "synthetic-staging" &&
            envelope.State is ("staging-verified" or "staging-blocked") &&
            envelope.InputVerified == (envelope.State == "staging-verified") &&
            !envelope.RuntimeIsolationVerified &&
            !envelope.AgentExecutionAllowed &&
            envelope.ObservedAtUtc.Offset == TimeSpan.Zero &&
            envelope.BlockingReasons is not null &&
            envelope.BlockingReasons.Contains("runtime-isolation-unverified") &&
            envelope.BlockingReasons.Contains("agent-execution-disabled");
    }
}
