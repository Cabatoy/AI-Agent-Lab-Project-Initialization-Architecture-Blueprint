public class UserIdentityStoreTests
{
    [Theory]
    [InlineData("  Alice.Example@Example.COM  ", "alice.example@example.com")]
    [InlineData("USER@EXAMPLE.TEST", "user@example.test")]
    public void EmailNormalization_TrimsAndIgnoresCase(string input, string expected)
    {
        Assert.Equal(expected, UserIdentityStore.NormalizeEmail(input));
    }

    [Theory]
    [InlineData(" Alice ", "alice")]
    [InlineData("USER_NAME", "user_name")]
    public void UsernameNormalization_TrimsAndIgnoresCase(string input, string expected)
    {
        Assert.Equal(expected, UserIdentityStore.NormalizeUsername(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("alice@@example.test")]
    [InlineData("alice @example.test")]
    [InlineData("alice@example .test")]
    [InlineData("alice@.example.test")]
    [InlineData("alice@exa_mple.test")]
    [InlineData("alice@-example.test")]
    public async Task InvalidEmail_FailsBeforeDatabaseAccess(string? email)
    {
        var result = await UserIdentityStore.CreateAsync("invalid-connection-string", email, "alice");

        Assert.Equal(UserIdentityCreateOutcome.InvalidIdentifier, result.Outcome);
        Assert.Null(result.Identity);
    }
}
