using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;

public class LocalCredentialTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AA==")]
    [InlineData("AQ==")]
    public void MissingOrInvalidHash_FailsClosed(string? hash)
    {
        Assert.Equal(PasswordVerificationResult.Failed, LocalCredentialStore.VerifyHash(hash, Guid.NewGuid().ToString()));
    }

    [Fact]
    public void MissingPassword_FailsClosed()
    {
        Assert.Equal(PasswordVerificationResult.Failed, LocalCredentialStore.VerifyHash(null, null));
    }

    [Fact]
    public void ExcessiveIterationCount_FailsClosed()
    {
        var encoded = new byte[64];
        encoded[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(5, 4), 1_000_001);
        Assert.Equal(PasswordVerificationResult.Failed,
            LocalCredentialStore.VerifyHash(Convert.ToBase64String(encoded), Guid.NewGuid().ToString()));
    }
}
