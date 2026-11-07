using System.Security.Cryptography;
using System.Text;
using Xunit;

public sealed class NativeApprovalReceiptVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 15, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)(i + 41)).ToArray();

    private static SignedNativeApprovalReceipt ValidUnsigned() => new(
        Version: 1, Issuer: NativeApprovalReceiptVerifier.Issuer,
        Audience: NativeApprovalReceiptVerifier.Audience, KeyId: "local-ci-key-v1",
        ApprovalId: Guid.NewGuid(), TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(),
        ActorId: Guid.NewGuid(), TaskVersion: 3,
        Action: NativeRunAdmissionPolicy.ReadOnlyAction,
        Scope: NativeRunAdmissionPolicy.ReadOnlyScope,
        IntentDigestSha256: new string('a', 64),
        ApprovedUtc: Now.AddSeconds(-15), ExpiresUtc: Now.AddMinutes(2),
        SignatureBase64Url: "");

    // Synthetic test issuer only. The Native Host exposes no production issuer or signing endpoint.
    private static SignedNativeApprovalReceipt Sign(SignedNativeApprovalReceipt receipt, byte[]? key = null)
    {
        var mac = HMACSHA256.HashData(key ?? Secret,
            Encoding.UTF8.GetBytes(NativeApprovalReceiptVerifier.CanonicalPayload(receipt)));
        var sig = Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return receipt with { SignatureBase64Url = sig };
    }

    private static bool Verify(SignedNativeApprovalReceipt receipt, byte[]? key = null, string? keyId = "local-ci-key-v1",
        DateTimeOffset? now = null) =>
        NativeApprovalReceiptVerifier.Verify(receipt, key ?? Secret, keyId, now ?? Now);

    [Fact]
    public void ValidSyntheticSignedApproval_PassesCryptographicVerificationOnly()
    {
        Assert.True(Verify(Sign(ValidUnsigned())));
    }

    [Fact]
    public void MissingOrShortKeyAndMissingReceipt_FailClosed()
    {
        var receipt = Sign(ValidUnsigned());
        Assert.False(NativeApprovalReceiptVerifier.Verify(null, Secret, "local-ci-key-v1", Now));
        Assert.False(NativeApprovalReceiptVerifier.Verify(receipt, null, "local-ci-key-v1", Now));
        Assert.False(NativeApprovalReceiptVerifier.Verify(receipt, new byte[12], "local-ci-key-v1", Now));
        Assert.False(Verify(receipt, keyId: null));
        Assert.False(Verify(receipt, keyId: "wrong-key"));
        Assert.False(Verify(receipt, key: Enumerable.Repeat((byte)4, 32).ToArray()));
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("task")]
    [InlineData("run")]
    [InlineData("actor")]
    [InlineData("version")]
    [InlineData("action")]
    [InlineData("scope")]
    [InlineData("digest")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("keyid")]
    public void FieldTampering_WithoutResigning_InvalidatesMac(string field)
    {
        var original = Sign(ValidUnsigned());
        var tampered = field switch
        {
            "approval" => original with { ApprovalId = Guid.NewGuid() },
            "task" => original with { TaskId = Guid.NewGuid() },
            "run" => original with { RunId = Guid.NewGuid() },
            "actor" => original with { ActorId = Guid.NewGuid() },
            "version" => original with { TaskVersion = original.TaskVersion + 1 },
            "action" => original with { Action = "shell.run" },
            "scope" => original with { Scope = "workspace:*:write" },
            "digest" => original with { IntentDigestSha256 = new string('b', 64) },
            "issuer" => original with { Issuer = "github-issue" },
            "audience" => original with { Audience = "external-client" },
            "keyid" => original with { KeyId = "different-key" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.False(Verify(tampered));
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("scope")]
    [InlineData("extraLifetime")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("badDigest")]
    [InlineData("badKeyId")]
    [InlineData("subsecond")]
    public void EvenACorrectlyResignedOutOfPolicyReceipt_IsRejected(string invalid)
    {
        var receipt = ValidUnsigned();
        receipt = invalid switch
        {
            "shell" => receipt with { Action = "shell.run" },
            "scope" => receipt with { Scope = "workspace:all:write" },
            "extraLifetime" => receipt with { ExpiresUtc = receipt.ApprovedUtc.AddMinutes(6) },
            "expired" => receipt with { ExpiresUtc = Now },
            "future" => receipt with { ApprovedUtc = Now.AddSeconds(1), ExpiresUtc = Now.AddMinutes(2) },
            "badDigest" => receipt with { IntentDigestSha256 = new string('G', 64) },
            "badKeyId" => receipt with { KeyId = "key\ninjection" },
            "subsecond" => receipt with { ApprovedUtc = receipt.ApprovedUtc.AddMilliseconds(12) },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        Assert.False(Verify(Sign(receipt)));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void MalformedOrIncorrectSignature_IsRejected(string invalid)
    {
        var receipt = Sign(ValidUnsigned()) with { SignatureBase64Url = invalid };
        Assert.False(Verify(receipt));
    }

    [Fact]
    public void SignatureVerificationIsNotAReplayClaim()
    {
        var receipt = Sign(ValidUnsigned());
        Assert.True(Verify(receipt));
        Assert.True(Verify(receipt));
        // A stateless signature is reusable; the v3 PostgreSQL UNIQUE claim must
        // still be acquired atomically and consumed before future execution.
    }
}
