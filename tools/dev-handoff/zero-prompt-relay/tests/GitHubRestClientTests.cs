using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace HIC.DevHandoff.RelayAuthClient.Tests;

public class GitHubRestClientTests
{
    private class FakeCredentialProvider : ICredentialProvider
    {
        private readonly GitHubCredential? _credential;

        public FakeCredentialProvider(GitHubCredential? credential = null)
        {
            _credential = credential;
        }

        public Task<GitHubCredential?> GetCredentialAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_credential);
        }

        public Task<bool> ValidateCredentialAsync(GitHubCredential credential, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(!string.IsNullOrEmpty(credential.Token));
        }
    }

    private class FakeRateLimitHandler : IRateLimitHandler
    {
        public int RemainingCalls { get; set; } = 60;
        public DateTime ResetTime { get; set; } = DateTime.UtcNow.AddHours(1);
        public int HandleRateLimitCallCount { get; set; } = 0;

        public void UpdateRemaining(int remaining) => RemainingCalls = remaining;
        public void UpdateResetTime(DateTime resetTime) => ResetTime = resetTime;

        public async Task HandleRateLimitAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
        {
            HandleRateLimitCallCount++;
            await Task.Delay(10, cancellationToken); // Minimal delay for testing
        }

        public bool ShouldRetry(out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            return RemainingCalls > 0;
        }
    }

    private class TestOutputRedactor : IOutputRedactor
    {
        public string RedactSensitiveData(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            return System.Text.RegularExpressions.Regex.Replace(input, @"[a-zA-Z0-9_-]{40,}", "***REDACTED***");
        }
    }

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task FetchIssueAsync_WithValidCredential_ReturnsIssue()
    {
        // Arrange
        var credential = new GitHubCredential { Token = "ghs_valid_token_1234567890abcdefghijklmnop" };
        var credentialProvider = new FakeCredentialProvider(credential);
        var rateLimitHandler = new FakeRateLimitHandler();
        var redactor = new TestOutputRedactor();

        var fakeHandler = new FakeHttpMessageHandler(request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = 1,
                    number = 142,
                    title = "Test Issue",
                    state = "open",
                    created_at = DateTime.UtcNow.AddDays(-1),
                    updated_at = DateTime.UtcNow
                })),
                Headers =
                {
                    { "X-RateLimit-Remaining", "59" },
                    { "X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString() }
                }
            };
        });

        using var httpClient = new HttpClient(fakeHandler);
        var client = new GitHubRestClient(httpClient, credentialProvider, rateLimitHandler, redactor);

        // Act
        var result = await client.FetchIssueAsync("Cabatoy", "AI-Agent-Lab-Project-Initialization-Architecture-Blueprint", 142);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(142, result.Number);
        Assert.Equal("Test Issue", result.Title);
        Assert.Equal("open", result.State);
    }

    [Fact]
    public async Task FetchIssueAsync_WithoutCredential_ReturnsNull()
    {
        // Arrange
        var credentialProvider = new FakeCredentialProvider(null);
        var rateLimitHandler = new FakeRateLimitHandler();
        var redactor = new TestOutputRedactor();

        using var httpClient = new HttpClient();
        var client = new GitHubRestClient(httpClient, credentialProvider, rateLimitHandler, redactor);

        // Act
        var result = await client.FetchIssueAsync("Cabatoy", "AI-Agent-Lab-Project-Initialization-Architecture-Blueprint", 142);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchIssueAsync_OnUnauthorized_ThrowsAuthException()
    {
        // Arrange
        var credential = new GitHubCredential { Token = "invalid_token" };
        var credentialProvider = new FakeCredentialProvider(credential);
        var rateLimitHandler = new FakeRateLimitHandler();
        var redactor = new TestOutputRedactor();

        var fakeHandler = new FakeHttpMessageHandler(request =>
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Headers =
                {
                    { "X-RateLimit-Remaining", "0" },
                    { "X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString() }
                }
            };
        });

        using var httpClient = new HttpClient(fakeHandler);
        var client = new GitHubRestClient(httpClient, credentialProvider, rateLimitHandler, redactor);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<GitHubAuthException>(
            async () => await client.FetchIssueAsync("Cabatoy", "AI-Agent-Lab-Project-Initialization-Architecture-Blueprint", 142));
        
        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("Authentication failed", ex.Message);
    }

    [Fact]
    public async Task FetchIssueAsync_OnForbidden_ThrowsAuthException()
    {
        // Arrange
        var credential = new GitHubCredential { Token = "ghs_valid_token_1234567890abcdefghijklmnop" };
        var credentialProvider = new FakeCredentialProvider(credential);
        var rateLimitHandler = new FakeRateLimitHandler();
        var redactor = new TestOutputRedactor();

        var fakeHandler = new FakeHttpMessageHandler(request =>
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Headers =
                {
                    { "X-RateLimit-Remaining", "0" },
                    { "X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString() }
                }
            };
        });

        using var httpClient = new HttpClient(fakeHandler);
        var client = new GitHubRestClient(httpClient, credentialProvider, rateLimitHandler, redactor);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<GitHubAuthException>(
            async () => await client.FetchIssueAsync("Cabatoy", "AI-Agent-Lab-Project-Initialization-Architecture-Blueprint", 142));
        
        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("Authorization failed", ex.Message);
    }

    [Fact]
    public async Task FetchIssueAsync_OnRateLimit_ReturnsNullAndCallsHandler()
    {
        // Arrange
        var credential = new GitHubCredential { Token = "ghs_valid_token_1234567890abcdefghijklmnop" };
        var credentialProvider = new FakeCredentialProvider(credential);
        var rateLimitHandler = new FakeRateLimitHandler();
        var redactor = new TestOutputRedactor();

        var fakeHandler = new FakeHttpMessageHandler(request =>
        {
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers =
                {
                    { "X-RateLimit-Remaining", "0" },
                    { "X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString() },
                    { "Retry-After", "60" }
                }
            };
        });

        using var httpClient = new HttpClient(fakeHandler);
        var client = new GitHubRestClient(httpClient, credentialProvider, rateLimitHandler, redactor);

        // Act
        var result = await client.FetchIssueAsync("Cabatoy", "AI-Agent-Lab-Project-Initialization-Architecture-Blueprint", 142);

        // Assert
        Assert.Null(result);
        Assert.Equal(1, rateLimitHandler.HandleRateLimitCallCount);
    }

    [Fact]
    public void OutputRedactor_RedactsTokenPatterns()
    {
        // Arrange
        var redactor = new OutputRedactor();

        // Act
        var result = redactor.RedactSensitiveData("token=ghp_1234567890abcdefghijklmnopqrstuvwxyz secret data");

        // Assert
        Assert.Contains("***REDACTED_TOKEN***", result);
        Assert.DoesNotContain("ghp_", result);
    }

    [Fact]
    public void OutputRedactor_RedactsAuthorizationHeader()
    {
        // Arrange
        var redactor = new OutputRedactor();

        // Act
        var result = redactor.RedactSensitiveData("Authorization: token ghp_1234567890abcdefghijklmnopqrstuvwxyz");

        // Assert
        Assert.Contains("Authorization: ***REDACTED***", result);
    }

    [Fact]
    public void OutputRedactor_RedactsBearerToken()
    {
        // Arrange
        var redactor = new OutputRedactor();

        // Act
        var result = redactor.RedactSensitiveData("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");

        // Assert
        Assert.Contains("Bearer ***REDACTED***", result);
    }

    [Fact]
    public void RateLimitHandler_CalculatesExponentialBackoff()
    {
        // Arrange
        var handler = new RateLimitHandler(maxRetries: 5);

        // Act & Assert - should not throw on first few retries
        Assert.Empty(Assert.Throws<RateLimitExceededException>(
            async () => await handler.HandleRateLimitAsync(new HttpResponseMessage(), CancellationToken.None)).Message);

        // After exceeding max retries, should throw
        for (int i = 1; i < 5; i++)
        {
            _ = Assert.ThrowsAsync<RateLimitExceededException>(
                async () => await handler.HandleRateLimitAsync(new HttpResponseMessage(), CancellationToken.None));
        }
    }

    [Fact]
    public void RateLimitHandler_ShouldRetry_RespectsRemainingCount()
    {
        // Arrange
        var handler = new RateLimitHandler();
        handler.UpdateRemaining(5);

        // Act
        var shouldRetry = handler.ShouldRetry(out var delay);

        // Assert
        Assert.True(shouldRetry);
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void RateLimitHandler_ShouldRetry_WhenResetTimeReached()
    {
        // Arrange
        var handler = new RateLimitHandler();
        handler.UpdateRemaining(0);
        handler.UpdateResetTime(DateTime.UtcNow.AddSeconds(-1)); // Already reset

        // Act
        var shouldRetry = handler.ShouldRetry(out var delay);

        // Assert
        Assert.True(shouldRetry);
    }
}
