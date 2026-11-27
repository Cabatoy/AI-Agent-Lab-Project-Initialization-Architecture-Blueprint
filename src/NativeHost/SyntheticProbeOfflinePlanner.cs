/// <summary>
/// Offline-only coordinator that binds admission, command specification, staging layout
/// and cleanup policy into one fail-closed plan. It never invokes Podman or filesystem APIs.
/// IMPORTANT: An in-memory ownership receipt is NOT proof of filesystem ownership.
/// </summary>
internal sealed record SyntheticProbeOfflinePlan(
    Guid ProbeId,
    string ManifestDigest,
    SyntheticPodmanCommand Command,
    SyntheticFixtureLayout Layout,
    SyntheticStagingOwnership Ownership);

internal sealed class SyntheticProbeOfflinePlanner
{
    private readonly SyntheticProbeAdmissionGate admission = new();

    internal SyntheticProbeOfflinePlan Prepare(
        SandboxProbeManifest manifest,
        SyntheticProbeAuthorization authorization,
        string fixtureName,
        string ownershipToken,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        // Validate all pure inputs before consuming one-use authorization.
        var command = SyntheticPodmanCommandBuilder.Build(manifest, nowUtc);
        var layout = SyntheticFixtureLayoutBuilder.Build(manifest, fixtureName, nowUtc);
        var ownership = new SyntheticStagingOwnership(
            manifest.ProbeId, manifest.ManifestDigest, layout.RelativeProbeRoot, ownershipToken);
        if (!SyntheticStagingOwnershipPolicy.MayAttemptCleanup(manifest, layout, ownership, nowUtc))
            throw new InvalidOperationException("Synthetic staging ownership rejected.");

        if (!admission.TryConsume(manifest, authorization, nowUtc))
            throw new InvalidOperationException("Synthetic probe authorization rejected or already consumed.");

        return new SyntheticProbeOfflinePlan(
            manifest.ProbeId, manifest.ManifestDigest, command, layout, ownership);
    }
}
