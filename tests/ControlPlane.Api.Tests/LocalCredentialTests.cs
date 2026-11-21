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
}
