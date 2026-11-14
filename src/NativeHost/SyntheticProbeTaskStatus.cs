/// <summary>
/// Safe, serializable task-status projection for the offline staging phase.
/// This is not sandbox runtime evidence or permission to execute agents.
/// </summary>
internal sealed record SyntheticProbeTaskStatus(
    Guid ProbeId,
    string ManifestDigest,
    string Phase,
    string State,
    bool InputVerified,
    bool RuntimeIsolationVerified,
    bool AgentExecutionAllowed,
    IReadOnlyList<string> BlockingReasons);

internal static class SyntheticProbeTaskStatusProjector
{
    internal static SyntheticProbeTaskStatus FromStagingResult(SyntheticProbeStagingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ProbeId == Guid.Empty ||
            string.IsNullOrWhiteSpace(result.ManifestDigest) ||
            result.AgentExecutionAllowed ||
            result.Status is not ("staged-input-verified" or "staged-input-unverified") ||
            result.InputVerified != (result.Status == "staged-input-verified"))
            throw new InvalidOperationException("Invalid offline staging result.");

        return new SyntheticProbeTaskStatus(
            result.ProbeId,
            result.ManifestDigest,
            Phase: "synthetic-staging",
            State: result.InputVerified ? "staging-verified" : "staging-blocked",
            InputVerified: result.InputVerified,
            RuntimeIsolationVerified: false,
            AgentExecutionAllowed: false,
            BlockingReasons: result.InputVerified
                ? new[] { "runtime-isolation-unverified", "agent-execution-disabled" }
                : new[] { "staged-input-unverified", "runtime-isolation-unverified", "agent-execution-disabled" });
    }
}
