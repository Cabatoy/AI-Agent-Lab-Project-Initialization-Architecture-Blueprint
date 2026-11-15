using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Pure, side-effect-free contract. This does NOT enqueue work or grant execution.
internal static class RelayHandoffIntent
{
    internal const string Version = "hic.relay-handoff/1";
    internal const string Scope = "staging:observe-only";
    private const string DigestDomain = "hic-relay-handoff-intent-v1";

    internal static string Digest(
        Guid taskId,
        string title,
        string description,
        DateTime taskUpdatedAtUtc,
        string repository,
        string expectedBaseRef)
    {
        if (taskId == Guid.Empty || string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(expectedBaseRef) ||
            taskUpdatedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Invalid immutable handoff snapshot.");

        var fields = new[]
        {
            DigestDomain,
            Version,
            Scope,
            taskId.ToString("N"),
            taskUpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            title,
            description ?? throw new ArgumentNullException(nameof(description)),
            repository,
            expectedBaseRef
        };
        var canonical = string.Concat(fields.Select(field =>
            Encoding.UTF8.GetByteCount(field).ToString(CultureInfo.InvariantCulture) + ":" + field));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static bool Matches(string? suppliedDigest, string expectedDigest)
    {
        if (!IsLowerHexDigest(suppliedDigest) || !IsLowerHexDigest(expectedDigest))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(suppliedDigest!),
            Convert.FromHexString(expectedDigest));
    }

    internal static bool IsValidIdempotencyKey(string? key) =>
        key is not null &&
        Regex.IsMatch(key, "^[A-Za-z0-9_-]{32,128}$", RegexOptions.CultureInvariant);

    private static bool IsLowerHexDigest(string? digest) =>
        digest is not null &&
        Regex.IsMatch(digest, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
}
