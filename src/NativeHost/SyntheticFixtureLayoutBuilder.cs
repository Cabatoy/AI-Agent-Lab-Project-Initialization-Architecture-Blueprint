using System.Text.RegularExpressions;

/// <summary>
/// Offline-only staging layout. No directory creation, host mount, container or artifact collection.
/// A caller MUST separately verify every filesystem component is not a symlink and
/// perform race-safe directory operations before using these paths.
/// </summary>
internal sealed record SyntheticFixtureLayout(
    string RelativeProbeRoot,
    string RelativeInputDirectory,
    string RelativeOutputDirectory,
    string RelativeInputFile);

internal static class SyntheticFixtureLayoutBuilder
{
    private static readonly Regex SafeName = new(
        "^[a-z0-9][a-z0-9_-]{0,63}$",
        RegexOptions.CultureInvariant);

    internal static SyntheticFixtureLayout Build(
        SandboxProbeManifest manifest,
        string fixtureFileName,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var report = RootlessPodmanSandboxPreflight.Evaluate(manifest, null, null, nowUtc);
        if (report.SanitizedReasons.Count != 1 ||
            report.SanitizedReasons[0] != "runtime-evidence-unavailable" ||
            report.AgentCodeExecutionAllowed)
            throw new InvalidOperationException("Synthetic fixture manifest rejected.");

        if (fixtureFileName is null || !SafeName.IsMatch(fixtureFileName) ||
            fixtureFileName is "." or "..")
            throw new ArgumentException("Invalid synthetic fixture name.", nameof(fixtureFileName));

        if (manifest.WritableOutputRoot != RootlessPodmanSandboxPreflight.WritableOutputRoot)
            throw new InvalidOperationException("Synthetic output root rejected.");

        var root = "probes/" + manifest.ProbeId.ToString("N");
        return new SyntheticFixtureLayout(
            root,
            root + "/input",
            root + "/output",
            root + "/input/" + fixtureFileName);
    }
}
