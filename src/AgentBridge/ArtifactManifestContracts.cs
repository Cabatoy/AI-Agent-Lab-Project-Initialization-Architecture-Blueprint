using System.Collections.Immutable;

namespace Hic.AgentBridge;

public sealed record ArtifactFile(string Path, string Sha256, long ByteLength, string Type, string Mode);
public sealed record ArtifactPathRule(string Path, ImmutableArray<string> PermittedSha256);
public sealed record ArtifactResourcePolicy(
    string ImageSha256, string Network, int CpuMillis, long MemoryBytes, int Pids, int TimeoutSeconds,
    int MaxFiles, long MaxFileBytes, long MaxTotalBytes, ImmutableArray<ArtifactPathRule> OutputRules);
public sealed record ArtifactInputFields(
    string SchemaVersion, Guid TaskId, Guid RunId, Guid ApprovalId, string Repository,
    long InstallationId, string BaseCommitSha, string SourceTreeSha256, string Operation,
    ArtifactResourcePolicy Policy, ImmutableArray<ArtifactFile> Files);
public sealed record ArtifactInputManifest(ArtifactInputFields Fields, string ManifestSha256);
public sealed record ArtifactOutputFields(
    string SchemaVersion, Guid TaskId, Guid RunId, Guid ApprovalId, string Repository,
    long InstallationId, string BaseCommitSha, string InputManifestSha256, ImmutableArray<ArtifactFile> Files);
public sealed record ArtifactOutputManifest(ArtifactOutputFields Fields, string ManifestSha256);

public sealed record VerifiedArtifactManifest(string ManifestSha256, int FileCount, long TotalBytes);
