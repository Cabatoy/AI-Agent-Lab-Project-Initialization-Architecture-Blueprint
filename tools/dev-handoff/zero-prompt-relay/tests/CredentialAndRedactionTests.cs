using System;
using System.Threading.Tasks;
using Xunit;

namespace HIC.DevHandoff.RelayAuthClient.Tests;

public class GitCredentialManagerProviderTests
{
    [Fact]
    public async Task GetCredentialAsync_WithNoGCM_ReturnsNull()
    {
        // Arrange
        var redactor = new OutputRedactor();
        var missingGit = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"), "git");
        var provider = new GitCredentialManagerProvider(redactor, "github.com", missingGit);

        // Act
        var result = await provider.GetCredentialAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateCredentialAsync_WithValidToken_ReturnsTrue()
    {
        // Arrange
        var redactor = new OutputRedactor();
        var provider = new GitCredentialManagerProvider(redactor);
        var credential = new GitHubCredential { Token = "ghs_valid_token_1234567890abcdefghijklmnop" };

        // Act
        var result = await provider.ValidateCredentialAsync(credential);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ValidateCredentialAsync_WithEmptyToken_ReturnsFalse()
    {
        // Arrange
        var redactor = new OutputRedactor();
        var provider = new GitCredentialManagerProvider(redactor);
        var credential = new GitHubCredential { Token = string.Empty };

        // Act
        var result = await provider.ValidateCredentialAsync(credential);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ValidateCredentialAsync_WithShortToken_ReturnsFalse()
    {
        // Arrange
        var redactor = new OutputRedactor();
        var provider = new GitCredentialManagerProvider(redactor);
        var credential = new GitHubCredential { Token = "short" };

        // Act
        var result = await provider.ValidateCredentialAsync(credential);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ValidateCredentialAsync_WithTokenContainingSpace_ReturnsFalse()
    {
        // Arrange
        var redactor = new OutputRedactor();
        var provider = new GitCredentialManagerProvider(redactor);
        var credential = new GitHubCredential { Token = "ghs_invalid token with spaces 1234567890" };

        // Act
        var result = await provider.ValidateCredentialAsync(credential);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ValidateCredentialAsync_WithNullToken_ReturnsFalse()
    {
        // Arrange
        var redactor = new OutputRedactor();
        var provider = new GitCredentialManagerProvider(redactor);
        var credential = new GitHubCredential { Token = null! };

        // Act
        var result = await provider.ValidateCredentialAsync(credential);

        // Assert
        Assert.False(result);
    }
}

public class OutputRedactorTests
{
    [Fact]
    public void RedactSensitiveData_RemovesLongHexTokens()
    {
        // Arrange
        var redactor = new OutputRedactor();
        var input = "token abcdef1234567890abcdef1234567890abcdef";

        // Act
        var result = redactor.RedactSensitiveData(input);

        // Assert
        Assert.Contains("***REDACTED_TOKEN***", result);
        Assert.DoesNotContain("abcdef1234567890", result);
    }

    [Fact]
    public void RedactSensitiveData_RemovesEqualsPrefixedHexToken()
    {
        var result = new OutputRedactor().RedactSensitiveData("token=abcdef1234567890abcdef1234567890");
        Assert.Contains("***REDACTED_TOKEN***", result);
        Assert.DoesNotContain("abcdef1234567890", result);
    }

    [Theory]
    [InlineData("Authorization: Bearer synthetic-short", "synthetic-short")]
    [InlineData("authorization: token synthetic-short", "synthetic-short")]
    public void RedactSensitiveData_RemovesAuthorizationCredentials(string input, string credential)
    {
        var result = new OutputRedactor().RedactSensitiveData(input);
        Assert.Contains("Authorization: ***REDACTED***", result);
        Assert.DoesNotContain(credential, result);
    }

    [Fact]
    public void RedactSensitiveData_PreservesShortStrings()
    {
        // Arrange
        var redactor = new OutputRedactor();
        var input = "short token here";

        // Act
        var result = redactor.RedactSensitiveData(input);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void RedactSensitiveData_HandlesEmptyString()
    {
        // Arrange
        var redactor = new OutputRedactor();

        // Act
        var result = redactor.RedactSensitiveData(string.Empty);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void RedactSensitiveData_HandlesNullString()
    {
        // Arrange
        var redactor = new OutputRedactor();

        // Act
        var result = redactor.RedactSensitiveData(null!);

        // Assert
        Assert.Null(result);
    }
}

public class RateLimitHandlerTests
{
    [Fact]
    public void UpdateRemaining_SetsCorrectValue()
    {
        // Arrange
        var handler = new RateLimitHandler();

        // Act
        handler.UpdateRemaining(42);

        // Assert
        // We can verify through ShouldRetry behavior
        var shouldRetry = handler.ShouldRetry(out _);
        Assert.True(shouldRetry);
    }

    [Fact]
    public void UpdateRemaining_CapsAtZero()
    {
        // Arrange
        var handler = new RateLimitHandler();

        // Act
        handler.UpdateRemaining(-5);

        // Assert
        var shouldRetry = handler.ShouldRetry(out _);
        Assert.False(shouldRetry);
    }

    [Fact]
    public void UpdateResetTime_SetsCorrectValue()
    {
        // Arrange
        var handler = new RateLimitHandler();
        handler.UpdateRemaining(0);
        var resetTime = DateTime.UtcNow.AddMinutes(5);

        // Act
        handler.UpdateResetTime(resetTime);

        // Assert
        var shouldRetry = handler.ShouldRetry(out var delay);
        Assert.False(shouldRetry);
        Assert.True(delay > TimeSpan.Zero && delay <= TimeSpan.FromMinutes(5.1));
    }

    [Fact]
    public async Task ShouldRetry_ReturnsFalseWhenExceedingMaxRetries()
    {
        // Arrange
        var handler = new RateLimitHandler(maxRetries: 2);
        handler.UpdateRemaining(0);

        // Act - simulate 3 failed attempts
        var calls = 0;
        while (calls < 3)
        {
            try
            {
                await handler.HandleRateLimitAsync(new System.Net.Http.HttpResponseMessage(), default);
            }
            catch (RateLimitExceededException)
            {
                // Expected after max retries
                break;
            }
            calls++;
        }

        var shouldRetry = handler.ShouldRetry(out _);

        // Assert
        Assert.False(shouldRetry);
    }
}
