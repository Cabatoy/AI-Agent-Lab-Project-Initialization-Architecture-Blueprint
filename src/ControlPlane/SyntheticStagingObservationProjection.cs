using System.Text.Json;

/// <summary>
/// Untrusted, read-only projection of a NativeHost staging envelope.
/// Does not modify tasks, queue work or establish sandbox isolation.
/// NativeHost and Control Plane intentionally share only a JSON wire contract.
/// </summary>
internal sealed record SyntheticStagingObservation(
    Guid TaskId, Guid ProbeId, string ManifestDigest, string Phase, string State,
    bool InputVerified, bool RuntimeIsolationVerified, bool AgentExecutionAllowed,
    IReadOnlyList<string>? BlockingReasons, DateTimeOffset ObservedAtUtc);

internal sealed record SyntheticStagingTaskView(
    Guid TaskId, Guid ProbeId, string State, bool InputVerified,
    bool AgentExecutionAllowed, IReadOnlyList<string> BlockingReasons);

internal static class SyntheticStagingObservationProjection
{
    private static bool IsSha256Digest(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static SyntheticStagingTaskView? TryRead(
        string? json, Guid expectedTaskId, Guid expectedProbeId, string expectedManifestDigest)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8192 ||
            expectedTaskId == Guid.Empty || expectedProbeId == Guid.Empty ||
            !IsSha256Digest(expectedManifestDigest)) return null;

        try
        {
            var observation = JsonSerializer.Deserialize<SyntheticStagingObservation>(json);
            if (observation is null ||
                observation.TaskId != expectedTaskId ||
                observation.ProbeId != expectedProbeId ||
                !string.Equals(observation.ManifestDigest, expectedManifestDigest, StringComparison.Ordinal) ||
                observation.Phase != "synthetic-staging" ||
                observation.State is not ("staging-verified" or "staging-blocked") ||
                observation.InputVerified != (observation.State == "staging-verified") ||
                observation.RuntimeIsolationVerified || observation.AgentExecutionAllowed ||
                observation.ObservedAtUtc.Offset != TimeSpan.Zero ||
                observation.BlockingReasons is null || observation.BlockingReasons.Count is < 2 or > 16 ||
                observation.BlockingReasons.Any(reason => string.IsNullOrWhiteSpace(reason) || reason.Length > 128) ||
                !observation.BlockingReasons.Contains("runtime-isolation-unverified") ||
                !observation.BlockingReasons.Contains("agent-execution-disabled") ||
                (!observation.InputVerified &&
                    !observation.BlockingReasons.Contains("staged-input-unverified")))
                return null;

            return new SyntheticStagingTaskView(
                observation.TaskId, observation.ProbeId, observation.State,
                observation.InputVerified, AgentExecutionAllowed: false,
                observation.BlockingReasons.ToArray());
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}
