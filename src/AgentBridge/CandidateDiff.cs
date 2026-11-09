using System.Text;

namespace Hic.AgentBridge;

public sealed record CandidateDiffPreview(string Path, string SourceSha256, string CandidateSha256,
    int FirstChangedLine, int RemovedLineCount, int AddedLineCount, string RemovedText, string AddedText);

public static class CandidateDiff
{
    public const int MaxInputBytes = 64 * 1024;
    public const int MaxChangedBytes = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CandidateDiffPreview Preview(
        QuarantinedCandidate candidate, string path, byte[] trustedSource, string trustedSourceSha256)
    {
        if (candidate is null || trustedSource is null || trustedSourceSha256 is null)
            throw new BridgeException("INVALID_DIFF_INPUT");
        ArtifactManifestVerifier.ValidatePath(path);
        Safety.Sha(trustedSourceSha256, 64);
        var file = candidate.Files.SingleOrDefault(item => item.Path == path)
            ?? throw new BridgeException("PATH_NOT_ALLOWED");
        var output = file.CopyBytes();
        if (trustedSource.Length > MaxInputBytes || output.Length > MaxInputBytes)
            throw new BridgeException("DIFF_SIZE");
        var source = (byte[])trustedSource.Clone();
        if (CanonicalJson.Hash(source) != trustedSourceSha256 || CanonicalJson.Hash(output) != file.Sha256)
            throw new BridgeException("CONTENT_MISMATCH");

        string before, after;
        try
        {
            before = StrictUtf8.GetString(source);
            after = StrictUtf8.GetString(output);
        }
        catch (DecoderFallbackException)
        {
            throw new BridgeException("DIFF_ENCODING");
        }
        if (before.Contains('\0') || after.Contains('\0')) throw new BridgeException("DIFF_ENCODING");
        var beforeLines = before.Split('\n');
        var afterLines = after.Split('\n');
        var prefix = 0;
        while (prefix < beforeLines.Length && prefix < afterLines.Length && beforeLines[prefix] == afterLines[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < beforeLines.Length - prefix && suffix < afterLines.Length - prefix &&
            beforeLines[beforeLines.Length - 1 - suffix] == afterLines[afterLines.Length - 1 - suffix])
            suffix++;
        var removed = string.Join('\n', beforeLines.Skip(prefix).Take(beforeLines.Length - prefix - suffix));
        var added = string.Join('\n', afterLines.Skip(prefix).Take(afterLines.Length - prefix - suffix));
        if (StrictUtf8.GetByteCount(removed) + StrictUtf8.GetByteCount(added) > MaxChangedBytes)
            throw new BridgeException("DIFF_SIZE");
        return new CandidateDiffPreview(path, trustedSourceSha256, file.Sha256, prefix + 1,
            beforeLines.Length - prefix - suffix, afterLines.Length - prefix - suffix, removed, added);
    }
}
