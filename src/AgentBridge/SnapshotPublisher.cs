using System.Text;

namespace Hic.AgentBridge;

public sealed class SnapshotPublisher(
    string sourceRoot,
    string workspacePath,
    string sourceRepository,
    PublicationTarget target,
    IPublicationTransport transport,
    string? credentialHelper = null)
{
    private readonly string sourceRoot = Path.GetFullPath(sourceRoot);
    private readonly PublicationWorkspace workspace = new(workspacePath);
    private readonly PublicationGit git = new(workspacePath, credentialHelper);

    public PublicationPreview Prepare(string projectId, DateTimeOffset now)
    {
        ValidateTarget();
        Safety.Project(projectId);
        Safety.Utc(now, now);
        AssertSourceIndexEmpty();
        var bundle = ReadSourceBundle(projectId, now);
        workspace.Create(sourceRoot, git);
        workspace.Check(git);
        var head = transport.Head(git, target);
        transport.Fetch(git, target);
        if (git.Text("rev-parse", "refs/heads/publication-base") != head || transport.Head(git, target) != head)
            throw new BridgeException("REMOTE_CHANGED_DURING_PREPARATION");
        git.Run(["read-tree", head]);
        var manifest = BuildManifest(bundle, head, now);
        var digest = Digest(manifest);
        workspace.Save(workspace.ManifestPath, manifest, createNew: true);
        workspace.Save(workspace.JournalPath,
            new PublicationJournal("prepared", digest, null, null, null), createNew: true);
        return new PublicationPreview(digest, Preview(manifest, digest, bundle.Snapshot), manifest);
    }

    public PublicationReceipt Publish(string approvalDigest, bool explicitApproval, DateTimeOffset now)
    {
        if (!explicitApproval)
            throw new BridgeException("EXPLICIT_PUBLICATION_APPROVAL_REQUIRED");
        Safety.Sha(approvalDigest, 64);
        ValidateTarget();
        workspace.Check(git);
        var lockPath = Path.Combine(workspace.Root, "publication.lock");
        PublicationWorkspace.NoLinks(lockPath);
        using var publicationLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var manifest = workspace.Read<PublicationManifest>(workspace.ManifestPath);
        var journal = workspace.Read<PublicationJournal>(workspace.JournalPath);
        var digest = Digest(manifest);
        if (digest != approvalDigest || journal.ManifestDigest != digest)
            throw new BridgeException("PUBLICATION_APPROVAL_DIGEST_MISMATCH");
        if (manifest.Destination != target || manifest.TransportFingerprint != transport.Fingerprint)
            throw new BridgeException("PUBLICATION_DESTINATION_CHANGED");
        ValidateManifest(manifest, now);
        if (journal.Stage == "verified")
        {
            if (journal.PublicationCommit is null || journal.VerifiedRemoteCommit is null
                || journal.VerifiedAtUtc is null)
                throw new BridgeException("INVALID_PUBLICATION_JOURNAL");
            VerifyRemote(manifest, journal.PublicationCommit, now);
            return Receipt(manifest, journal.PublicationCommit, now, "already-published");
        }
        if (journal.Stage != "prepared")
            throw new BridgeException("PUBLICATION_OUTCOME_UNKNOWN_NO_RETRY");
        Safety.Utc(manifest.PreparedAtUtc, now);
        if (now - manifest.PreparedAtUtc > TimeSpan.FromHours(1))
            throw new BridgeException("PUBLICATION_APPROVAL_EXPIRED");
        AssertSourceIndexEmpty();
        AssertIndexUnchanged(manifest.ExpectedRemoteCommit);
        var bundle = ReadSourceBundle(manifest.ProjectId, now);
        var current = BuildManifest(bundle, manifest.ExpectedRemoteCommit, manifest.PreparedAtUtc);
        if (!CanonicalJson.Serialize(current).AsSpan().SequenceEqual(CanonicalJson.Serialize(manifest)))
            throw new BridgeException("PUBLICATION_CANDIDATE_CHANGED");
        if (transport.Head(git, target) != manifest.ExpectedRemoteCommit)
            throw new BridgeException("REMOTE_ADVANCED_RECONCILIATION_REQUIRED");
        var files = Files(bundle);
        if (manifest.Files.All(f => f.PreviousBlob is not null
            && git.Blob(manifest.ExpectedRemoteCommit, f.Path).AsSpan().SequenceEqual(files[f.Path])))
        {
            VerifyRemote(manifest, manifest.ExpectedRemoteCommit, now);
            SaveJournal("verified", digest, manifest.ExpectedRemoteCommit, now);
            return Receipt(manifest, manifest.ExpectedRemoteCommit, now, "already-published");
        }

        SaveJournal("committing", digest, null, null);
        foreach (var file in manifest.Files)
        {
            var blob = Encoding.UTF8.GetString(git.Run(["hash-object", "-w", "--stdin"], files[file.Path])).Trim();
            Safety.Sha(blob);
            git.Run(["update-index", "--add", "--cacheinfo", "100644," + blob + "," + file.Path]);
        }
        var staged = git.Text("diff", "--cached", "--no-ext-diff", "--no-textconv",
            "--name-only", manifest.ExpectedRemoteCommit, "--").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var expectedChanges = manifest.Files.Where(f => f.PreviousBlob is null
            || !git.Blob(manifest.ExpectedRemoteCommit, f.Path).AsSpan().SequenceEqual(files[f.Path]))
            .Select(f => f.Path).Order(StringComparer.Ordinal).ToArray();
        if (!staged.Order(StringComparer.Ordinal).SequenceEqual(expectedChanges))
            throw new BridgeException("UNEXPECTED_PUBLICATION_STAGED_PATHS");
        var tree = git.Text("write-tree");
        Safety.Sha(tree);
        var message = "docs(handoff): publish " + manifest.ProjectId + " snapshot\n\n"
            + "Publication-Manifest: " + digest + "\n"
            + "Source-Revision: " + manifest.Source.CommitSha + "\n\n"
            + "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>\n";
        var commit = Encoding.UTF8.GetString(git.Run([
            "-c", "user.name=HIC Snapshot Publisher",
            "-c", "user.email=hic-snapshot@localhost",
            "commit-tree", tree, "-p", manifest.ExpectedRemoteCommit], Encoding.UTF8.GetBytes(message))).Trim();
        Safety.Sha(commit);
        SaveJournal("commit-created", digest, commit, null);
        if (transport.Head(git, target) != manifest.ExpectedRemoteCommit)
            throw new BridgeException("REMOTE_ADVANCED_AFTER_COMMIT_NO_RETRY");
        SaveJournal("push-started", digest, commit, null);
        try
        {
            transport.Push(git, target, commit);
            VerifyRemote(manifest, commit, now);
        }
        catch (BridgeException)
        {
            SaveJournal("outcome-unknown", digest, commit, null);
            throw new BridgeException("PUBLICATION_OUTCOME_UNKNOWN_NO_RETRY");
        }
        catch (IOException)
        {
            SaveJournal("outcome-unknown", digest, commit, null);
            throw new BridgeException("PUBLICATION_OUTCOME_UNKNOWN_NO_RETRY");
        }
        SaveJournal("verified", digest, commit, now);
        return Receipt(manifest, commit, now, "published");
    }

    private void ValidateTarget()
    {
        Safety.Repository(sourceRepository);
        Safety.Repository(target.Repository);
        Safety.PublicationBranch(target.Branch);
        transport.ValidateTarget(target);
    }

    private void ValidateManifest(PublicationManifest manifest, DateTimeOffset now)
    {
        Safety.Project(manifest.ProjectId);
        Safety.Sha(manifest.SnapshotId, 64);
        Safety.Sha(manifest.TransportFingerprint, 64);
        Safety.Sha(manifest.ExpectedRemoteCommit);
        Safety.Utc(manifest.PreparedAtUtc, now);
        SnapshotProducer.ValidateSource(manifest.Source);
        if (manifest.ContractVersion != "hic.publication/1.0"
            || manifest.Source.Repository != sourceRepository || manifest.Files.Length != 2)
            throw new BridgeException("INVALID_PUBLICATION_MANIFEST");
        var paths = new[]
        {
            $"handoffs/{manifest.ProjectId}/latest.json",
            $"handoffs/{manifest.ProjectId}/snapshots/{manifest.SnapshotId}.json"
        };
        if (!manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal).SequenceEqual(paths))
            throw new BridgeException("UNAPPROVED_PUBLICATION_PATHS");
        foreach (var file in manifest.Files)
        {
            Safety.Sha(file.Sha256, 64);
            if (file.PreviousBlob is not null)
                Safety.Sha(file.PreviousBlob);
            if (file.SizeBytes is < 1 or > CanonicalJson.MaxBytes)
                throw new BridgeException("PUBLICATION_FILE_SIZE");
        }
    }

    private void AssertSourceIndexEmpty()
    {
        var sourceGit = new PublicationGit(sourceRoot);
        if (sourceGit.Text("diff", "--cached", "--no-ext-diff", "--no-textconv", "--name-only", "--").Length != 0)
            throw new BridgeException("UNEXPECTED_SOURCE_STAGED_FILES");
    }

    private void AssertIndexUnchanged(string expectedBase)
    {
        if (git.Text("rev-parse", "refs/heads/publication-base") != expectedBase
            || git.Text("diff", "--cached", "--no-ext-diff", "--no-textconv",
                "--name-only", expectedBase, "--").Length != 0)
            throw new BridgeException("UNEXPECTED_PUBLICATION_STAGED_FILES");
    }

    private sealed record Bundle(Snapshot Snapshot, byte[] SnapshotBytes, byte[] PointerBytes);

    private Bundle ReadSourceBundle(string projectId, DateTimeOffset now)
    {
        var sourceGit = new LocalGit(sourceRoot, sourceRepository);
        var source = sourceGit.ReadSource();
        var store = new ArtifactStore(sourceRoot);
        var snapshot = store.ReadLatest(projectId, source.CommitSha, now, TimeSpan.FromDays(7), sourceGit);
        var pointer = store.ReadBounded(Path.Combine(sourceRoot, "handoffs", projectId, "latest.json"));
        var bytes = store.ReadBounded(Path.Combine(sourceRoot, "handoffs", projectId, "snapshots", snapshot.SnapshotId + ".json"));
        var checkedSnapshot = ArtifactStore.ValidateBundle(projectId, pointer, bytes, now);
        if (checkedSnapshot.SnapshotId != snapshot.SnapshotId || checkedSnapshot.Source.Repository != sourceRepository)
            throw new BridgeException("LOCAL_CANDIDATE_CHANGED");
        return new(checkedSnapshot, bytes, pointer);
    }

    private static Dictionary<string, byte[]> Files(Bundle bundle) => new(StringComparer.Ordinal)
    {
        [$"handoffs/{bundle.Snapshot.ProjectId}/snapshots/{bundle.Snapshot.SnapshotId}.json"] = bundle.SnapshotBytes,
        [$"handoffs/{bundle.Snapshot.ProjectId}/latest.json"] = bundle.PointerBytes
    };

    private PublicationManifest BuildManifest(Bundle bundle, string expectedHead, DateTimeOffset preparedAt)
    {
        ValidateRemoteHistory(bundle, expectedHead, preparedAt);
        return new("hic.publication/1.0", target, transport.Fingerprint, expectedHead,
            bundle.Snapshot.Source, bundle.Snapshot.ProjectId, bundle.Snapshot.SnapshotId, preparedAt,
            Files(bundle).Select(f => new PublicationFile(f.Key, CanonicalJson.Hash(f.Value), f.Value.Length,
                git.BlobId(expectedHead, f.Key))).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray());
    }

    private void ValidateRemoteHistory(Bundle bundle, string head, DateTimeOffset now)
    {
        var snapshot = bundle.Snapshot;
        var snapshotPath = $"handoffs/{snapshot.ProjectId}/snapshots/{snapshot.SnapshotId}.json";
        var latestPath = $"handoffs/{snapshot.ProjectId}/latest.json";
        if (git.BlobId(head, snapshotPath) is not null
            && !git.Blob(head, snapshotPath).AsSpan().SequenceEqual(bundle.SnapshotBytes))
            throw new BridgeException("IMMUTABLE_REMOTE_SNAPSHOT_CONFLICT");
        if (git.BlobId(head, latestPath) is not null)
        {
            var pointerBytes = git.Blob(head, latestPath);
            var pointer = CanonicalJson.Parse<LatestPointer>(pointerBytes);
            Safety.Sha(pointer.SnapshotId, 64);
            var oldBytes = git.Blob(head, $"handoffs/{snapshot.ProjectId}/snapshots/{pointer.SnapshotId}.json");
            var previous = ArtifactStore.ValidateBundle(snapshot.ProjectId, pointerBytes, oldBytes, now);
            if (previous.Source.Repository != snapshot.Source.Repository
                || previous.Source.Branch != snapshot.Source.Branch
                || previous.SnapshotId != snapshot.SnapshotId && previous.ObservedAtUtc >= snapshot.ObservedAtUtc)
                throw new BridgeException("REMOTE_LATEST_RECONCILIATION_REQUIRED");
        }
        else if (git.Text("ls-tree", "-r", "--name-only", head, "--",
            $"handoffs/{snapshot.ProjectId}").Length != 0)
            throw new BridgeException("REMOTE_ORPHAN_HISTORY");
    }

    private void VerifyRemote(PublicationManifest manifest, string publicationCommit, DateTimeOffset now)
    {
        Safety.Sha(publicationCommit);
        if (transport.Head(git, target) != publicationCommit)
            throw new BridgeException("REMOTE_PUBLICATION_REVISION_MISMATCH");
        var files = manifest.Files;
        if (files.Length != 2)
            throw new BridgeException("INVALID_PUBLICATION_FILES");
        var bytes = files.Select(f =>
        {
            var data = git.Blob(publicationCommit, f.Path);
            if (CanonicalJson.Hash(data) != f.Sha256 || data.Length != f.SizeBytes)
                throw new BridgeException("REMOTE_PUBLICATION_INTEGRITY_MISMATCH");
            return (f.Path, Data: data);
        }).ToDictionary(f => f.Path, f => f.Data, StringComparer.Ordinal);
        var snapshotPath = $"handoffs/{manifest.ProjectId}/snapshots/{manifest.SnapshotId}.json";
        var pointerPath = $"handoffs/{manifest.ProjectId}/latest.json";
        if (!bytes.ContainsKey(snapshotPath) || !bytes.ContainsKey(pointerPath))
            throw new BridgeException("INVALID_PUBLICATION_FILES");
        _ = ArtifactStore.ValidateBundle(manifest.ProjectId, bytes[pointerPath], bytes[snapshotPath], now);
        if (transport.Head(git, target) != publicationCommit)
            throw new BridgeException("REMOTE_CHANGED_DURING_VERIFICATION");
    }

    private void SaveJournal(string stage, string digest, string? commit, DateTimeOffset? verifiedAt) =>
        workspace.Save(workspace.JournalPath, new PublicationJournal(stage, digest, commit,
            verifiedAt is not null ? commit : null, verifiedAt));

    public static string Digest(PublicationManifest manifest) => CanonicalJson.Hash(CanonicalJson.Serialize(manifest));

    private static PublicationReceipt Receipt(PublicationManifest manifest, string commit, DateTimeOffset now,
        string disposition) => new(disposition, manifest.Source.CommitSha,
            manifest.Files.Single(f => f.Path.Contains("/snapshots/", StringComparison.Ordinal)).Sha256,
            commit, commit, now);

    private static string Preview(PublicationManifest manifest, string digest, Snapshot snapshot) =>
        $"HIC publication preview (not authorization)\n"
        + $"Destination: {manifest.Destination.Repository} / {manifest.Destination.Branch}\n"
        + $"Expected remote: {manifest.ExpectedRemoteCommit}\n"
        + $"Source: {manifest.Source.Repository} / {manifest.Source.Branch} / {manifest.Source.CommitSha}\n"
        + $"Snapshot: {manifest.SnapshotId}; status={snapshot.ExecutionStatus} ({snapshot.ExecutionStatusConfidence})\n"
        + $"Summary: {snapshot.Summary}\n"
        + $"Usage: unknown; values null. Referenced contents are NOT exported.\n"
        + "Evidence: " + string.Join(", ", snapshot.VerificationEvidence.Select(e => $"{e.Kind}:{e.Status}/{e.Result}")) + "\n"
        + "Risks: " + string.Join(" ", snapshot.KnownRisks) + "\n"
        + "References: " + string.Join(", ", snapshot.ArtifactReferences.Select(a => a.Path + "@" + a.SourceCommit)) + "\n"
        + string.Join("\n", manifest.Files.Select(f => $"File: {f.Path}; bytes={f.SizeBytes}; sha256={f.Sha256}"))
        + $"\nManifest SHA-256: {digest}\n"
        + "Approval expires in one hour. Only these two artifact files will be committed; no source files or source history.\n";
}
