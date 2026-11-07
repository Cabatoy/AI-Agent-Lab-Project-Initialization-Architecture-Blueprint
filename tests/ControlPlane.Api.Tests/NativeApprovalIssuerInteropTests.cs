using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace ControlPlane.Api.Tests;

public sealed class NativeApprovalIssuerInteropTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);
    private static NativeApprovalReceiptV2Envelope Candidate() => new(
        2, "hic-control-plane", "hic-native-host", "ci-public-key-2026",
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5,
        "workspace.inspect", "workspace:configured:read", new string('a', 64),
        Now.AddSeconds(-10), Now.AddMinutes(2), "");

    [Fact]
    public void SignedByControlPlane_VerifiesAsNativeHostJsonUsingOnlyPublicKey()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var receipt = NativeApprovalReceiptV2Issuer.Sign(Candidate(), signer, "ci-public-key-2026", Now);
        Assert.NotNull(receipt);
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var host = JsonSerializer.Deserialize<SignedNativeApprovalReceipt>(JsonSerializer.Serialize(receipt, opts), opts);
        Assert.NotNull(host);
        Assert.Equal(NativeApprovalReceiptV2Issuer.CanonicalPayload(receipt),
            NativeApprovalReceiptV2Verifier.CanonicalPayload(host));
        Assert.True(NativeApprovalReceiptV2Verifier.Verify(
            host, signer.ExportSubjectPublicKeyInfo(), receipt.KeyId, Now));
    }

    [Theory]
    [InlineData("action")]
    [InlineData("scope")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("expiry")]
    [InlineData("actor")]
    [InlineData("digest")]
    [InlineData("presigned")]
    public void InvalidCandidate_CannotBeSigned(string caseName)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var c = Candidate();
        c = caseName switch {
            "action" => c with { Action = "shell.run" },
            "scope" => c with { Scope = "all:write" },
            "issuer" => c with { Issuer = "untrusted-browser" },
            "audience" => c with { Audience = "github" },
            "version" => c with { Version = 1 },
            "revision" => c with { TaskVersion = 0 },
            "expiry" => c with { ExpiresUtc = c.ApprovedUtc.AddMinutes(6) },
            "actor" => c with { ActorId = Guid.Empty },
            "digest" => c with { IntentDigestSha256 = new string('G',64) },
            "presigned" => c with { SignatureBase64Url = "client-provided" },
            _ => throw new ArgumentOutOfRangeException(nameof(caseName))
        };
        Assert.Null(NativeApprovalReceiptV2Issuer.Sign(c, signer, c.KeyId, Now));
    }

    [Fact]
    public void PublicOnlyKeyOrWrongCurveCannotSign()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var publicOnly = ECDsa.Create();
        publicOnly.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _);
        var c = Candidate();
        Assert.Null(NativeApprovalReceiptV2Issuer.Sign(c, null, c.KeyId, Now));
        Assert.Null(NativeApprovalReceiptV2Issuer.Sign(c, signer, "other", Now));
        Assert.Null(NativeApprovalReceiptV2Issuer.Sign(c, other, c.KeyId, Now));
        Assert.Null(NativeApprovalReceiptV2Issuer.Sign(c, publicOnly, c.KeyId, Now));
    }
}
