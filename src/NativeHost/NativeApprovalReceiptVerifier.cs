using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// Verification primitive ONLY. No issuer, signing-key store, HTTP route, task dispatch
// or process execution exists here. A verified signature is not a durable run claim.
internal sealed record SignedNativeApprovalReceipt(
    int Version,
    string Issuer,
    string Audience,
    string KeyId,
    Guid ApprovalId,
    Guid TaskId,
    Guid RunId,
    Guid ActorId,
    long TaskVersion,
    string Action,
    string Scope,
    string IntentDigestSha256,
    DateTimeOffset ApprovedUtc,
    DateTimeOffset ExpiresUtc,
    string SignatureBase64Url);

internal static class NativeApprovalReceiptVerifier
{
    internal const int ProtocolVersion = 1;
    internal const string Issuer = "hic-control-plane";
    internal const string Audience = "hic-native-host";
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    internal static bool Verify(
        SignedNativeApprovalReceipt? receipt,
        byte[]? trustedIssuerKey,
        string? trustedKeyId,
        DateTimeOffset now)
    {
        if (receipt is null || trustedIssuerKey is null || trustedIssuerKey.Length < 32 ||
            !ValidKeyId(trustedKeyId) || !string.Equals(receipt.KeyId, trustedKeyId, StringComparison.Ordinal) ||
            receipt.Version != ProtocolVersion ||
            !string.Equals(receipt.Issuer, Issuer, StringComparison.Ordinal) ||
            !string.Equals(receipt.Audience, Audience, StringComparison.Ordinal) ||
            receipt.ApprovalId == Guid.Empty || receipt.TaskId == Guid.Empty ||
            receipt.RunId == Guid.Empty || receipt.ActorId == Guid.Empty || receipt.TaskVersion < 1 ||
            !string.Equals(receipt.Action, NativeRunAdmissionPolicy.ReadOnlyAction, StringComparison.Ordinal) ||
            !string.Equals(receipt.Scope, NativeRunAdmissionPolicy.ReadOnlyScope, StringComparison.Ordinal) ||
            !LowercaseSha256(receipt.IntentDigestSha256) ||
            receipt.ApprovedUtc == default || receipt.ApprovedUtc > now || now >= receipt.ExpiresUtc ||
            receipt.ExpiresUtc <= receipt.ApprovedUtc ||
            receipt.ExpiresUtc - receipt.ApprovedUtc > MaxLifetime ||
            !WholeSecondUtc(receipt.ApprovedUtc) || !WholeSecondUtc(receipt.ExpiresUtc) ||
            !ValidSignatureShape(receipt.SignatureBase64Url))
            return false;

        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(
                receipt.SignatureBase64Url.Replace('-', '+').Replace('_', '/') + "=");
        }
        catch (FormatException)
        {
            return false;
        }
        if (supplied.Length != 32) return false;

        var expected = HMACSHA256.HashData(
            trustedIssuerKey, Encoding.UTF8.GetBytes(CanonicalPayload(receipt)));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    // v1 canonical format: fixed 15 newline-separated ASCII fields, including
    // a domain-separation header. All strings are constrained to fixed constants
    // or ASCII allowlists; a caller cannot inject separators.
    internal static string CanonicalPayload(SignedNativeApprovalReceipt receipt) =>
        string.Join('\n',
            "hic-native-approval-receipt-v1",
            receipt.Version.ToString(CultureInfo.InvariantCulture),
            receipt.Issuer,
            receipt.Audience,
            receipt.KeyId,
            receipt.ApprovalId.ToString("N"),
            receipt.TaskId.ToString("N"),
            receipt.RunId.ToString("N"),
            receipt.ActorId.ToString("N"),
            receipt.TaskVersion.ToString(CultureInfo.InvariantCulture),
            receipt.Action,
            receipt.Scope,
            receipt.IntentDigestSha256,
            receipt.ApprovedUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            receipt.ExpiresUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    private static bool WholeSecondUtc(DateTimeOffset date) =>
        date.ToUniversalTime() == DateTimeOffset.FromUnixTimeSeconds(date.ToUnixTimeSeconds());

    private static bool LowercaseSha256(string? digest) =>
        digest is { Length: 64 } && digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidKeyId(string? keyId) =>
        !string.IsNullOrEmpty(keyId) && keyId.Length <= 64 &&
        keyId.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_');

    private static bool ValidSignatureShape(string? value) =>
        value is { Length: 43 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');
}
