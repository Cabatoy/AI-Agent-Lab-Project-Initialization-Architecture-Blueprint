using System.Globalization;

/// <summary>
/// Pure, offline command specification for a synthetic fixture.
/// Does not launch Podman, stage files, collect evidence or authorize execution.
/// In particular, no host paths, sockets or environment variables are accepted.
/// </summary>
internal sealed record SyntheticPodmanCommand(string Executable, IReadOnlyList<string> Arguments);

internal static class SyntheticPodmanCommandBuilder
{
    internal static SyntheticPodmanCommand Build(SandboxProbeManifest manifest, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        // Validate the canonical manifest through the existing fail-closed contract.
        // Missing runtime evidence is the expected result for a valid offline manifest.
        var report = RootlessPodmanSandboxPreflight.Evaluate(manifest, null, null, nowUtc);
        if (report.SanitizedReasons.Count != 1 ||
            report.SanitizedReasons[0] != "runtime-evidence-unavailable" ||
            report.AgentCodeExecutionAllowed)
            throw new InvalidOperationException("Synthetic probe manifest rejected.");

        // Immutable image reference: only digest, never a mutable tag.
        var args = new[]
        {
            "run",
            "--rm",
            "--pull=never",
            "--network=none",
            "--read-only",
            "--cap-drop=ALL",
            "--security-opt=no-new-privileges",
            "--userns=private",
            "--user=65534:65534",
            "--pids-limit=" + manifest.Limits.Pids.ToString(CultureInfo.InvariantCulture),
            "--memory=" + manifest.Limits.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            "--cpu-period=" + manifest.Limits.CpuPeriodMicros.ToString(CultureInfo.InvariantCulture),
            "--cpu-quota=" + manifest.Limits.CpuQuotaMicros.ToString(CultureInfo.InvariantCulture),
            "--",
            "localhost/hic-synthetic-fixture@" + manifest.ImageDigest,
            "/bin/true"
        };

        // This specification deliberately has no mounts, no writable output,
        // no input fixture, and no runtime evidence. It is not yet a usable probe.
        return new SyntheticPodmanCommand("/usr/bin/podman", Array.AsReadOnly(args));
    }
}
