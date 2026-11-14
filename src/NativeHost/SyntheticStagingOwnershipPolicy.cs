using System.Text.RegularExpressions;

/// <summary>
/// Pure ownership receipt validator for future exact-owned staging cleanup.
/// Does not touch the filesystem or attest that a path is free of symlinks.
/// </summary>
internal sealed record SyntheticStagingOwnership(
    Guid ProbeId,
    string ManifestDigest,
    string RelativeProbeRoot,
    string OwnershipToken);

internal static class SyntheticStagingOwnershipPolicy
{
    private static readonly Regex TokenPattern = new(
        "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    internal static bool MayAttemptCleanup(
        SandboxProbeManifest? manifest,
        SyntheticFixtureLayout? layout,
        SyntheticStagingOwnership? receipt,
        DateTimeOffset nowUtc)
    {
        if (manifest is null || layout is null || receipt is null ||
            manifest.ProbeId == Guid.Empty ||
            receipt.ProbeId != manifest.ProbeId ||
            receipt.ManifestDigest != manifest.ManifestDigest ||
            receipt.OwnershipToken is null ||
            !TokenPattern.IsMatch(receipt.OwnershipToken) ||
            nowUtc.Offset != TimeSpan.Zero)
            return false;

        var preflight = RootlessPodmanSandboxPreflight.Evaluate(manifest, null, null, nowUtc);
        if (preflight.SanitizedReasons.Count != 1 ||
            preflight.SanitizedReasons[0] != "runtime-evidence-unavailable" ||
            preflight.AgentCodeExecutionAllowed)
            return false;

        var expectedRoot = "probes/" + manifest.ProbeId.ToString("N");
        return layout.RelativeProbeRoot == expectedRoot &&
            layout.RelativeInputDirectory == expectedRoot + "/input" &&
            layout.RelativeOutputDirectory == expectedRoot + "/output" &&
            layout.RelativeInputFile.StartsWith(expectedRoot + "/input/", StringComparison.Ordinal) &&
            receipt.RelativeProbeRoot == expectedRoot;
    }
}
