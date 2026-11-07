namespace Hic.AgentBridge;

public sealed class ArtifactStore(string repositoryRoot)
{
    private readonly string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));

    public string Write(Snapshot snapshot, DateTimeOffset now)
    {
        SnapshotProducer.ValidateStored(snapshot, now);
        var snapshotBytes = CanonicalJson.Serialize(snapshot);
        var project = ProjectDirectory(snapshot.ProjectId);
        CheckLinks(project);
        CheckLinks(Path.Combine(project, "snapshots"));
        Directory.CreateDirectory(project);
        var lockPath = Path.Combine(project, ".write.lock");
        CheckLinks(lockPath);
        using var writeLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.DeleteOnClose);
        var snapshotPath = SnapshotPath(snapshot.ProjectId, snapshot.SnapshotId);
        CheckLinks(snapshotPath);
        if (File.Exists(snapshotPath))
            throw new BridgeException("DUPLICATE_SNAPSHOT");
        var latestPath = Path.Combine(project, "latest.json");
        CheckLinks(latestPath);
        if (File.Exists(latestPath))
        {
            var previous = ReadValidated(snapshot.ProjectId, now);
            if (snapshot.ObservedAtUtc < previous.ObservedAtUtc)
                throw new BridgeException("LATEST_WOULD_REGRESS");
        }
        else if (Directory.Exists(Path.GetDirectoryName(snapshotPath))
            && Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(snapshotPath)!).Any())
        {
            throw new BridgeException("ORPHAN_STATE_REQUIRES_INSPECTION");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        using (var file = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(snapshotBytes);
            file.Flush(flushToDisk: true);
        }
        var pointer = new LatestPointer(SnapshotProducer.Version, snapshot.ProjectId, snapshot.SnapshotId,
            $"handoffs/{snapshot.ProjectId}/snapshots/{snapshot.SnapshotId}.json",
            CanonicalJson.Hash(snapshotBytes), snapshot.Source.CommitSha, snapshot.ObservedAtUtc, null);
        var temp = Path.Combine(project, $"latest.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(CanonicalJson.Serialize(pointer));
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, latestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        return pointer.SnapshotPath;
    }

    public Snapshot ReadLatest(string projectId, string expectedSource, DateTimeOffset now,
        TimeSpan maxAge, ILocalGit git)
    {
        Safety.Sha(expectedSource);
        if (maxAge <= TimeSpan.Zero || maxAge > TimeSpan.FromDays(7))
            throw new BridgeException("INVALID_FRESHNESS_LIMIT");
        var source = git.ReadSource();
        SnapshotProducer.ValidateSource(source);
        if (source.CommitSha != expectedSource)
            throw new BridgeException("SOURCE_COMMIT_MISMATCH");
        var snapshot = ReadValidated(projectId, now);
        if (snapshot.Source.CommitSha != expectedSource || snapshot.Source.Branch != source.Branch
            || now - snapshot.ObservedAtUtc > maxAge)
            throw new BridgeException("STALE_SNAPSHOT");
        foreach (var artifact in snapshot.ArtifactReferences)
        {
            var bytes = git.ReadApprovedBlob(expectedSource, artifact.Path);
            if (bytes.LongLength != artifact.SizeBytes || CanonicalJson.Hash(bytes) != artifact.Sha256)
                throw new BridgeException("ARTIFACT_INTEGRITY_MISMATCH");
        }
        return snapshot;
    }

    private Snapshot ReadValidated(string projectId, DateTimeOffset now)
    {
        var project = ProjectDirectory(projectId);
        var pointerBytes = ReadBounded(Path.Combine(project, "latest.json"));
        var pointer = CanonicalJson.Parse<LatestPointer>(pointerBytes);
        Safety.Sha(pointer.SnapshotId, 64);
        return ValidateBundle(projectId, pointerBytes, ReadBounded(SnapshotPath(projectId, pointer.SnapshotId)), now);
    }

    public static Snapshot ValidateBundle(string projectId, byte[] pointerBytes, byte[] bytes, DateTimeOffset now)
    {
        Safety.Project(projectId);
        var pointer = CanonicalJson.Parse<LatestPointer>(pointerBytes);
        Safety.Sha(pointer.SnapshotId, 64);
        Safety.Sha(pointer.Sha256, 64);
        var expectedPath = $"handoffs/{projectId}/snapshots/{pointer.SnapshotId}.json";
        if (pointer.ContractVersion != SnapshotProducer.Version || pointer.ProjectId != projectId
            || pointer.SnapshotPath != expectedPath || pointer.PublishedAtUtc is not null)
            throw new BridgeException("INVALID_LATEST_POINTER");
        var snapshot = CanonicalJson.Parse<Snapshot>(bytes);
        SnapshotProducer.ValidateStored(snapshot, now);
        if (pointer.Sha256 != CanonicalJson.Hash(bytes) || snapshot.ProjectId != projectId
            || snapshot.SnapshotId != pointer.SnapshotId || snapshot.Source.CommitSha != pointer.SourceCommit
            || snapshot.ObservedAtUtc != pointer.ObservedAtUtc
            || !pointerBytes.AsSpan().SequenceEqual(CanonicalJson.Serialize(pointer))
            || !bytes.AsSpan().SequenceEqual(CanonicalJson.Serialize(snapshot)))
            throw new BridgeException("LATEST_INTEGRITY_MISMATCH");
        return snapshot;
    }

    public byte[] ReadBounded(string path)
    {
        CheckLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > CanonicalJson.MaxBytes)
            throw new BridgeException("PAYLOAD_SIZE");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private string ProjectDirectory(string projectId)
    {
        Safety.Project(projectId);
        return Path.Combine(root, "handoffs", projectId);
    }

    private string SnapshotPath(string projectId, string snapshotId)
    {
        Safety.Sha(snapshotId, 64);
        return Path.Combine(ProjectDirectory(projectId), "snapshots", snapshotId + ".json");
    }

    private void CheckLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new BridgeException("PATH_OUTSIDE_REPOSITORY");
        for (var item = new FileInfo(full); item is not null; item = item.Directory is null ? null : new FileInfo(item.Directory.FullName))
        {
            if (File.Exists(item.FullName) || Directory.Exists(item.FullName))
            {
                if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("LINK_NOT_ALLOWED");
            }
        }
    }
}
