using System.Security.Cryptography;
using System.Text;
using Xunit;

public sealed class NativeApprovalReceiptV2VerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);

    private static SignedNativeApprovalReceipt Unsigned() => new(
        Version: 2,
        Issuer: NativeApprovalReceiptVerifier.Issuer,
        Audience: NativeApprovalReceiptVerifier.Audience,
        KeyId: "local-issuer-public-2026",
        ApprovalId: Guid.NewGuid(),
        TaskId: Guid.NewGuid(),
        RunId: Guid.NewGuid(),
        ActorId: Guid.NewGuid(),
        TaskVersion: 3,
        Action: NativeRunAdmissionPolicy.ReadOnlyAction,
        Scope: NativeRunAdmissionPolicy.ReadOnlyScope,
        IntentDigestSha256: new string('a', 64),
        ApprovedUtc: Now.AddSeconds(-15),
        ExpiresUtc: Now.AddMinutes(2),
        SignatureBase64Url: "");

    // Test-only issuer; production Native Host contains NO private signing key.
    private static SignedNativeApprovalReceipt Sign(ECDsa issuer, SignedNativeApprovalReceipt input)
    {
        var signature = issuer.SignData(
            Encoding.UTF8.GetBytes(NativeApprovalReceiptV2Verifier.CanonicalPayload(input)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.Equal(64, signature.Length);
        return input with {
            SignatureBase64Url = Convert.ToBase64String(signature)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_')
        };
    }

    [Fact]
    public void ValidP256Signature_VerifiesWithPublicKeyOnly()
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var receipt = Sign(issuer, Unsigned());
        var publicKey = issuer.ExportSubjectPublicKeyInfo();
        Assert.True(NativeApprovalReceiptV2Verifier.Verify(
            receipt, publicKey, receipt.KeyId, Now));
        // The same receipt is still replayable. PostgreSQL v3 must consume the claim.
        Assert.True(NativeApprovalReceiptV2Verifier.Verify(receipt, publicKey, receipt.KeyId, Now));
        Assert.False(NativeApprovalReceiptVerifier.Verify(
            receipt, Enumerable.Repeat((byte)1, 32).ToArray(), receipt.KeyId, Now));
    }

    [Fact]
    public void MissingWrongOrInvalidPublicKey_FailsClosed()
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var receipt = Sign(issuer, Unsigned());
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(receipt, null, receipt.KeyId, Now));
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(receipt, new byte[100], receipt.KeyId, Now));
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(receipt, other.ExportSubjectPublicKeyInfo(), receipt.KeyId, Now));
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(receipt, wrongCurve.ExportSubjectPublicKeyInfo(), receipt.KeyId, Now));
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(receipt, issuer.ExportSubjectPublicKeyInfo(), "other-key", Now));
    }

    [Theory]
    [InlineData("task")]
    [InlineData("run")]
    [InlineData("actor")]
    [InlineData("approval")]
    [InlineData("revision")]
    [InlineData("digest")]
    [InlineData("scope")]
    [InlineData("action")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("keyid")]
    public void TamperingBoundFields_DoesNotValidate(string field)
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Sign(issuer, Unsigned());
        var changed = field switch {
            "task" => signed with { TaskId = Guid.NewGuid() },
            "run" => signed with { RunId = Guid.NewGuid() },
            "actor" => signed with { ActorId = Guid.NewGuid() },
            "approval" => signed with { ApprovalId = Guid.NewGuid() },
            "revision" => signed with { TaskVersion = signed.TaskVersion + 1 },
            "digest" => signed with { IntentDigestSha256 = new string('b', 64) },
            "scope" => signed with { Scope = "workspace:all:write" },
            "action" => signed with { Action = "shell.run" },
            "audience" => signed with { Audience = "github-issue" },
            "issuer" => signed with { Issuer = "untrusted" },
            "keyid" => signed with { KeyId = "different-key" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(
            changed, issuer.ExportSubjectPublicKeyInfo(), signed.KeyId, Now));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("tooLong")]
    [InlineData("subsecond")]
    [InlineData("wrongVersion")]
    [InlineData("wrongAction")]
    [InlineData("wrongScope")]
    [InlineData("invalidKeyId")]
    [InlineData("invalidDigest")]
    public void EvenCorrectlySignedOutOfPolicyReceipt_IsRejected(string problem)
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var input = Unsigned();
        input = problem switch {
            "expired" => input with { ExpiresUtc = Now },
            "future" => input with { ApprovedUtc = Now.AddSeconds(1) },
            "tooLong" => input with { ExpiresUtc = input.ApprovedUtc.AddMinutes(6) },
            "subsecond" => input with { ApprovedUtc = input.ApprovedUtc.AddMilliseconds(10) },
            "wrongVersion" => input with { Version = 1 },
            "wrongAction" => input with { Action = "shell.run" },
            "wrongScope" => input with { Scope = "workspace:*:write" },
            "invalidKeyId" => input with { KeyId = "key\ninjection" },
            "invalidDigest" => input with { IntentDigestSha256 = new string('G', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };
        var signed = Sign(issuer, input);
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(
            signed, issuer.ExportSubjectPublicKeyInfo(), input.KeyId, Now));
    }

    [Fact]
    public void MalformedSignatureAndDowngradeToHmacV1_AreRejected()
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var r = Sign(issuer, Unsigned());
        var publicKey = issuer.ExportSubjectPublicKeyInfo();
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(r with { SignatureBase64Url = "not-base64" }, publicKey, r.KeyId, Now));
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(r with { SignatureBase64Url = "" }, publicKey, r.KeyId, Now));
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(r with { SignatureBase64Url = new string('A', 86) }, publicKey, r.KeyId, Now));
        Assert.False(NativeApprovalReceiptV2Verifier.Verify(r with { Version = 1 }, publicKey, r.KeyId, Now));
    }
}
