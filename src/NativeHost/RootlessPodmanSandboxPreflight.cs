using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal enum SandboxCheckState
{
    Verified,
    Blocked,
    Unverified
}

internal enum SandboxCheck
{
    RootlessPodmanAvailable,
    UserNamespaceIsolated,
    NonRootIdentity,
    CpuQuotaEnforced,
    MemoryLimitEnforced,
    PidLimitEnforced,
    RootFilesystemReadOnly,
    StagedInputReadOnly,
    WritableOutputRestricted,
    CapabilitiesRemoved,
    NoNewPrivileges,
    SeccompEnforced,
    NetworkNamespaceIsolated,
    OutboundNetworkDenied,
    DnsDenied,
    CloudMetadataDenied,
    CredentialEnvironmentIsolated,
    SshCloudCredentialsAndSocketsExcluded,
    HostAndSiblingWorkspacesIsolated,
    SymlinkAndTraversalDenied,
    ExactOwnedBoundedCleanup,
    RuntimeTimeoutEnforced,
    OutputSizeLimitEnforced,
    ImmutableImageAndManifestValidated
}

internal enum SandboxEvidenceMethod
{
    CliConfiguration,
    RuntimeMeasurement,
    NegativeTest,
    OwnershipReceipt,
    ManifestValidation
}

internal enum SandboxEvidenceTrust
{
    Verified,
    Rejected,
    Replay,
    Unavailable
}

internal sealed record SandboxResourceLimits(
    long CpuQuotaMicros,
    long CpuPeriodMicros,
    long MemoryBytes,
    int Pids,
    int TimeoutSeconds,
    long MaxOutputBytes);

internal sealed record SandboxSecurityPolicy(
    bool EnabledByDefault,
    bool AgentCodeExecutionEnabled,
    bool RootlessPodmanRequired,
    bool PrivateUserNamespaceRequired,
    bool NonRootRequired,
    bool CgroupLimitsRequired,
    bool ReadOnlyRootRequired,
    bool ReadOnlyInputRequired,
    bool BoundedOutputRequired,
    bool DropAllCapabilitiesRequired,
    bool NoNewPrivilegesRequired,
    bool SeccompRequired,
    bool IsolatedNetworkNamespaceRequired,
    bool DenyOutboundNetwork,
    bool DenyDns,
    bool DenyInheritedEnvironment,
    bool DenyHostCredentialsAndSockets,
    bool DenyHostWorkspaceMounts,
    bool DenySymlinkAndTraversal,
    bool ExactOwnedCleanupRequired,
    bool ImmutableImageDigestRequired);

internal sealed record SandboxProbeManifest(
    int ContractVersion,
    Guid ProbeId,
    string ChallengeNonce,
    string ImageDigest,
    string StagedInputDigest,
    string WritableOutputRoot,
    SandboxResourceLimits Limits,
    string PolicyDigest,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool AgentCodeExecutionAllowed,
    string ManifestDigest);

internal sealed record SandboxEvidenceSignal(
    Guid ObservationId,
    SandboxCheck Check,
    SandboxEvidenceMethod Method,
    SandboxCheckState State,
    string EvidenceCode,
    string Measurement,
    string ManifestDigest,
    DateTimeOffset ObservedAtUtc);

internal sealed record SandboxProbeEvidence(
    Guid ProbeId,
    string ChallengeNonce,
    string ImageDigest,
    string ManifestDigest,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<SandboxEvidenceSignal> Signals);

internal sealed record SandboxPreflightReport(
    Guid ProbeId,
    string? ImageDigest,
    string? ManifestDigest,
    string Status,
    bool AgentCodeExecutionAllowed,
    IReadOnlyDictionary<string, string> Checks,
    IReadOnlyList<string> SanitizedReasons);

internal interface IReadOnlyPodmanInspector
{
    Task<PodmanRuntimeObservation> InspectAsync(CancellationToken cancellationToken);
}

internal interface IAuthorizedSyntheticPodmanProbe
{
    Task<SandboxProbeEvidence> ProbeAsync(
        SandboxProbeManifest manifest,
        CancellationToken cancellationToken);
}

internal interface ITrustedSandboxEvidenceVerifier
{
    SandboxEvidenceTrust Verify(
        SandboxProbeManifest manifest,
        SandboxProbeEvidence evidence,
        DateTimeOffset nowUtc);
}

internal sealed record PodmanRuntimeObservation(
    bool Rootless,
    int EffectiveUid,
    bool UserNamespaceIsolated,
    string? CgroupVersion);

internal static class RootlessPodmanSandboxPreflight
{
    internal const int ContractVersion = 1;
    internal const string WritableOutputRoot = "output";
    private static readonly TimeSpan MaximumEvidenceLifetime = TimeSpan.FromMinutes(5);
    private const int MaximumEvidenceSignals = 128;
    private static readonly Regex Sha256Pattern = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex HexNoncePattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex EvidenceCodePattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<SandboxCheck, (SandboxEvidenceMethod First, string FirstCode,
        SandboxEvidenceMethod Second, string SecondCode)> RequiredSignals =
        new Dictionary<SandboxCheck, (SandboxEvidenceMethod, string, SandboxEvidenceMethod, string)>
        {
            [SandboxCheck.RootlessPodmanAvailable] = (SandboxEvidenceMethod.RuntimeMeasurement, "podman-rootless-observed",
                SandboxEvidenceMethod.NegativeTest, "rootless-runtime-canary-passed"),
            [SandboxCheck.UserNamespaceIsolated] = (SandboxEvidenceMethod.RuntimeMeasurement, "userns-mapping-observed",
                SandboxEvidenceMethod.NegativeTest, "host-userns-unavailable"),
            [SandboxCheck.NonRootIdentity] = (SandboxEvidenceMethod.RuntimeMeasurement, "effective-uid-nonzero",
                SandboxEvidenceMethod.NegativeTest, "root-only-operation-denied"),
            [SandboxCheck.CpuQuotaEnforced] = (SandboxEvidenceMethod.RuntimeMeasurement, "cgroup-cpu-quota-observed",
                SandboxEvidenceMethod.NegativeTest, "cpu-throttling-observed"),
            [SandboxCheck.MemoryLimitEnforced] = (SandboxEvidenceMethod.RuntimeMeasurement, "cgroup-memory-limit-observed",
                SandboxEvidenceMethod.NegativeTest, "container-local-oom-observed"),
            [SandboxCheck.PidLimitEnforced] = (SandboxEvidenceMethod.RuntimeMeasurement, "cgroup-pid-limit-observed",
                SandboxEvidenceMethod.NegativeTest, "pid-limit-denied"),
            [SandboxCheck.RootFilesystemReadOnly] = (SandboxEvidenceMethod.RuntimeMeasurement, "rootfs-readonly-observed",
                SandboxEvidenceMethod.NegativeTest, "rootfs-write-denied"),
            [SandboxCheck.StagedInputReadOnly] = (SandboxEvidenceMethod.RuntimeMeasurement, "input-readonly-observed",
                SandboxEvidenceMethod.NegativeTest, "input-write-denied"),
            [SandboxCheck.WritableOutputRestricted] = (SandboxEvidenceMethod.RuntimeMeasurement, "output-bound-observed",
                SandboxEvidenceMethod.NegativeTest, "write-outside-output-denied"),
            [SandboxCheck.CapabilitiesRemoved] = (SandboxEvidenceMethod.RuntimeMeasurement, "effective-capabilities-empty",
                SandboxEvidenceMethod.NegativeTest, "capability-operation-denied"),
            [SandboxCheck.NoNewPrivileges] = (SandboxEvidenceMethod.RuntimeMeasurement, "no-new-privileges-observed",
                SandboxEvidenceMethod.NegativeTest, "privilege-escalation-denied"),
            [SandboxCheck.SeccompEnforced] = (SandboxEvidenceMethod.RuntimeMeasurement, "seccomp-mode-observed",
                SandboxEvidenceMethod.NegativeTest, "forbidden-syscall-denied"),
            [SandboxCheck.NetworkNamespaceIsolated] = (SandboxEvidenceMethod.RuntimeMeasurement, "network-namespace-isolated",
                SandboxEvidenceMethod.NegativeTest, "host-network-unavailable"),
            [SandboxCheck.OutboundNetworkDenied] = (SandboxEvidenceMethod.RuntimeMeasurement, "egress-policy-observed",
                SandboxEvidenceMethod.NegativeTest, "outbound-connect-denied"),
            [SandboxCheck.DnsDenied] = (SandboxEvidenceMethod.RuntimeMeasurement, "dns-policy-observed",
                SandboxEvidenceMethod.NegativeTest, "dns-query-denied"),
            [SandboxCheck.CloudMetadataDenied] = (SandboxEvidenceMethod.RuntimeMeasurement, "metadata-policy-observed",
                SandboxEvidenceMethod.NegativeTest, "metadata-connect-denied"),
            [SandboxCheck.CredentialEnvironmentIsolated] = (SandboxEvidenceMethod.RuntimeMeasurement, "environment-allowlist-observed",
                SandboxEvidenceMethod.NegativeTest, "credential-canary-absent"),
            [SandboxCheck.SshCloudCredentialsAndSocketsExcluded] = (SandboxEvidenceMethod.RuntimeMeasurement, "mount-allowlist-observed",
                SandboxEvidenceMethod.NegativeTest, "ssh-cloud-socket-canaries-absent"),
            [SandboxCheck.HostAndSiblingWorkspacesIsolated] = (SandboxEvidenceMethod.RuntimeMeasurement, "workspace-mounts-confined",
                SandboxEvidenceMethod.NegativeTest, "host-home-sibling-canaries-absent"),
            [SandboxCheck.SymlinkAndTraversalDenied] = (SandboxEvidenceMethod.RuntimeMeasurement, "path-policy-observed",
                SandboxEvidenceMethod.NegativeTest, "symlink-traversal-denied"),
            [SandboxCheck.ExactOwnedBoundedCleanup] = (SandboxEvidenceMethod.OwnershipReceipt, "exact-owner-removal-within-bound-confirmed",
                SandboxEvidenceMethod.NegativeTest, "unowned-resource-preserved"),
            [SandboxCheck.RuntimeTimeoutEnforced] = (SandboxEvidenceMethod.RuntimeMeasurement, "deadline-policy-observed",
                SandboxEvidenceMethod.NegativeTest, "timeout-termination-observed"),
            [SandboxCheck.OutputSizeLimitEnforced] = (SandboxEvidenceMethod.RuntimeMeasurement, "output-cap-observed",
                SandboxEvidenceMethod.NegativeTest, "output-limit-enforced"),
            [SandboxCheck.ImmutableImageAndManifestValidated] = (SandboxEvidenceMethod.ManifestValidation, "manifest-digest-validated",
                SandboxEvidenceMethod.RuntimeMeasurement, "immutable-image-digest-observed")
        };

    internal static SandboxSecurityPolicy RequiredPolicy { get; } = new(
        EnabledByDefault: false,
        AgentCodeExecutionEnabled: false,
        RootlessPodmanRequired: true,
        PrivateUserNamespaceRequired: true,
        NonRootRequired: true,
        CgroupLimitsRequired: true,
        ReadOnlyRootRequired: true,
        ReadOnlyInputRequired: true,
        BoundedOutputRequired: true,
        DropAllCapabilitiesRequired: true,
        NoNewPrivilegesRequired: true,
        SeccompRequired: true,
        IsolatedNetworkNamespaceRequired: true,
        DenyOutboundNetwork: true,
        DenyDns: true,
        DenyInheritedEnvironment: true,
        DenyHostCredentialsAndSockets: true,
        DenyHostWorkspaceMounts: true,
        DenySymlinkAndTraversal: true,
        ExactOwnedCleanupRequired: true,
        ImmutableImageDigestRequired: true);

    internal static SandboxProbeManifest CreateManifest(
        string imageDigest,
        string stagedInputDigest,
        SandboxResourceLimits limits,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (!IsSha256(imageDigest) || !IsSha256(stagedInputDigest))
            throw new ArgumentException("Image and staged-input digests must be immutable SHA-256 digests.");
        if (!IsValidLimits(limits))
            throw new ArgumentOutOfRangeException(nameof(limits), "All resource limits must be positive.");
        if (!IsUtc(issuedAtUtc) || !IsUtc(expiresAtUtc) ||
            expiresAtUtc <= issuedAtUtc ||
            expiresAtUtc - issuedAtUtc > MaximumEvidenceLifetime)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Manifest lifetime is invalid.");

        var probeId = Guid.NewGuid();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var policyDigest = ComputePolicyDigest(limits);
        var provisional = new SandboxProbeManifest(
            ContractVersion,
            probeId,
            nonce,
            imageDigest,
            stagedInputDigest,
            WritableOutputRoot,
            limits,
            policyDigest,
            issuedAtUtc,
            expiresAtUtc,
            AgentCodeExecutionAllowed: false,
            ManifestDigest: string.Empty);
        return provisional with { ManifestDigest = ComputeManifestDigest(provisional) };
    }

    internal static SandboxPreflightReport Evaluate(
        SandboxProbeManifest? manifest,
        SandboxProbeEvidence? evidence,
        ITrustedSandboxEvidenceVerifier? verifier,
        DateTimeOffset nowUtc)
    {
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var checks = Enum.GetValues<SandboxCheck>()
            .ToDictionary(check => check, _ => SandboxCheckState.Unverified);

        if (!IsUtc(nowUtc))
        {
            reasons.Add("clock-invalid");
            return Report(manifest, SandboxCheckState.Unverified, checks, reasons);
        }

        if (manifest is null || !IsValidManifest(manifest, nowUtc))
        {
            reasons.Add(manifest is null ? "manifest-unavailable" : "manifest-invalid-or-stale");
            return Report(manifest, SandboxCheckState.Unverified, checks, reasons);
        }

        if (evidence is null)
        {
            reasons.Add("runtime-evidence-unavailable");
            return Report(manifest, SandboxCheckState.Unverified, checks, reasons);
        }

        if (evidence.Signals is null || evidence.Signals.Count > MaximumEvidenceSignals)
        {
            reasons.Add("runtime-evidence-invalid");
            return Report(manifest, SandboxCheckState.Unverified, checks, reasons);
        }

        var observedSignals = evidence.Signals.ToArray();
        if (observedSignals is null ||
            observedSignals.Any(signal => signal is null ||
                !Enum.IsDefined(signal.Check) ||
                !Enum.IsDefined(signal.Method) ||
                !Enum.IsDefined(signal.State)))
        {
            reasons.Add("runtime-evidence-invalid");
            return Report(manifest, SandboxCheckState.Unverified, checks, reasons);
        }
        evidence = evidence with { Signals = Array.AsReadOnly(observedSignals) };

        if (!IsBoundAndFresh(manifest, evidence, nowUtc))
        {
            reasons.Add("runtime-evidence-stale-or-mismatched");
            return Report(manifest, SandboxCheckState.Unverified, checks, reasons);
        }

        if (verifier is null)
        {
            reasons.Add("trusted-verifier-unavailable");
            return Report(manifest, SandboxCheckState.Unverified, checks, reasons);
        }

        SandboxEvidenceTrust trust;
        try
        {
            trust = verifier.Verify(manifest, evidence, nowUtc);
        }
        catch (Exception)
        {
            reasons.Add("trusted-verifier-error");
            return Report(manifest, SandboxCheckState.Unverified, checks, reasons);
        }

        if (trust != SandboxEvidenceTrust.Verified)
        {
            reasons.Add(trust switch
            {
                SandboxEvidenceTrust.Rejected => "runtime-evidence-rejected",
                SandboxEvidenceTrust.Replay => "runtime-evidence-replay",
                _ => "trusted-verifier-unavailable"
            });
            return Report(manifest, SandboxCheckState.Unverified, checks, reasons);
        }

        if (HasDuplicateObservations(evidence.Signals))
        {
            reasons.Add("duplicate-observation");
            return Report(manifest, SandboxCheckState.Unverified, checks, reasons);
        }

        foreach (var check in Enum.GetValues<SandboxCheck>())
        {
            var signals = evidence.Signals.Where(signal => signal.Check == check).ToArray();
            checks[check] = EvaluateCheck(
                check,
                signals,
                manifest.ManifestDigest,
                manifest.IssuedAtUtc,
                evidence.ObservedAtUtc,
                nowUtc);
            if (checks[check] != SandboxCheckState.Verified)
            {
                reasons.Add(check switch
                {
                    SandboxCheck.CpuQuotaEnforced or SandboxCheck.MemoryLimitEnforced or SandboxCheck.PidLimitEnforced
                        when signals.Length == 0 => "cgroup-enforcement-unverified",
                    SandboxCheck.ExactOwnedBoundedCleanup when signals.Length == 0
                        => "cleanup-evidence-incomplete",
                    _ => $"{ToCode(check)}-{ToCode(checks[check])}"
                });
            }
        }

        // No independently enrolled runtime verifier exists in this offline slice.
        foreach (var check in checks.Keys.ToArray())
        {
            if (checks[check] == SandboxCheckState.Verified)
                checks[check] = SandboxCheckState.Unverified;
        }
        reasons.Add("independent-runtime-verification-unavailable");

        var status = checks.Values.Contains(SandboxCheckState.Blocked)
            ? SandboxCheckState.Blocked
            : checks.Values.All(state => state == SandboxCheckState.Verified)
                ? SandboxCheckState.Verified
                : SandboxCheckState.Unverified;
        return Report(manifest, status, checks, reasons);
    }

    private static SandboxPreflightReport Report(
        SandboxProbeManifest? manifest,
        SandboxCheckState status,
        IReadOnlyDictionary<SandboxCheck, SandboxCheckState> checks,
        IEnumerable<string> reasons) =>
        new(
            manifest?.ProbeId ?? Guid.Empty,
            manifest is not null && IsSha256(manifest.ImageDigest) ? manifest.ImageDigest : null,
            manifest is not null && IsSha256(manifest.ManifestDigest) ? manifest.ManifestDigest : null,
            ToCode(status),
            AgentCodeExecutionAllowed: false,
            checks.ToDictionary(pair => ToCode(pair.Key), pair => ToCode(pair.Value)),
            reasons.ToArray());

    private static SandboxCheckState EvaluateCheck(
        SandboxCheck check,
        IReadOnlyCollection<SandboxEvidenceSignal> signals,
        string manifestDigest,
        DateTimeOffset manifestIssuedAtUtc,
        DateTimeOffset evidenceObservedAtUtc,
        DateTimeOffset nowUtc)
    {
        if (signals.Any(signal =>
                !Enum.IsDefined(signal.State) ||
                signal.State == SandboxCheckState.Blocked ||
                signal.ManifestDigest != manifestDigest ||
                !IsUtc(signal.ObservedAtUtc) ||
                signal.ObservedAtUtc < manifestIssuedAtUtc ||
                signal.ObservedAtUtc > evidenceObservedAtUtc ||
                signal.ObservedAtUtc > nowUtc ||
                !IsEvidenceCode(signal.EvidenceCode) ||
                !IsSafeMeasurement(signal.Measurement)))
            return SandboxCheckState.Unverified;

        if (signals.Any(signal => signal.State != SandboxCheckState.Verified))
            return SandboxCheckState.Unverified;

        if (!RequiredSignals.TryGetValue(check, out var required))
            return SandboxCheckState.Unverified;

        var measuredSignals = signals
            .Where(signal => signal.Method != SandboxEvidenceMethod.CliConfiguration)
            .ToArray();
        if (measuredSignals.Length > 2 ||
            measuredSignals.Any(signal =>
                (signal.Method != required.First || signal.EvidenceCode != required.FirstCode) &&
                (signal.Method != required.Second || signal.EvidenceCode != required.SecondCode)))
            return SandboxCheckState.Unverified;

        var firstFound = measuredSignals.Any(signal =>
            signal.Method == required.First && signal.EvidenceCode == required.FirstCode);
        var secondFound = measuredSignals.Any(signal =>
            signal.Method == required.Second && signal.EvidenceCode == required.SecondCode);

        return measuredSignals.Length == 2 && firstFound && secondFound
            ? SandboxCheckState.Verified
            : SandboxCheckState.Unverified;
    }

    private static bool IsValidManifest(SandboxProbeManifest manifest, DateTimeOffset nowUtc)
    {
        if (manifest.ContractVersion != ContractVersion ||
            manifest.ProbeId == Guid.Empty ||
            !IsNonce(manifest.ChallengeNonce) ||
            !IsSha256(manifest.ImageDigest) ||
            !IsSha256(manifest.StagedInputDigest) ||
            manifest.WritableOutputRoot != WritableOutputRoot ||
            manifest.Limits is null ||
            !IsValidLimits(manifest.Limits) ||
            manifest.AgentCodeExecutionAllowed ||
            !IsUtc(manifest.IssuedAtUtc) ||
            !IsUtc(manifest.ExpiresAtUtc) ||
            manifest.ExpiresAtUtc <= manifest.IssuedAtUtc ||
            manifest.ExpiresAtUtc - manifest.IssuedAtUtc > MaximumEvidenceLifetime ||
            nowUtc < manifest.IssuedAtUtc ||
            nowUtc >= manifest.ExpiresAtUtc)
            return false;

        return IsSha256(manifest.PolicyDigest) &&
            manifest.PolicyDigest == ComputePolicyDigest(manifest.Limits) &&
            IsSha256(manifest.ManifestDigest) &&
            manifest.ManifestDigest == ComputeManifestDigest(manifest);
    }

    private static bool IsBoundAndFresh(
        SandboxProbeManifest manifest,
        SandboxProbeEvidence evidence,
        DateTimeOffset nowUtc) =>
        evidence.ProbeId == manifest.ProbeId &&
        evidence.ChallengeNonce == manifest.ChallengeNonce &&
        evidence.ImageDigest == manifest.ImageDigest &&
        evidence.ManifestDigest == manifest.ManifestDigest &&
        IsUtc(evidence.ObservedAtUtc) &&
        IsUtc(evidence.ExpiresAtUtc) &&
        evidence.ObservedAtUtc >= manifest.IssuedAtUtc &&
        evidence.ObservedAtUtc <= nowUtc &&
        evidence.ExpiresAtUtc > nowUtc &&
        evidence.ExpiresAtUtc <= manifest.ExpiresAtUtc &&
        evidence.ExpiresAtUtc - evidence.ObservedAtUtc <= MaximumEvidenceLifetime;

    private static bool IsValidLimits(SandboxResourceLimits? limits) =>
        limits is not null &&
        limits.CpuQuotaMicros > 0 &&
        limits.CpuPeriodMicros > 0 &&
        limits.MemoryBytes > 0 &&
        limits.Pids > 0 &&
        limits.TimeoutSeconds > 0 &&
        limits.MaxOutputBytes > 0;

    private static bool HasDuplicateObservations(IReadOnlyCollection<SandboxEvidenceSignal> signals) =>
        signals.Any(signal => signal.ObservationId == Guid.Empty) ||
        signals.Select(signal => signal.ObservationId).Distinct().Count() != signals.Count ||
        signals.GroupBy(signal => (signal.Check, signal.Method, signal.EvidenceCode))
            .Any(group => group.Count() > 1);

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static bool IsSha256(string? value) => value is not null && Sha256Pattern.IsMatch(value);

    private static bool IsNonce(string? value) => value is not null && HexNoncePattern.IsMatch(value);

    private static bool IsEvidenceCode(string? value) =>
        value is not null && EvidenceCodePattern.IsMatch(value);

    private static bool IsSafeMeasurement(string? value) =>
        value is { Length: > 0 and <= 1024 } &&
        !value.Any(char.IsControl);

    private static string ComputePolicyDigest(SandboxResourceLimits limits) =>
        Hash(string.Join('\n',
            "hic.podman-preflight-policy/1",
            RequiredPolicy.EnabledByDefault.ToString(),
            RequiredPolicy.AgentCodeExecutionEnabled.ToString(),
            RequiredPolicy.RootlessPodmanRequired.ToString(),
            RequiredPolicy.PrivateUserNamespaceRequired.ToString(),
            RequiredPolicy.NonRootRequired.ToString(),
            RequiredPolicy.CgroupLimitsRequired.ToString(),
            RequiredPolicy.ReadOnlyRootRequired.ToString(),
            RequiredPolicy.ReadOnlyInputRequired.ToString(),
            RequiredPolicy.BoundedOutputRequired.ToString(),
            RequiredPolicy.DropAllCapabilitiesRequired.ToString(),
            RequiredPolicy.NoNewPrivilegesRequired.ToString(),
            RequiredPolicy.SeccompRequired.ToString(),
            RequiredPolicy.IsolatedNetworkNamespaceRequired.ToString(),
            RequiredPolicy.DenyOutboundNetwork.ToString(),
            RequiredPolicy.DenyDns.ToString(),
            RequiredPolicy.DenyInheritedEnvironment.ToString(),
            RequiredPolicy.DenyHostCredentialsAndSockets.ToString(),
            RequiredPolicy.DenyHostWorkspaceMounts.ToString(),
            RequiredPolicy.DenySymlinkAndTraversal.ToString(),
            RequiredPolicy.ExactOwnedCleanupRequired.ToString(),
            RequiredPolicy.ImmutableImageDigestRequired.ToString(),
            limits.CpuQuotaMicros.ToString(CultureInfo.InvariantCulture),
            limits.CpuPeriodMicros.ToString(CultureInfo.InvariantCulture),
            limits.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            limits.Pids.ToString(CultureInfo.InvariantCulture),
            limits.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            limits.MaxOutputBytes.ToString(CultureInfo.InvariantCulture)));

    private static string ComputeManifestDigest(SandboxProbeManifest manifest) =>
        Hash(string.Join('\n',
            "hic.podman-preflight-manifest/1",
            manifest.ContractVersion.ToString(CultureInfo.InvariantCulture),
            manifest.ProbeId.ToString("N"),
            manifest.ChallengeNonce,
            manifest.ImageDigest,
            manifest.StagedInputDigest,
            manifest.WritableOutputRoot,
            manifest.Limits.CpuQuotaMicros.ToString(CultureInfo.InvariantCulture),
            manifest.Limits.CpuPeriodMicros.ToString(CultureInfo.InvariantCulture),
            manifest.Limits.MemoryBytes.ToString(CultureInfo.InvariantCulture),
            manifest.Limits.Pids.ToString(CultureInfo.InvariantCulture),
            manifest.Limits.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            manifest.Limits.MaxOutputBytes.ToString(CultureInfo.InvariantCulture),
            manifest.PolicyDigest,
            manifest.IssuedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            manifest.ExpiresAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            manifest.AgentCodeExecutionAllowed.ToString()));

    private static string Hash(string input) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant()}";

    private static string ToCode<T>(T value) where T : Enum =>
        Regex.Replace(value.ToString(), "([a-z0-9])([A-Z])", "$1-$2").ToLowerInvariant();
}
