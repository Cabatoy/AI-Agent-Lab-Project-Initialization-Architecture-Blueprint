using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// UNWIRED: a cryptographic primitive, NOT authenticated approval issuance.
// No HTTP endpoint, private key loading, task transition, Worker or execution.
// A future CP service MUST verify human identity and durable approval from DB
// before calling this helper. Client fields must never mint an approval.
internal sealed record NativeApprovalReceiptV2Envelope(
    int Version, string Issuer, string Audience, string KeyId,
    Guid ApprovalId, Guid TaskId, Guid RunId, Guid ActorId,
    long TaskVersion, string Action, string Scope, string IntentDigestSha256,
    DateTimeOffset ApprovedUtc, DateTimeOffset ExpiresUtc, string SignatureBase64Url);

internal static class NativeApprovalReceiptV2Issuer
{
    internal const string Domain = "hic-native-approval-receipt-v2-ecdsa-p256-sha256";
    private const string P256Oid = "1.2.840.10045.3.1.7";

    internal static NativeApprovalReceiptV2Envelope? Sign(
        NativeApprovalReceiptV2Envelope? r, ECDsa? privateKey, string? authorizedKeyId, DateTimeOffset now)
    {
        if (r is null || privateKey is null || r.Version != 2 ||
            !string.Equals(r.Issuer, "hic-control-plane", StringComparison.Ordinal) ||
            !string.Equals(r.Audience, "hic-native-host", StringComparison.Ordinal) ||
            !ValidKeyId(authorizedKeyId) || !string.Equals(r.KeyId, authorizedKeyId, StringComparison.Ordinal) ||
            r.ApprovalId == Guid.Empty || r.TaskId == Guid.Empty || r.RunId == Guid.Empty ||
            r.ActorId == Guid.Empty || r.TaskVersion < 1 ||
            !string.Equals(r.Action, "workspace.inspect", StringComparison.Ordinal) ||
            !string.Equals(r.Scope, "workspace:configured:read", StringComparison.Ordinal) ||
            !ValidDigest(r.IntentDigestSha256) || r.SignatureBase64Url is not "" ||
            r.ApprovedUtc == default || r.ApprovedUtc > now ||
            r.ExpiresUtc <= now || r.ExpiresUtc <= r.ApprovedUtc ||
            r.ExpiresUtc - r.ApprovedUtc > TimeSpan.FromMinutes(5) ||
            !WholeSecond(r.ApprovedUtc) || !WholeSecond(r.ExpiresUtc))
            return null;

        try
        {
            if (privateKey.KeySize != 256 || privateKey.ExportParameters(false).Curve.Oid.Value != P256Oid)
                return null;
            var sig = privateKey.SignData(Encoding.UTF8.GetBytes(CanonicalPayload(r)),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (sig.Length != 64) return null;
            return r with { SignatureBase64Url = Convert.ToBase64String(sig)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') };
        }
        catch (CryptographicException) { return null; }
    }

    internal static string CanonicalPayload(NativeApprovalReceiptV2Envelope r) =>
        string.Join('\n', Domain, r.Version.ToString(CultureInfo.InvariantCulture),
            r.Issuer, r.Audience, r.KeyId, r.ApprovalId.ToString("N"),
            r.TaskId.ToString("N"), r.RunId.ToString("N"), r.ActorId.ToString("N"),
            r.TaskVersion.ToString(CultureInfo.InvariantCulture), r.Action, r.Scope,
            r.IntentDigestSha256, r.ApprovedUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            r.ExpiresUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    private static bool WholeSecond(DateTimeOffset x) =>
        x.ToUniversalTime() == DateTimeOffset.FromUnixTimeSeconds(x.ToUnixTimeSeconds());
    private static bool ValidDigest(string? x) =>
        x is { Length: 64 } && x.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidKeyId(string? x) =>
        !string.IsNullOrEmpty(x) && x.Length <= 64 &&
        x.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_');
}
