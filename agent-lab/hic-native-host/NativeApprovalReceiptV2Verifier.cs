using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// UNWIRED v2 verifier. The trusted key is a pinned PUBLIC SPKI P-256 key.
// Unlike the v1 HMAC prototype, the Native Host never needs a signing secret.
// This checks cryptography and format, NOT user identity, revocation or a DB run claim.
internal static class NativeApprovalReceiptV2Verifier
{
    internal const int Version = 2;
    internal const string Domain = "hic-native-approval-receipt-v2-ecdsa-p256-sha256";
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    internal static bool Verify(
        SignedNativeApprovalReceipt? receipt,
        byte[]? trustedPublicKeySpki,
        string? trustedKeyId,
        DateTimeOffset now)
    {
        if (receipt is null || trustedPublicKeySpki is not { Length: >= 80 and <= 256 } ||
            !ValidKeyId(trustedKeyId) ||
            !string.Equals(receipt.KeyId, trustedKeyId, StringComparison.Ordinal) ||
            receipt.Version != Version ||
            !string.Equals(receipt.Issuer, NativeApprovalReceiptVerifier.Issuer, StringComparison.Ordinal) ||
            !string.Equals(receipt.Audience, NativeApprovalReceiptVerifier.Audience, StringComparison.Ordinal) ||
            receipt.ApprovalId == Guid.Empty || receipt.TaskId == Guid.Empty ||
            receipt.RunId == Guid.Empty || receipt.ActorId == Guid.Empty ||
            receipt.TaskVersion < 1 ||
            !string.Equals(receipt.Action, NativeRunAdmissionPolicy.ReadOnlyAction, StringComparison.Ordinal) ||
            !string.Equals(receipt.Scope, NativeRunAdmissionPolicy.ReadOnlyScope, StringComparison.Ordinal) ||
            !ValidLowercaseDigest(receipt.IntentDigestSha256) ||
            receipt.ApprovedUtc == default || receipt.ApprovedUtc > now ||
            now >= receipt.ExpiresUtc || receipt.ExpiresUtc <= receipt.ApprovedUtc ||
            receipt.ExpiresUtc - receipt.ApprovedUtc > MaxLifetime ||
            !IsWholeUtcSecond(receipt.ApprovedUtc) || !IsWholeUtcSecond(receipt.ExpiresUtc) ||
            !ValidSignatureShape(receipt.SignatureBase64Url))
            return false;

        // A P-256 ECDSA signature in IEEE P1363 format contains exactly 64 bytes:
        // r(32) || s(32). Strict URL-safe base64 without padding is 86 chars.
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(
                receipt.SignatureBase64Url.Replace('-', '+').Replace('_', '/') + "==");
        }
        catch (FormatException)
        {
            return false;
        }
        if (signature.Length != 64) return false;

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(trustedPublicKeySpki, out var consumed);
            if (consumed != trustedPublicKeySpki.Length ||
                key.KeySize != 256 ||
                key.ExportParameters(false).Curve.Oid.Value != P256Oid)
                return false;

            return key.VerifyData(
                Encoding.UTF8.GetBytes(CanonicalPayload(receipt)),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            // Malformed/unsupported public keys and signatures fail closed.
            return false;
        }
    }

    internal static string CanonicalPayload(SignedNativeApprovalReceipt receipt) =>
        string.Join('\n',
            Domain,
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

    private static bool IsWholeUtcSecond(DateTimeOffset value) =>
        value.ToUniversalTime() == DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    private static bool ValidLowercaseDigest(string? digest) =>
        digest is { Length: 64 } &&
        digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidKeyId(string? keyId) =>
        !string.IsNullOrEmpty(keyId) && keyId.Length <= 64 &&
        keyId.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_');

    private static bool ValidSignatureShape(string? value) =>
        value is { Length: 86 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');
}
