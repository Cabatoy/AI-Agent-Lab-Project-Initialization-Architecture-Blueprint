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
        if (taskId == Guid.Empty || !IsSafeTaskText(title, 256) || !IsSafeTaskText(description, 16384) ||
            !IsSafeRepository(repository) || !IsSafeBaseRef(expectedBaseRef) ||
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

    // Bound untrusted task snapshots and reject control characters before canonicalization.
    // Task content remains data, never an instruction or execution authorization.
    private static bool IsSafeTaskText(string? value, int maxLength) =>
        value is not null && !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength && !value.Any(char.IsControl);

    // Restrict identifiers before they reach GitHub APIs or downstream shell arguments.
    // These checks do not authorize access to the named repository or ref.
    private static bool IsSafeRepository(string? repository) =>
        repository is not null &&
        Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant) &&
        repository.Split('/').All(part => part is not "." and not ".." && !part.StartsWith('.'));

    private static bool IsSafeBaseRef(string? reference) =>
        reference is not null && reference.Length <= 200 &&
        Regex.IsMatch(reference, @"^[A-Za-z0-9_][A-Za-z0-9_./-]*$", RegexOptions.CultureInvariant) &&
        !reference.Contains("..", StringComparison.Ordinal) &&
        !reference.Contains("//", StringComparison.Ordinal) &&
        !reference.EndsWith('/') && !reference.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
        !reference.Split('/').Any(part => part.StartsWith('.'));

    private static bool IsLowerHexDigest(string? digest) =>
        digest is not null &&
        Regex.IsMatch(digest, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
}
