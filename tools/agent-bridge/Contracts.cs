namespace Hic.AgentBridge;

public sealed record SnapshotInput
{
    public required string ContractVersion { get; init; }
    public required string ProjectId { get; init; }
    public required Guid? TaskId { get; init; }
    public required Guid? ExecutionId { get; init; }
    public required Guid? CorrelationId { get; init; }
    public required string SourceCommit { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required string ExecutionStatus { get; init; }
    public required string Summary { get; init; }
    public required ChangedFile[] ChangedFiles { get; init; }
    public required Evidence[] VerificationEvidence { get; init; }
    public required string[] KnownRisks { get; init; }
    public required string RecommendedNextAction { get; init; }
    public required Usage Usage { get; init; }
    public required ArtifactInput[] ArtifactReferences { get; init; }
}

public sealed record ChangedFile(string Path, string Change);
public sealed record ArtifactInput(string Path);
public sealed record Usage(string Measurement, long? InputTokens, long? OutputTokens,
    decimal? Cost, string? Currency, string? PricingVersion);
public sealed record Evidence(string Kind, string Status, string Result, string? SourceRef,
    string SourceCommit, DateTimeOffset ObservedAtUtc);
public sealed record Source(string Repository, string Branch, string CommitSha, bool WorkingTreeDirty);
public sealed record Artifact(string Path, string SourceCommit, string Sha256, long SizeBytes,
    string AccessScope);

public sealed record Snapshot(
    string ContractVersion,
    string SnapshotId,
    string ProjectId,
    Guid? TaskId,
    Guid? ExecutionId,
    Guid? CorrelationId,
    Source Source,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    string ExecutionStatus,
    string ExecutionStatusConfidence,
    string Summary,
    ChangedFile[] ChangedFiles,
    Evidence[] VerificationEvidence,
    string[] KnownRisks,
    string RecommendedNextAction,
    Usage Usage,
    Artifact[] ArtifactReferences);

public sealed record LatestPointer(string ContractVersion, string ProjectId, string SnapshotId,
    string SnapshotPath, string Sha256, string SourceCommit, DateTimeOffset ObservedAtUtc,
    DateTimeOffset? PublishedAtUtc);

public sealed class BridgeException(string code) : Exception(code);
