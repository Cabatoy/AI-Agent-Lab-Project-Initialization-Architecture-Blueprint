using Microsoft.Win32.SafeHandles;

/// <summary>
/// Single-call, offline-only synthetic staging lifecycle. Never runs Podman,
/// executes agents, cleans files, or emits trusted sandbox evidence.
/// Caller owns and must dispose the returned creation receipt.
/// </summary>
internal sealed record SyntheticProbeStagingResult(
    Guid ProbeId, string ManifestDigest, bool InputVerified,
    bool AgentExecutionAllowed, string Status);

internal static class SyntheticProbeStagingLifecycle
{
    internal static (SyntheticProbeStagingResult Result,
        LinuxSyntheticStagingRoot.CreatedProbeReceipt Receipt) PrepareAndVerify(
        SafeFileHandle trustedParent,
        SandboxProbeManifest manifest,
        ReadOnlySpan<byte> fixture,
        int maximumBytes,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (maximumBytes < 0 || fixture.Length > maximumBytes)
            throw new ArgumentException("Fixture exceeds staging limit.", nameof(fixture));

        // Prevalidate the immutable input digest before touching the filesystem.
        var digest = "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(fixture)).ToLowerInvariant();
        if (!string.Equals(digest, manifest.StagedInputDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("Fixture does not match manifest.");

        var receipt = LinuxSyntheticStagingRoot.CreateManifestProbeWithReceipt(
            trustedParent, manifest, nowUtc);
        try
        {
            receipt.StageManifestInput(manifest, fixture, maximumBytes, nowUtc);
            var verified = receipt.VerifyStagedInput(manifest, maximumBytes, nowUtc);
            return (new SyntheticProbeStagingResult(
                manifest.ProbeId, manifest.ManifestDigest, verified,
                AgentExecutionAllowed: false,
                Status: verified ? "staged-input-verified" : "staged-input-unverified"), receipt);
        }
        catch
        {
            // Do not perform unsafe cleanup of partially staged data.
            receipt.Dispose();
            throw;
        }
    }
}
