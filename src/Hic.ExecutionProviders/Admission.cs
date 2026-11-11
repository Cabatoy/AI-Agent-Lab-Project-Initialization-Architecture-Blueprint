using System.Security.Cryptography;
using System.Text.Json;

namespace Hic.ExecutionProviders;

public static class SyntheticAdmission
{
    public const string ProofVersion = "hic.execution-attestation/0";
    public const string CapabilityVersion = "hic.execution-capabilities/0";

    public static string CapabilitiesDigest(ProviderCapabilities capabilities) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(capabilities)));

    public static void CheckAuthority(ExecutionManifest m, ISyntheticTrustSource trust, DateTimeOffset now,
        bool allowRevoked = false)
    {
        var digest = m.Digest();
        var a = trust.GetAuthority(m);
        if (a is null || a.TenantId != m.TenantId || a.ActorId != m.ActorId ||
            a.TaskId != m.TaskId || a.RunId != m.RunId || a.AttemptId != m.AttemptId ||
            a.TaskRevision != m.TaskRevision || a.ApprovalId != m.ApprovalId ||
            a.ApprovalEpoch != m.ApprovalEpoch || a.ClaimGeneration != m.ClaimGeneration ||
            a.ManifestDigest != digest || now >= a.ExpiresAt || now >= m.ExpiresAt)
            throw new ProviderException(ProviderError.AuthorityUnverified);
        if (a.Revoked && !allowRevoked) throw new ProviderException(ProviderError.ApprovalRevoked);
    }

    public static void Check(ExecutionManifest m, ProviderCapabilities c, string challenge,
        ISyntheticTrustSource trust, DateTimeOffset now)
    {
        CheckAuthority(m, trust, now);
        if (c.Version != CapabilityVersion) throw new ProviderException(ProviderError.ProtocolMismatch);
        if (c.Identity.TenantId != m.TenantId) throw new ProviderException(ProviderError.AuthorityUnverified);
        var consent = trust.GetConsent(c.Identity);
        if (consent is null || consent.TenantId != m.TenantId ||
            consent.ManifestDigest != m.Digest() || consent.InstallationId != c.Identity.InstallationId ||
            consent.InstanceId != c.Identity.InstanceId || consent.Environment != c.Environment ||
            consent.Region != c.Region || consent.PrivacyClass != c.PrivacyClass ||
            consent.Currency != c.Currency || c.EstimatedCharge is not null &&
            consent.MaxCharge < c.EstimatedCharge)
            throw new ProviderException(ProviderError.ConsentRequired);
        if (!c.Available) throw new ProviderException(ProviderError.ProviderUnavailable);
        if (!c.QuotaAvailable) throw new ProviderException(ProviderError.QuotaUnavailable);
        if (c.EstimatedCharge is null || c.EstimatedCharge < 0 || c.EstimatedCharge > m.MaxCharge ||
            c.Currency != m.Currency) throw new ProviderException(ProviderError.CostNotAllowed);
        if (c.Region != m.Region) throw new ProviderException(ProviderError.RegionNotAllowed);
        if (c.PrivacyClass != m.PrivacyClass || c.Architecture != m.Architecture ||
            c.Tool != m.RequiredTool || c.NetworkPolicy != m.NetworkPolicy ||
            c.CpuMillicores < m.CpuMillicores || c.MemoryMiB < m.MemoryMiB ||
            c.PidsMax < m.PidsMax || c.TimeoutSeconds < m.TimeoutSeconds ||
            c.MaxOutputBytes < m.MaxOutputBytes)
            throw new ProviderException(ProviderError.CapabilityMismatch);
        if (string.IsNullOrWhiteSpace(challenge) || challenge.Length > 128)
            throw new ProviderException(ProviderError.AttestationUnverified);
        var proof = trust.GetProof(c.Identity, challenge);
        if (proof is null || proof.Version != ProofVersion || proof.Identity != c.Identity ||
            proof.ManifestDigest != m.Digest() || proof.CapabilitiesDigest != CapabilitiesDigest(c) ||
            proof.Challenge != challenge ||
            !proof.TrustedFixture || !proof.AllControlsSimulated)
            throw new ProviderException(ProviderError.AttestationUnverified);
        if (proof.ObservedAt > now || proof.ExpiresAt <= now || proof.ExpiresAt <= proof.ObservedAt)
            throw new ProviderException(ProviderError.AttestationStale);
    }
}

public enum SelectionMode { Manual, Auto }

// Consent is injected from a trusted source in this offline simulation, not from a provider.
public sealed record SyntheticConsent(
    string TenantId, string ManifestDigest, string InstallationId, string InstanceId,
    string Environment, string Region, string PrivacyClass, decimal MaxCharge, string Currency);

public static class SyntheticProviderSelector
{
    public static IExecutionProvider Select(ExecutionManifest manifest, SelectionMode mode,
        ProviderIdentity? manual, IReadOnlyList<IExecutionProvider> providers,
        string challenge, ISyntheticTrustSource trust, DateTimeOffset now)
    {
        SyntheticAdmission.CheckAuthority(manifest, trust, now);
        if (mode is not SelectionMode.Manual and not SelectionMode.Auto ||
            mode == SelectionMode.Manual && manual is null)
            throw new ProviderException(ProviderError.ConsentRequired);

        var seen = new HashSet<ProviderIdentity>();
        foreach (var provider in providers)
            if (!seen.Add(provider.DescribeCapabilities().Identity))
                throw new ProviderException(ProviderError.Conflict);

        ProviderError? lastError = null;
        foreach (var provider in providers)
        {
            var c = provider.DescribeCapabilities();
            if (mode == SelectionMode.Manual && c.Identity != manual) continue;
            try
            {
                SyntheticAdmission.Check(manifest, c, challenge, trust, now);
                return provider;
            }
            catch (ProviderException e)
            {
                lastError = e.Code;
            }
        }
        throw new ProviderException(lastError ?? ProviderError.ConsentRequired);
    }
}
