using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

public sealed class RootlessPodmanSandboxPreflightTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 14, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PolicyIsDisabledAndNeverAllowsAgentCodeExecution()
    {
        Assert.False(RootlessPodmanSandboxPreflight.RequiredPolicy.EnabledByDefault);
        Assert.False(RootlessPodmanSandboxPreflight.RequiredPolicy.AgentCodeExecutionEnabled);

        var report = RootlessPodmanSandboxPreflight.Evaluate(Manifest(), null, null, Now);
        Assert.False(report.AgentCodeExecutionAllowed);
    }

    [Fact]
    public void MissingEvidenceAndUnavailableVerifierFailClosed()
    {
        var manifest = Manifest();

        var missing = RootlessPodmanSandboxPreflight.Evaluate(manifest, null, Verifier(), Now);
        Assert.Equal("unverified", missing.Status);
        Assert.Contains("runtime-evidence-unavailable", missing.SanitizedReasons);

        var unavailable = RootlessPodmanSandboxPreflight.Evaluate(
            manifest, CompleteEvidence(manifest), null, Now);
        Assert.Equal("unverified", unavailable.Status);
        Assert.Contains("trusted-verifier-unavailable", unavailable.SanitizedReasons);
    }

    [Theory]
    [InlineData((int)SandboxEvidenceTrust.Rejected, "runtime-evidence-rejected")]
    [InlineData((int)SandboxEvidenceTrust.Replay, "runtime-evidence-replay")]
    public void RejectedOrReplayedEvidenceRemainsUnverifiedAndDeniesExecution(
        int trust,
        string expectedReason)
    {
        var manifest = Manifest();
        var report = RootlessPodmanSandboxPreflight.Evaluate(
            manifest, CompleteEvidence(manifest), Verifier((SandboxEvidenceTrust)trust), Now);

        Assert.Equal("unverified", report.Status);
        Assert.All(report.Checks.Values, state => Assert.Equal("unverified", state));
        Assert.Contains(expectedReason, report.SanitizedReasons);
        Assert.False(report.AgentCodeExecutionAllowed);
    }

    [Fact]
    public void VerifierExceptionReturnsSanitizedUnverifiedReport()
    {
        var manifest = Manifest();
        var report = RootlessPodmanSandboxPreflight.Evaluate(
            manifest, CompleteEvidence(manifest), new ThrowingVerifier(), Now);

        Assert.Equal("unverified", report.Status);
        Assert.All(report.Checks.Values, state => Assert.Equal("unverified", state));
        Assert.Contains("trusted-verifier-error", report.SanitizedReasons);
        Assert.False(report.AgentCodeExecutionAllowed);
        Assert.DoesNotContain("private-verifier-details", JsonSerializer.Serialize(report));
    }

    [Fact]
    public void CompleteFreshEvidenceRemainsUnverifiedWithoutIndependentRuntimeVerification()
    {
        var manifest = Manifest();
        var report = RootlessPodmanSandboxPreflight.Evaluate(
            manifest, CompleteEvidence(manifest), Verifier(), Now);

        Assert.Equal("unverified", report.Status);
        Assert.All(report.Checks.Values, state => Assert.Equal("unverified", state));
        Assert.Contains("independent-runtime-verification-unavailable", report.SanitizedReasons);
        Assert.False(report.AgentCodeExecutionAllowed);
    }

    [Fact]
    public void CliFlagsAloneCannotVerifyIsolation()
    {
        var manifest = Manifest();
        var evidence = CompleteEvidence(manifest);
        var signals = evidence.Signals
            .Where(signal => signal.Check != SandboxCheck.CpuQuotaEnforced)
            .Append(Signal(
                SandboxCheck.CpuQuotaEnforced,
                SandboxEvidenceMethod.CliConfiguration,
                "cpu-quota-requested",
                manifest.ManifestDigest));
        evidence = evidence with { Signals = signals.ToArray() };

        var report = RootlessPodmanSandboxPreflight.Evaluate(manifest, evidence, Verifier(), Now);

        Assert.Equal("unverified", CheckState(report, SandboxCheck.CpuQuotaEnforced));
        Assert.Equal("unverified", report.Status);
    }

    [Fact]
    public void StaleOrMismatchedManifestEvidenceFailsClosed()
    {
        var manifest = Manifest();
        var staleEvidence = CompleteEvidence(manifest) with
        {
            ExpiresAtUtc = Now
        };
        var stale = RootlessPodmanSandboxPreflight.Evaluate(
            manifest, staleEvidence, Verifier(), Now);
        Assert.Equal("unverified", stale.Status);
        Assert.Contains("runtime-evidence-stale-or-mismatched", stale.SanitizedReasons);
        Assert.False(stale.AgentCodeExecutionAllowed);

        var mismatched = RootlessPodmanSandboxPreflight.Evaluate(
            manifest,
            CompleteEvidence(manifest) with { ManifestDigest = Digest('b') },
            Verifier(),
            Now);
        Assert.Equal("unverified", mismatched.Status);
        Assert.Contains("runtime-evidence-stale-or-mismatched", mismatched.SanitizedReasons);
        Assert.False(mismatched.AgentCodeExecutionAllowed);
    }

    [Fact]
    public void StaleOrTamperedManifestIsUnverified()
    {
        var manifest = Manifest();
        var stale = manifest with { ExpiresAtUtc = Now };
        var tampered = manifest with { ImageDigest = Digest('b') };
        var traversingOutput = manifest with { WritableOutputRoot = "../home" };

        Assert.Equal("unverified",
            RootlessPodmanSandboxPreflight.Evaluate(stale, CompleteEvidence(manifest), Verifier(), Now).Status);
        Assert.Equal("unverified",
            RootlessPodmanSandboxPreflight.Evaluate(tampered, CompleteEvidence(manifest), Verifier(), Now).Status);
        Assert.Equal("unverified",
            RootlessPodmanSandboxPreflight.Evaluate(
                traversingOutput, CompleteEvidence(manifest), Verifier(), Now).Status);
    }

    [Theory]
    [InlineData((int)SandboxCheck.StagedInputReadOnly)]
    [InlineData((int)SandboxCheck.WritableOutputRestricted)]
    [InlineData((int)SandboxCheck.HostAndSiblingWorkspacesIsolated)]
    public void ReportedWritableInputOrUnexpectedHostPathsRemainUnverified(int check)
    {
        AssertReportedFailureUnverified((SandboxCheck)check);
    }

    [Theory]
    [InlineData((int)SandboxCheck.CredentialEnvironmentIsolated)]
    [InlineData((int)SandboxCheck.SshCloudCredentialsAndSocketsExcluded)]
    [InlineData((int)SandboxCheck.OutboundNetworkDenied)]
    [InlineData((int)SandboxCheck.DnsDenied)]
    [InlineData((int)SandboxCheck.SymlinkAndTraversalDenied)]
    public void ReportedCredentialNetworkAndPathFailuresRemainUnverified(int check)
    {
        AssertReportedFailureUnverified((SandboxCheck)check);
    }

    [Fact]
    public void DuplicateObservationAndDuplicateAttemptReplayFailClosed()
    {
        var manifest = Manifest();
        var evidence = CompleteEvidence(manifest);
        var duplicate = evidence with
        {
            Signals = evidence.Signals.Append(evidence.Signals[0]).ToArray()
        };

        var report = RootlessPodmanSandboxPreflight.Evaluate(
            manifest, duplicate, Verifier(), Now);

        Assert.Equal("unverified", report.Status);
        Assert.Contains("duplicate-observation", report.SanitizedReasons);
        Assert.False(report.AgentCodeExecutionAllowed);

        var replay = RootlessPodmanSandboxPreflight.Evaluate(
            manifest, CompleteEvidence(manifest), Verifier(SandboxEvidenceTrust.Replay), Now);
        Assert.Equal("unverified", replay.Status);
        Assert.Contains("runtime-evidence-replay", replay.SanitizedReasons);
        Assert.False(replay.AgentCodeExecutionAllowed);
    }

    [Fact]
    public void ContradictoryEvidenceLeavesTheAffectedCheckUnverified()
    {
        var manifest = Manifest();
        var evidence = CompleteEvidence(manifest);
        evidence = evidence with
        {
            Signals = evidence.Signals.Append(Signal(
                SandboxCheck.CpuQuotaEnforced,
                SandboxEvidenceMethod.RuntimeMeasurement,
                "unexpected-cpu-measurement",
                manifest.ManifestDigest)).ToArray()
        };

        var report = RootlessPodmanSandboxPreflight.Evaluate(manifest, evidence, Verifier(), Now);

        Assert.Equal("unverified", report.Status);
        Assert.Equal("unverified", CheckState(report, SandboxCheck.CpuQuotaEnforced));
        Assert.Contains("cpu-quota-enforced-unverified", report.SanitizedReasons);
        Assert.False(report.AgentCodeExecutionAllowed);
    }

    [Theory]
    [InlineData((int)SandboxCheck.CpuQuotaEnforced)]
    [InlineData((int)SandboxCheck.MemoryLimitEnforced)]
    [InlineData((int)SandboxCheck.PidLimitEnforced)]
    public void MissingCgroupEnforcementEvidenceRemainsUnverified(int checkValue)
    {
        var check = (SandboxCheck)checkValue;
        var manifest = Manifest();
        var evidence = CompleteEvidence(manifest) with
        {
            Signals = CompleteEvidence(manifest).Signals
                .Where(signal => signal.Check != check)
                .ToArray()
        };

        var report = RootlessPodmanSandboxPreflight.Evaluate(manifest, evidence, Verifier(), Now);

        Assert.Equal("unverified", report.Status);
        Assert.Equal("unverified", CheckState(report, check));
        Assert.Contains("cgroup-enforcement-unverified", report.SanitizedReasons);
    }

    [Fact]
    public void IncompleteCleanupEvidenceCannotVerifyCleanup()
    {
        var manifest = Manifest();
        var evidence = CompleteEvidence(manifest);
        evidence = evidence with
        {
            Signals = evidence.Signals.Where(signal =>
                signal.Check != SandboxCheck.ExactOwnedBoundedCleanup ||
                signal.Method != SandboxEvidenceMethod.NegativeTest).ToArray()
        };

        var report = RootlessPodmanSandboxPreflight.Evaluate(manifest, evidence, Verifier(), Now);

        Assert.Equal("unverified", CheckState(report, SandboxCheck.ExactOwnedBoundedCleanup));
        Assert.Contains("exact-owned-bounded-cleanup-unverified", report.SanitizedReasons);
    }

    [Fact]
    public void SecurityEvidenceOutputContainsNoPathsOrRawObservationValues()
    {
        var manifest = Manifest();
        var evidence = CompleteEvidence(manifest) with
        {
            Signals = CompleteEvidence(manifest).Signals.Select(signal =>
                signal.Check == SandboxCheck.CredentialEnvironmentIsolated
                    ? signal with
                    {
                        State = SandboxCheckState.Blocked,
                        Measurement = "/home/private/NOT-A-CREDENTIAL"
                    }
                    : signal.Check == SandboxCheck.SshCloudCredentialsAndSocketsExcluded
                        ? signal with { Measurement = "ssh-agent.sock" }
                    : signal).ToArray()
        };

        var report = RootlessPodmanSandboxPreflight.Evaluate(manifest, evidence, Verifier(), Now);
        var serialized = JsonSerializer.Serialize(report);

        Assert.DoesNotContain("/home/private", serialized);
        Assert.DoesNotContain("NOT-A-CREDENTIAL", serialized);
        Assert.DoesNotContain("ssh-agent.sock", serialized);
        Assert.Contains("credential-environment-isolated-unverified", serialized);
    }

    private static void AssertReportedFailureUnverified(SandboxCheck check)
    {
        var manifest = Manifest();
        var evidence = CompleteEvidence(manifest);
        evidence = evidence with
        {
            Signals = evidence.Signals.Select(signal =>
                signal.Check == check ? signal with { State = SandboxCheckState.Blocked } : signal)
                .ToArray()
        };

        var report = RootlessPodmanSandboxPreflight.Evaluate(manifest, evidence, Verifier(), Now);

        Assert.Equal("unverified", CheckState(report, check));
        Assert.Equal("unverified", report.Status);
        Assert.False(report.AgentCodeExecutionAllowed);
    }

    private static SandboxProbeManifest Manifest() =>
        RootlessPodmanSandboxPreflight.CreateManifest(
            Digest('a'),
            Digest('c'),
            new SandboxResourceLimits(
                CpuQuotaMicros: 50_000,
                CpuPeriodMicros: 100_000,
                MemoryBytes: 128 * 1024 * 1024,
                Pids: 32,
                TimeoutSeconds: 30,
                MaxOutputBytes: 1_048_576),
            Now.AddSeconds(-30),
            Now.AddMinutes(2));

    private static SandboxProbeEvidence CompleteEvidence(SandboxProbeManifest manifest)
    {
        var signals = new List<SandboxEvidenceSignal>();
        foreach (var (check, firstMethod, firstCode, secondMethod, secondCode) in RequiredSignals())
        {
            signals.Add(Signal(check, firstMethod, firstCode, manifest.ManifestDigest));
            signals.Add(Signal(check, secondMethod, secondCode, manifest.ManifestDigest));
        }

        return new SandboxProbeEvidence(
            manifest.ProbeId,
            manifest.ChallengeNonce,
            manifest.ImageDigest,
            manifest.ManifestDigest,
            Now.AddSeconds(-3),
            Now.AddMinutes(1),
            signals);
    }

    private static IEnumerable<(SandboxCheck Check, SandboxEvidenceMethod FirstMethod, string FirstCode,
        SandboxEvidenceMethod SecondMethod, string SecondCode)> RequiredSignals()
    {
        yield return (SandboxCheck.RootlessPodmanAvailable, SandboxEvidenceMethod.RuntimeMeasurement, "podman-rootless-observed",
            SandboxEvidenceMethod.NegativeTest, "rootless-runtime-canary-passed");
        yield return (SandboxCheck.UserNamespaceIsolated, SandboxEvidenceMethod.RuntimeMeasurement, "userns-mapping-observed",
            SandboxEvidenceMethod.NegativeTest, "host-userns-unavailable");
        yield return (SandboxCheck.NonRootIdentity, SandboxEvidenceMethod.RuntimeMeasurement, "effective-uid-nonzero",
            SandboxEvidenceMethod.NegativeTest, "root-only-operation-denied");
        yield return (SandboxCheck.CpuQuotaEnforced, SandboxEvidenceMethod.RuntimeMeasurement, "cgroup-cpu-quota-observed",
            SandboxEvidenceMethod.NegativeTest, "cpu-throttling-observed");
        yield return (SandboxCheck.MemoryLimitEnforced, SandboxEvidenceMethod.RuntimeMeasurement, "cgroup-memory-limit-observed",
            SandboxEvidenceMethod.NegativeTest, "container-local-oom-observed");
        yield return (SandboxCheck.PidLimitEnforced, SandboxEvidenceMethod.RuntimeMeasurement, "cgroup-pid-limit-observed",
            SandboxEvidenceMethod.NegativeTest, "pid-limit-denied");
        yield return (SandboxCheck.RootFilesystemReadOnly, SandboxEvidenceMethod.RuntimeMeasurement, "rootfs-readonly-observed",
            SandboxEvidenceMethod.NegativeTest, "rootfs-write-denied");
        yield return (SandboxCheck.StagedInputReadOnly, SandboxEvidenceMethod.RuntimeMeasurement, "input-readonly-observed",
            SandboxEvidenceMethod.NegativeTest, "input-write-denied");
        yield return (SandboxCheck.WritableOutputRestricted, SandboxEvidenceMethod.RuntimeMeasurement, "output-bound-observed",
            SandboxEvidenceMethod.NegativeTest, "write-outside-output-denied");
        yield return (SandboxCheck.CapabilitiesRemoved, SandboxEvidenceMethod.RuntimeMeasurement, "effective-capabilities-empty",
            SandboxEvidenceMethod.NegativeTest, "capability-operation-denied");
        yield return (SandboxCheck.NoNewPrivileges, SandboxEvidenceMethod.RuntimeMeasurement, "no-new-privileges-observed",
            SandboxEvidenceMethod.NegativeTest, "privilege-escalation-denied");
        yield return (SandboxCheck.SeccompEnforced, SandboxEvidenceMethod.RuntimeMeasurement, "seccomp-mode-observed",
            SandboxEvidenceMethod.NegativeTest, "forbidden-syscall-denied");
        yield return (SandboxCheck.NetworkNamespaceIsolated, SandboxEvidenceMethod.RuntimeMeasurement, "network-namespace-isolated",
            SandboxEvidenceMethod.NegativeTest, "host-network-unavailable");
        yield return (SandboxCheck.OutboundNetworkDenied, SandboxEvidenceMethod.RuntimeMeasurement, "egress-policy-observed",
            SandboxEvidenceMethod.NegativeTest, "outbound-connect-denied");
        yield return (SandboxCheck.DnsDenied, SandboxEvidenceMethod.RuntimeMeasurement, "dns-policy-observed",
            SandboxEvidenceMethod.NegativeTest, "dns-query-denied");
        yield return (SandboxCheck.CloudMetadataDenied, SandboxEvidenceMethod.RuntimeMeasurement, "metadata-policy-observed",
            SandboxEvidenceMethod.NegativeTest, "metadata-connect-denied");
        yield return (SandboxCheck.CredentialEnvironmentIsolated, SandboxEvidenceMethod.RuntimeMeasurement, "environment-allowlist-observed",
            SandboxEvidenceMethod.NegativeTest, "credential-canary-absent");
        yield return (SandboxCheck.SshCloudCredentialsAndSocketsExcluded, SandboxEvidenceMethod.RuntimeMeasurement, "mount-allowlist-observed",
            SandboxEvidenceMethod.NegativeTest, "ssh-cloud-socket-canaries-absent");
        yield return (SandboxCheck.HostAndSiblingWorkspacesIsolated, SandboxEvidenceMethod.RuntimeMeasurement, "workspace-mounts-confined",
            SandboxEvidenceMethod.NegativeTest, "host-home-sibling-canaries-absent");
        yield return (SandboxCheck.SymlinkAndTraversalDenied, SandboxEvidenceMethod.RuntimeMeasurement, "path-policy-observed",
            SandboxEvidenceMethod.NegativeTest, "symlink-traversal-denied");
        yield return (SandboxCheck.ExactOwnedBoundedCleanup, SandboxEvidenceMethod.OwnershipReceipt, "exact-owner-removal-within-bound-confirmed",
            SandboxEvidenceMethod.NegativeTest, "unowned-resource-preserved");
        yield return (SandboxCheck.RuntimeTimeoutEnforced, SandboxEvidenceMethod.RuntimeMeasurement, "deadline-policy-observed",
            SandboxEvidenceMethod.NegativeTest, "timeout-termination-observed");
        yield return (SandboxCheck.OutputSizeLimitEnforced, SandboxEvidenceMethod.RuntimeMeasurement, "output-cap-observed",
            SandboxEvidenceMethod.NegativeTest, "output-limit-enforced");
        yield return (SandboxCheck.ImmutableImageAndManifestValidated, SandboxEvidenceMethod.ManifestValidation, "manifest-digest-validated",
            SandboxEvidenceMethod.RuntimeMeasurement, "immutable-image-digest-observed");
    }

    private static SandboxEvidenceSignal Signal(
        SandboxCheck check,
        SandboxEvidenceMethod method,
        string code,
        string manifestDigest) =>
        new(Guid.NewGuid(), check, method, SandboxCheckState.Verified, code,
            Measurement(check, method), manifestDigest, Now.AddSeconds(-5));

    private static string Measurement(SandboxCheck check, SandboxEvidenceMethod method) =>
        (check, method) switch
        {
            (SandboxCheck.CpuQuotaEnforced, SandboxEvidenceMethod.RuntimeMeasurement)
                => "quotaMicros=50000;periodMicros=100000;throttledDelta=2",
            (SandboxCheck.CpuQuotaEnforced, SandboxEvidenceMethod.NegativeTest)
                => "busyLoopThrottled=true",
            (SandboxCheck.MemoryLimitEnforced, SandboxEvidenceMethod.RuntimeMeasurement)
                => "memoryLimitBytes=134217728",
            (SandboxCheck.MemoryLimitEnforced, SandboxEvidenceMethod.NegativeTest)
                => "oomKillDelta=1",
            (SandboxCheck.PidLimitEnforced, SandboxEvidenceMethod.RuntimeMeasurement)
                => "maxPids=32",
            (SandboxCheck.PidLimitEnforced, SandboxEvidenceMethod.NegativeTest)
                => "pidLimitEventDelta=1",
            (_, SandboxEvidenceMethod.RuntimeMeasurement) => "observed=true",
            (_, SandboxEvidenceMethod.OwnershipReceipt) => "exactOwnedResourceRemoved=true",
            (_, SandboxEvidenceMethod.ManifestValidation) => "digestMatches=true",
            _ => "denied=true"
        };

    private static FakeVerifier Verifier(SandboxEvidenceTrust trust = SandboxEvidenceTrust.Verified) => new(trust);

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";

    private static string CheckState(SandboxPreflightReport report, SandboxCheck check) =>
        report.Checks[Regex.Replace(check.ToString(), "([a-z0-9])([A-Z])", "$1-$2").ToLowerInvariant()];

    private sealed class FakeVerifier(SandboxEvidenceTrust result) : ITrustedSandboxEvidenceVerifier
    {
        public SandboxEvidenceTrust Verify(
            SandboxProbeManifest manifest,
            SandboxProbeEvidence evidence,
            DateTimeOffset nowUtc) => result;
    }

    private sealed class ThrowingVerifier : ITrustedSandboxEvidenceVerifier
    {
        public SandboxEvidenceTrust Verify(
            SandboxProbeManifest manifest,
            SandboxProbeEvidence evidence,
            DateTimeOffset nowUtc) => throw new InvalidOperationException("private-verifier-details");
    }
}
