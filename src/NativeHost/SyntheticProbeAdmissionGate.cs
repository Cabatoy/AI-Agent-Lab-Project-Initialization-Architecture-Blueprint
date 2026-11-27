using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

internal sealed record SyntheticProbeAuthorization(
    Guid ProbeId,
    string ManifestDigest,
    DateTimeOffset ExpiresAtUtc,
    bool OperatorApproved);

/// <summary>
/// An offline admission gate for a future synthetic-only probe. No process or container is launched.
/// Authorization is exact-manifest scoped and single-use within this gate instance.
/// This is NOT an execution authorization or an independently trusted verifier.
/// </summary>
internal sealed class SyntheticProbeAdmissionGate
{
    private readonly ConcurrentDictionary<Guid, byte> consumed = new();

    internal bool TryConsume(
        SandboxProbeManifest? manifest,
        SyntheticProbeAuthorization? authorization,
        DateTimeOffset nowUtc)
    {
        if (manifest is null || authorization is null ||
            !authorization.OperatorApproved ||
            manifest.AgentCodeExecutionAllowed ||
            manifest.ContractVersion != RootlessPodmanSandboxPreflight.ContractVersion ||
            manifest.ProbeId == Guid.Empty ||
            authorization.ProbeId != manifest.ProbeId ||
            nowUtc.Offset != TimeSpan.Zero ||
            manifest.IssuedAtUtc.Offset != TimeSpan.Zero ||
            manifest.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            authorization.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            nowUtc < manifest.IssuedAtUtc ||
            nowUtc >= manifest.ExpiresAtUtc ||
            nowUtc >= authorization.ExpiresAtUtc ||
            authorization.ExpiresAtUtc > manifest.ExpiresAtUtc ||
            manifest.ManifestDigest is null ||
            authorization.ManifestDigest is null)
            return false;

        var expected = Encoding.UTF8.GetBytes(manifest.ManifestDigest);
        var actual = Encoding.UTF8.GetBytes(authorization.ManifestDigest);
        if (expected.Length != actual.Length ||
            !CryptographicOperations.FixedTimeEquals(expected, actual))
            return false;

        // Existing preflight contract performs canonical digest, policy and limit validation.
        // A null evidence/verifier always returns unverified, even for a valid manifest.
        var preflight = RootlessPodmanSandboxPreflight.Evaluate(manifest, null, null, nowUtc);
        if (!preflight.SanitizedReasons.Contains("runtime-evidence-unavailable"))
            return false;

        return consumed.TryAdd(manifest.ProbeId, 0);
    }
}
