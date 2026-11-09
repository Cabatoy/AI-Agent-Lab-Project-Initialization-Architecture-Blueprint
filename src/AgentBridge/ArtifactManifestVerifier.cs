using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Hic.AgentBridge;

public static class ArtifactManifestVerifier
{
    public const string InputVersion = "hic.sandbox.input.v1";
    public const string OutputVersion = "hic.sandbox.output.v1";
    public const int MaxFiles = 100;
    public const long MaxFileBytes = 1024 * 1024;
    public const long MaxTotalBytes = 8 * 1024 * 1024;
    private static readonly Regex Device = new(
        @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static VerifiedArtifactManifest VerifyInput(
        byte[] json, ArtifactInputFields trusted, IReadOnlyDictionary<string, byte[]> bytes)
    {
        if (json is null) throw new BridgeException("INVALID_JSON");
        var manifest = CanonicalJson.Parse<ArtifactInputManifest>(json);
        ValidateInputFields(trusted);
        ValidateInputFields(manifest.Fields);
        var trustedHash = Digest(trusted);
        var hash = VerifyDigest(manifest.Fields, manifest.ManifestSha256);
        if (hash != trustedHash) throw new BridgeException("INPUT_BINDING_MISMATCH");
        var total = ValidateFiles(manifest.Fields.Files, bytes, manifest.Fields.Policy, null);
        return new(hash, manifest.Fields.Files.Length, total);
    }

    public static VerifiedArtifactManifest VerifyOutput(
        byte[] json, ArtifactInputFields trusted, string trustedInputDigest, IReadOnlyDictionary<string, byte[]> bytes)
    {
        ValidateInputFields(trusted);
        ValidateSha(trustedInputDigest, 64);
        if (Digest(trusted) != trustedInputDigest) throw new BridgeException("INPUT_BINDING_MISMATCH");
        if (json is null) throw new BridgeException("INVALID_JSON");
        var manifest = CanonicalJson.Parse<ArtifactOutputManifest>(json);
        var fields = manifest.Fields;
        if (fields.SchemaVersion != OutputVersion) throw new BridgeException("UNSUPPORTED_VERSION");
        if (fields.TaskId != trusted.TaskId || fields.RunId != trusted.RunId ||
            fields.ApprovalId != trusted.ApprovalId || fields.Repository != trusted.Repository ||
            fields.InstallationId != trusted.InstallationId || fields.BaseCommitSha != trusted.BaseCommitSha ||
            fields.InputManifestSha256 != trustedInputDigest)
            throw new BridgeException("OUTPUT_BINDING_MISMATCH");
        var hash = VerifyDigest(fields, manifest.ManifestSha256);
        var total = ValidateFiles(fields.Files, bytes, trusted.Policy, trusted.Policy.OutputRules);
        return new(hash, fields.Files.Length, total);
    }

    public static string Digest<T>(T fields) => CanonicalJson.Hash(CanonicalJson.Serialize(fields));

    private static string VerifyDigest<T>(T fields, string claimed)
    {
        ValidateSha(claimed, 64);
        var digest = Digest(fields);
        if (digest != claimed) throw new BridgeException("MANIFEST_HASH_MISMATCH");
        return digest;
    }

    private static void ValidateInputFields(ArtifactInputFields fields)
    {
        if (fields is null) throw new BridgeException("INVALID_BINDING");
        if (fields.SchemaVersion != InputVersion) throw new BridgeException("UNSUPPORTED_VERSION");
        if (fields.TaskId == Guid.Empty || fields.RunId == Guid.Empty || fields.ApprovalId == Guid.Empty ||
            fields.InstallationId <= 0) throw new BridgeException("INVALID_BINDING");
        if (fields.Repository is null) throw new BridgeException("INVALID_REPOSITORY");
        Safety.Repository(fields.Repository);
        ValidateSha(fields.BaseCommitSha, 40);
        ValidateSha(fields.SourceTreeSha256, 64);
        if (fields.Operation != "edit") throw new BridgeException("INVALID_OPERATION");
        var p = fields.Policy;
        if (p is null) throw new BridgeException("INVALID_POLICY");
        ValidateSha(p.ImageSha256, 64);
        if (p.Network != "none" || p.CpuMillis is < 1 or > 4000 ||
            p.MemoryBytes is < 1 or > 4294967296 || p.Pids is < 1 or > 256 ||
            p.TimeoutSeconds is < 1 or > 3600 || p.MaxFiles is < 1 or > MaxFiles ||
            p.MaxFileBytes is < 1 or > MaxFileBytes || p.MaxTotalBytes is < 1 or > MaxTotalBytes ||
            p.OutputRules.IsDefault || p.OutputRules.Length is < 1 or > MaxFiles)
            throw new BridgeException("INVALID_POLICY");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in p.OutputRules)
        {
            if (rule is null) throw new BridgeException("INVALID_POLICY");
            ValidatePath(rule.Path);
            AddPath(paths, rule.Path);
            if (rule.PermittedSha256.IsDefault || rule.PermittedSha256.Length is < 1 or > 16)
                throw new BridgeException("INVALID_HASH_ALLOWLIST");
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hash in rule.PermittedSha256)
            {
                ValidateSha(hash, 64);
                if (!hashes.Add(hash)) throw new BridgeException("INVALID_HASH_ALLOWLIST");
            }
        }
        ValidateFileMetadata(fields.Files, p);
    }

    private static long ValidateFiles(ImmutableArray<ArtifactFile> files, IReadOnlyDictionary<string, byte[]> bytes,
        ArtifactResourcePolicy policy, ImmutableArray<ArtifactPathRule>? rules)
    {
        if (files.IsDefault || files.Length > policy.MaxFiles || bytes is null || bytes.Count != files.Length ||
            rules is { } required && files.Length != required.Length)
            throw new BridgeException("FILE_COUNT");
        var total = ValidateFileMetadata(files, policy);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            AddPath(seen, file.Path);
            if (rules is { } allow)
            {
                var match = allow.FirstOrDefault(r => r.Path == file.Path);
                if (match is null) throw new BridgeException("PATH_NOT_ALLOWED");
                if (!match.PermittedSha256.Contains(file.Sha256)) throw new BridgeException("HASH_NOT_ALLOWED");
            }
            if (!bytes.TryGetValue(file.Path, out var content) || content is null ||
                content.LongLength != file.ByteLength || CanonicalJson.Hash(content) != file.Sha256)
                throw new BridgeException("CONTENT_MISMATCH");
        }
        if (bytes.Keys.Any(path => !seen.Contains(path) || !files.Any(f => f.Path == path)))
            throw new BridgeException("CONTENT_MISMATCH");
        return total;
    }

    private static long ValidateFileMetadata(ImmutableArray<ArtifactFile> files, ArtifactResourcePolicy policy)
    {
        if (files.IsDefault || files.Length > policy.MaxFiles) throw new BridgeException("FILE_COUNT");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files)
        {
            if (file is null) throw new BridgeException("INVALID_FILE_TYPE");
            ValidatePath(file.Path);
            AddPath(seen, file.Path);
            if (file.Type != "regular" || file.Mode != "100644") throw new BridgeException("INVALID_FILE_TYPE");
            if (file.ByteLength < 0 || file.ByteLength > policy.MaxFileBytes ||
                file.ByteLength > policy.MaxTotalBytes - total) throw new BridgeException("FILE_SIZE");
            total += file.ByteLength;
            ValidateSha(file.Sha256, 64);
        }
        return total;
    }

    private static void ValidateSha(string value, int length)
    {
        if (value is null) throw new BridgeException("INVALID_SHA");
        Safety.Sha(value, length);
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Any(c => c > 127) ||
            !Regex.IsMatch(path, @"^[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant))
            throw new BridgeException("INVALID_PATH");
        var segments = path.Split('/');
        if (segments.Length > 16 || segments.Any(s => s.Length is 0 or > 100 ||
            s is "." or ".." || s.EndsWith('.') || s.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            Device.IsMatch(s)))
            throw new BridgeException("INVALID_PATH");
    }

    private static void AddPath(HashSet<string> paths, string path)
    {
        if (!paths.Add(path)) throw new BridgeException("PATH_COLLISION");
        foreach (var other in paths)
            if (!string.Equals(path, other, StringComparison.OrdinalIgnoreCase) &&
                (path.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase) ||
                 other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                throw new BridgeException("PATH_COLLISION");
    }
}
