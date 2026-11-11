using System.Security.Cryptography;
using System.Text.Json;

namespace Hic.ExecutionProviders;

public enum ProviderError
{
    InvalidManifest, ProtocolMismatch, AuthorityUnverified, ApprovalRevoked,
    AttestationUnverified, AttestationStale, CapabilityMismatch, CostNotAllowed,
    QuotaUnavailable, RegionNotAllowed, ConsentRequired, ProviderUnavailable,
    Conflict, NotFound, Cancelled, CleanupUnverified
}

public sealed class ProviderException(ProviderError code) : Exception(code.ToString())
{
    public ProviderError Code { get; } = code;
}

// All fields are immutable scalars; the digest is computed, never supplied by the request.
public sealed record ExecutionManifest(
    string Version, string TenantId, string ActorId, Guid TaskId, Guid RunId, Guid AttemptId,
    long TaskRevision, Guid ApprovalId, long ApprovalEpoch, long ClaimGeneration,
    string RepositoryInstallation, string Repository, string BaseSha, string SourceDigest,
    string PolicyRevision, string Architecture, string RequiredTool, int CpuMillicores,
    int MemoryMiB, int PidsMax, int TimeoutSeconds, long MaxOutputBytes,
    string NetworkPolicy, string PrivacyClass, string Region, decimal MaxCharge,
    string Currency, string IdempotencyKey, DateTimeOffset ExpiresAt)
{
    public const string CurrentVersion = "hic.execution-request/0";

    public string Digest()
    {
        Validate();
        // Fixed property order, invariant JSON numbers and UTC timestamp form define v0 bytes.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this with { ExpiresAt = ExpiresAt.ToUniversalTime() });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public void Validate()
    {
        if (Version != CurrentVersion) throw new ProviderException(ProviderError.ProtocolMismatch);
        if (!Token(TenantId) || !Token(ActorId) || TaskId == Guid.Empty || RunId == Guid.Empty ||
            AttemptId == Guid.Empty || ApprovalId == Guid.Empty || TaskRevision <= 0 ||
            ApprovalEpoch <= 0 || ClaimGeneration <= 0 || !Token(RepositoryInstallation) ||
            !Token(Repository) || !Hex(BaseSha, 40) || !Hex(SourceDigest, 64) ||
            !Token(PolicyRevision) || !Token(Architecture) || !Token(RequiredTool) ||
            CpuMillicores <= 0 || MemoryMiB <= 0 || PidsMax <= 0 || TimeoutSeconds <= 0 ||
            MaxOutputBytes <= 0 || NetworkPolicy != "deny-all" || !Token(PrivacyClass) ||
            !Token(Region) || MaxCharge < 0 || Currency != "USD" || !Token(IdempotencyKey) ||
            ExpiresAt == default)
            throw new ProviderException(ProviderError.InvalidManifest);
    }

    private static bool Token(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(c => c >= '!' && c <= '~');

    private static bool Hex(string? value, int length) =>
        value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record ProviderIdentity(string TenantId, string InstallationId, string InstanceId);

public sealed record ProviderCapabilities(
    string Version, ProviderIdentity Identity, string Environment, string Region,
    string PrivacyClass, string Architecture, string Tool, int CpuMillicores,
    int MemoryMiB, int PidsMax, int TimeoutSeconds, long MaxOutputBytes,
    string NetworkPolicy, decimal? EstimatedCharge, string Currency, bool QuotaAvailable,
    bool Available);

// These values are test inputs, not signed approvals, real claims, or OS attestations.
public sealed record SyntheticAuthority(
    string TenantId, string ActorId, Guid TaskId, Guid RunId, Guid AttemptId,
    long TaskRevision, Guid ApprovalId, long ApprovalEpoch, long ClaimGeneration,
    string ManifestDigest, DateTimeOffset ExpiresAt, bool Revoked);

public sealed record SyntheticProof(
    string Version, ProviderIdentity Identity, string ManifestDigest, string CapabilitiesDigest, string Challenge,
    DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt, bool TrustedFixture,
    bool AllControlsSimulated);

public interface ISyntheticTrustSource
{
    SyntheticAuthority? GetAuthority(ExecutionManifest manifest);
    SyntheticProof? GetProof(ProviderIdentity identity, string challenge);
    SyntheticConsent? GetConsent(ProviderIdentity identity);
}

public enum FakeStatus { Prepared, Started, Cancelled, Cleaned }
public sealed record FakeHandle(ProviderIdentity Provider, Guid AttemptId, string ResourceId);
public sealed record FakeObservation(FakeStatus Status, string EvidenceLevel);

public interface IExecutionProvider
{
    ProviderCapabilities DescribeCapabilities();
    void Preflight(ExecutionManifest manifest, string challenge);
    FakeHandle Prepare(ExecutionManifest manifest, string challenge);
    FakeObservation Start(ExecutionManifest manifest, FakeHandle handle, string challenge);
    FakeObservation Inspect(ExecutionManifest manifest, FakeHandle handle);
    FakeObservation Cancel(ExecutionManifest manifest, FakeHandle handle);
    FakeObservation CollectEvidence(ExecutionManifest manifest, FakeHandle handle);
    FakeObservation Cleanup(ExecutionManifest manifest, FakeHandle handle);
}
