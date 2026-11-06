namespace Hic.AgentBridge;

public sealed class SnapshotProducer
{
    public const string Version = "hic.bridge/1.0";
    public const string Repository = "Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint";

    private static readonly Dictionary<string, string> Summaries = new(StringComparer.Ordinal)
    {
        ["snapshot-foundation"] = "Read-only handoff snapshot foundation prepared.",
        ["verification-complete"] = "Scoped verification evidence is available; inspect confidence and limits.",
        ["work-pending"] = "Work remains pending; no execution permission is implied.",
        ["blocked"] = "Progress is blocked; inspect known risks before proceeding.",
        ["unknown"] = "Execution outcome is unknown."
    };
    private static readonly Dictionary<string, string> Risks = new(StringComparer.Ordinal)
    {
        ["caller-attestation"] = "Caller-supplied execution and test results are not independently verified.",
        ["unpublished"] = "This artifact has not been published to GitHub.",
        ["runtime-not-run"] = "Live runtime verification was not run.",
        ["usage-not-measured"] = "Token usage and cost were not measured.",
        ["recovery-not-verified"] = "Crash recovery and external side effects were not verified.",
        ["source-dirty"] = "The source worktree includes uncommitted changes."
    };
    private static readonly Dictionary<string, string> Actions = new(StringComparer.Ordinal)
    {
        ["review-before-publication"] = "Review the snapshot digest and approve a separate B1.2 publication workflow.",
        ["collect-evidence"] = "Collect explicitly authorized verification evidence.",
        ["resolve-blocker"] = "Resolve the documented blocker under a separately approved scope."
    };

    public Snapshot Generate(byte[] approvedInput, string approvalSha256, ILocalGit git, DateTimeOffset now)
    {
        Safety.Utc(now, now);
        Safety.Sha(approvalSha256, 64);
        if (CanonicalJson.Hash(approvedInput) != approvalSha256)
            throw new BridgeException("APPROVAL_DIGEST_MISMATCH");
        var input = CanonicalJson.Parse<SnapshotInput>(approvedInput);
        Validate(input, now);
        var source = git.ReadSource();
        ValidateSource(source);
        if (source.CommitSha != input.SourceCommit)
            throw new BridgeException("SOURCE_COMMIT_MISMATCH");

        var artifacts = input.ArtifactReferences.Select(reference =>
        {
            Safety.Reference(reference.Path);
            var bytes = git.ReadApprovedBlob(source.CommitSha, reference.Path);
            return new Artifact(reference.Path, source.CommitSha, CanonicalJson.Hash(bytes),
                bytes.LongLength, "repository-reader");
        }).OrderBy(a => a.Path, StringComparer.Ordinal).ToArray();

        var evidence = input.VerificationEvidence.ToList();
        evidence.Add(new Evidence("git_revision", "Verified", "MATCH", null,
            source.CommitSha, now));
        evidence.AddRange(artifacts.Select(a => new Evidence("artifact_integrity", "Verified", "MATCH",
            a.Path, source.CommitSha, now)));
        var risks = input.KnownRisks.Append("caller-attestation").Append("unpublished")
            .Append("usage-not-measured").Concat(source.WorkingTreeDirty ? ["source-dirty"] : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(r => Risks[r]).ToArray();
        var snapshot = new Snapshot(Version, "", input.ProjectId, input.TaskId, input.ExecutionId,
            input.CorrelationId, source, input.ObservedAtUtc, null, input.ExecutionStatus,
            input.ExecutionStatus == "Unknown" ? "Unknown" : "Inferred", Summaries[input.Summary],
            input.ChangedFiles.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(),
            evidence.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.SourceRef, StringComparer.Ordinal)
                .ThenBy(e => e.Status, StringComparer.Ordinal).ThenBy(e => e.Result, StringComparer.Ordinal)
                .ThenBy(e => e.ObservedAtUtc).ToArray(),
            risks, Actions[input.RecommendedNextAction], input.Usage, artifacts);
        snapshot = snapshot with { SnapshotId = Identity(snapshot) };
        ValidateStored(snapshot, now);
        return snapshot;
    }

    private static void Validate(SnapshotInput input, DateTimeOffset now)
    {
        if (input.ContractVersion != Version)
            throw new BridgeException("UNSUPPORTED_VERSION");
        Safety.Project(input.ProjectId);
        Safety.Sha(input.SourceCommit);
        Safety.Utc(input.ObservedAtUtc, now);
        if (input.TaskId == Guid.Empty || input.ExecutionId == Guid.Empty || input.CorrelationId == Guid.Empty
            || (input.ExecutionId is not null && input.TaskId is null))
            throw new BridgeException("INVALID_IDENTITY");
        if (!new[] { "Draft", "Proposed", "Planned", "Approved", "Executing", "InProgress", "Review", "Retrying",
            "Completed", "Failed", "Blocked", "Cancelled", "Rejected", "Unknown" }.Contains(input.ExecutionStatus))
            throw new BridgeException("INVALID_STATUS");
        if (input.ChangedFiles.Any(f => f is null) || input.ArtifactReferences.Any(a => a is null)
            || input.VerificationEvidence.Any(e => e is null) || input.KnownRisks.Any(r => r is null))
            throw new BridgeException("NULL_COLLECTION_ITEM");
        if (!Summaries.ContainsKey(input.Summary) || !Actions.ContainsKey(input.RecommendedNextAction)
            || input.KnownRisks.Length > 8 || input.KnownRisks.Any(r => !Risks.ContainsKey(r)))
            throw new BridgeException("UNAPPROVED_TEXT_CODE");
        if (input.ChangedFiles.Length > 20 || input.ArtifactReferences.Length > 8
            || input.VerificationEvidence.Length is < 1 or > 12)
            throw new BridgeException("COLLECTION_LIMIT");
        if (input.Usage != new Usage("unknown", null, null, null, null, null))
            throw new BridgeException("USAGE_NOT_MEASURED");
        foreach (var file in input.ChangedFiles)
        {
            Safety.Reference(file.Path);
            if (!new[] { "added", "modified", "deleted" }.Contains(file.Change))
                throw new BridgeException("INVALID_CHANGE");
        }
        if (input.ChangedFiles.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != input.ChangedFiles.Length
            || input.ArtifactReferences.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != input.ArtifactReferences.Length)
            throw new BridgeException("DUPLICATE_REFERENCE");
        foreach (var evidence in input.VerificationEvidence)
        {
            if (evidence.SourceCommit != input.SourceCommit)
                throw new BridgeException("EVIDENCE_COMMIT_MISMATCH");
            Safety.Utc(evidence.ObservedAtUtc, input.ObservedAtUtc);
            if (evidence.SourceRef is not null)
                Safety.Reference(evidence.SourceRef);
            if (!new[] { "tests", "build", "runtime", "review", "usage" }.Contains(evidence.Kind)
                || !(evidence.Status == "Inferred" && new[] { "PASS", "FAIL" }.Contains(evidence.Result)
                    && evidence.SourceRef is not null
                    || evidence.Status == "NOT_RUN" && evidence.Result == "NOT_RUN"
                    || evidence.Status == "NOT_MEASURED" && evidence.Result == "NOT_MEASURED"
                    || evidence.Status == "Unknown" && evidence.Result == "Unknown"))
                throw new BridgeException("UNVERIFIED_EVIDENCE_CLAIM");
        }
    }

    public static string Identity(Snapshot snapshot) =>
        CanonicalJson.Hash(CanonicalJson.Serialize(snapshot with { SnapshotId = "" }));

    public static void ValidateStored(Snapshot snapshot, DateTimeOffset now)
    {
        ValidateSource(snapshot.Source);
        Safety.Sha(snapshot.SnapshotId, 64);
        if (snapshot.ContractVersion != Version || snapshot.PublishedAtUtc is not null
            || snapshot.SnapshotId != Identity(snapshot))
            throw new BridgeException("INVALID_SNAPSHOT_IDENTITY");
        var summary = Summaries.SingleOrDefault(p => p.Value == snapshot.Summary).Key;
        var action = Actions.SingleOrDefault(p => p.Value == snapshot.RecommendedNextAction).Key;
        var risks = snapshot.KnownRisks.Select(r => Risks.SingleOrDefault(p => p.Value == r).Key).ToArray();
        if (summary is null || action is null || risks.Any(r => r is null)
            || !risks.Contains("caller-attestation") || !risks.Contains("unpublished")
            || !risks.Contains("usage-not-measured")
            || snapshot.Source.WorkingTreeDirty && !risks.Contains("source-dirty")
            || snapshot.ExecutionStatusConfidence != (snapshot.ExecutionStatus == "Unknown" ? "Unknown" : "Inferred"))
            throw new BridgeException("INVALID_SNAPSHOT_CONTENT");
        var supplied = snapshot.VerificationEvidence.Where(e => e.Status != "Verified").ToArray();
        Validate(new SnapshotInput
        {
            ContractVersion = snapshot.ContractVersion, ProjectId = snapshot.ProjectId,
            TaskId = snapshot.TaskId, ExecutionId = snapshot.ExecutionId, CorrelationId = snapshot.CorrelationId,
            SourceCommit = snapshot.Source.CommitSha, ObservedAtUtc = snapshot.ObservedAtUtc,
            ExecutionStatus = snapshot.ExecutionStatus, Summary = summary, ChangedFiles = snapshot.ChangedFiles,
            VerificationEvidence = supplied, KnownRisks = risks, RecommendedNextAction = action,
            Usage = snapshot.Usage, ArtifactReferences = snapshot.ArtifactReferences.Select(a => new ArtifactInput(a.Path)).ToArray()
        }, now);
        var gitEvidence = snapshot.VerificationEvidence.Where(e => e.Kind == "git_revision").ToArray();
        if (gitEvidence.Length != 1 || gitEvidence[0].ObservedAtUtc < snapshot.ObservedAtUtc)
            throw new BridgeException("INVALID_VERIFIED_EVIDENCE");
        var capturedAt = gitEvidence[0].ObservedAtUtc;
        Safety.Utc(capturedAt, now);
        var expected = new List<Evidence>
        {
            new("git_revision", "Verified", "MATCH", null, snapshot.Source.CommitSha, capturedAt)
        };
        foreach (var artifact in snapshot.ArtifactReferences)
        {
            Safety.Sha(artifact.Sha256, 64);
            if (artifact.SourceCommit != snapshot.Source.CommitSha || artifact.SizeBytes is < 0 or > 1_048_576
                || artifact.AccessScope != "repository-reader")
                throw new BridgeException("INVALID_ARTIFACT");
            expected.Add(new Evidence("artifact_integrity", "Verified", "MATCH", artifact.Path,
                snapshot.Source.CommitSha, capturedAt));
        }
        var verified = snapshot.VerificationEvidence.Where(e => e.Status == "Verified").ToArray();
        if (verified.Length != expected.Count || expected.Any(e => verified.Count(v => v == e) != 1))
            throw new BridgeException("INVALID_VERIFIED_EVIDENCE");
    }

    public static void ValidateSource(Source source)
    {
        Safety.Sha(source.CommitSha);
        Safety.CheckString(source.Branch);
        Safety.Repository(source.Repository);
        if (source.Branch.Length > 128
            || !System.Text.RegularExpressions.Regex.IsMatch(source.Branch,
                "^[A-Za-z0-9][A-Za-z0-9_./-]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new BridgeException("INVALID_SOURCE");
    }
}
