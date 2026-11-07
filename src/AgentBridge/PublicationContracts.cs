namespace Hic.AgentBridge;

public sealed record PublicationTarget(string Repository, string Branch);
public sealed record PublicationFile(string Path, string Sha256, int SizeBytes, string? PreviousBlob);
public sealed record PublicationManifest(
    string ContractVersion,
    PublicationTarget Destination,
    string TransportFingerprint,
    string ExpectedRemoteCommit,
    Source Source,
    string ProjectId,
    string SnapshotId,
    DateTimeOffset PreparedAtUtc,
    PublicationFile[] Files);
public sealed record PublicationJournal(string Stage, string ManifestDigest, string? PublicationCommit,
    string? VerifiedRemoteCommit, DateTimeOffset? VerifiedAtUtc);
public sealed record PublicationPreview(string Digest, string Text, PublicationManifest Manifest);
public sealed record PublicationReceipt(string Disposition, string SourceCommit, string SnapshotDigest,
    string PublicationCommit, string RemoteCommit, DateTimeOffset VerifiedAtUtc);

public interface IPublicationTransport
{
    string Fingerprint { get; }
    void ValidateTarget(PublicationTarget target);
    string Head(PublicationGit git, PublicationTarget target);
    void Fetch(PublicationGit git, PublicationTarget target);
    void Push(PublicationGit git, PublicationTarget target, string commit);
}
