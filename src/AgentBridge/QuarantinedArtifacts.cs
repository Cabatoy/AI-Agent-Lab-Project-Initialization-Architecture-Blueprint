namespace Hic.AgentBridge;

public sealed record ArtifactCandidateBinding(
    Guid TaskId, Guid RunId, Guid DecisionId, string Repository, long InstallationId,
    string BaseCommitSha, string InputManifestSha256);

public sealed record TrustedCandidateFile(string Path, string Type, string Mode, long ByteLength, string Sha256, byte[] Bytes);

public sealed record ArtifactAcceptance(
    Guid DecisionId, ArtifactCandidateBinding Binding, string CandidateSha256);

public sealed class CandidateArtifact
{
    private readonly byte[] content;

    internal CandidateArtifact(string path, byte[] bytes, string sha256)
    {
        Path = path;
        content = bytes;
        Sha256 = sha256;
    }

    public string Path { get; }
    public string Sha256 { get; }
    public long ByteLength => content.LongLength;
    public byte[] CopyBytes() => (byte[])content.Clone();
}

public sealed class QuarantinedCandidate
{
    internal QuarantinedCandidate(ArtifactCandidateBinding binding, IReadOnlyList<CandidateArtifact> files, string sha256)
    {
        Binding = binding;
        Files = Array.AsReadOnly(files.ToArray());
        Sha256 = sha256;
    }

    public ArtifactCandidateBinding Binding { get; }
    public IReadOnlyList<CandidateArtifact> Files { get; }
    public string Sha256 { get; }
}

public sealed class AcceptedArtifact
{
    internal AcceptedArtifact(QuarantinedCandidate candidate, Guid decisionId)
    {
        Candidate = candidate;
        DecisionId = decisionId;
    }

    public QuarantinedCandidate Candidate { get; }
    public Guid DecisionId { get; }
}

public static class QuarantinedArtifacts
{
    public static QuarantinedCandidate Import(
        byte[] trustedInputManifest, ArtifactInputFields trustedInput, IReadOnlyDictionary<string, byte[]> sourceBytes,
        IReadOnlyList<TrustedCandidateFile> files)
    {
        var verified = ArtifactManifestVerifier.VerifyInput(trustedInputManifest, trustedInput, sourceBytes);
        var binding = new ArtifactCandidateBinding(trustedInput.TaskId, trustedInput.RunId, trustedInput.ApprovalId,
            trustedInput.Repository, trustedInput.InstallationId, trustedInput.BaseCommitSha, verified.ManifestSha256);
        if (files is null || files.Count > trustedInput.Policy.MaxFiles || files.Count != trustedInput.Policy.OutputRules.Length)
            throw new BridgeException("FILE_COUNT");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imported = new List<CandidateArtifact>(files.Count);
        long total = 0;
        foreach (var file in files)
        {
            if (file is null) throw new BridgeException("INVALID_FILE_TYPE");
            ArtifactManifestVerifier.ValidatePath(file.Path);
            ArtifactManifestVerifier.AddPath(seen, file.Path);
            if (file.Type != "regular" || file.Mode != "100644") throw new BridgeException("INVALID_FILE_TYPE");
            if (!trustedInput.Policy.OutputRules.Any(rule => rule.Path == file.Path))
                throw new BridgeException("PATH_NOT_ALLOWED");
            if (file.Bytes is null || file.ByteLength < 0 || file.ByteLength > trustedInput.Policy.MaxFileBytes ||
                file.ByteLength > trustedInput.Policy.MaxTotalBytes - total || file.Bytes.LongLength != file.ByteLength)
                throw new BridgeException("FILE_SIZE");
            if (file.Sha256 is null) throw new BridgeException("INVALID_SHA");
            Safety.Sha(file.Sha256, 64);
            var copy = (byte[])file.Bytes.Clone();
            if (CanonicalJson.Hash(copy) != file.Sha256) throw new BridgeException("CONTENT_MISMATCH");
            total += file.ByteLength;
            imported.Add(new CandidateArtifact(file.Path, copy, file.Sha256));
        }
        var ordered = imported.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        var digest = CanonicalJson.Hash(CanonicalJson.Serialize(new CandidateDigest(
            "hic.quarantined-candidate.v1", binding,
            ordered.Select(file => new CandidateDigestFile(file.Path, file.ByteLength, file.Sha256)).ToArray())));
        return new QuarantinedCandidate(binding, ordered, digest);
    }

    public static AcceptedArtifact Accept(QuarantinedCandidate candidate, ArtifactAcceptance authorization)
    {
        if (candidate is null || authorization is null || authorization.DecisionId == Guid.Empty ||
            authorization.DecisionId == candidate.Binding.DecisionId ||
            authorization.Binding != candidate.Binding || authorization.CandidateSha256 != candidate.Sha256)
            throw new BridgeException("ACCEPTANCE_BINDING_MISMATCH");
        foreach (var file in candidate.Files)
            if (CanonicalJson.Hash(file.CopyBytes()) != file.Sha256)
                throw new BridgeException("CONTENT_MISMATCH");
        return new AcceptedArtifact(candidate, authorization.DecisionId);
    }

    public static void ImportFromUntrustedFilesystem() =>
        throw new BridgeException("UNSAFE_FILESYSTEM_BACKEND");

    private sealed record CandidateDigest(string SchemaVersion, ArtifactCandidateBinding Binding, CandidateDigestFile[] Files);
    private sealed record CandidateDigestFile(string Path, long ByteLength, string Sha256);
}
