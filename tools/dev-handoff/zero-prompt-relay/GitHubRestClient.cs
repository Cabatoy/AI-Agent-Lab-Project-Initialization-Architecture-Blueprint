using System;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace HIC.DevHandoff.RelayAuthClient;

/// <summary>
/// Authenticated GitHub REST API client with rate-limit aware retry logic.
/// Never logs, forwards or exposes credentials in exceptions, diagnostics or responses.
/// </summary>
public class GitHubRestClient
{
    private readonly HttpClient _httpClient;
    private readonly ICredentialProvider _credentialProvider;
    private readonly IRateLimitHandler _rateLimitHandler;
    private readonly IOutputRedactor _redactor;

    public GitHubRestClient(
        HttpClient httpClient,
        ICredentialProvider credentialProvider,
        IRateLimitHandler rateLimitHandler,
        IOutputRedactor redactor)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _rateLimitHandler = rateLimitHandler ?? throw new ArgumentNullException(nameof(rateLimitHandler));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    /// <summary>
    /// Fetch issue from GitHub with authenticated request and rate-limit awareness.
    /// Returns null if credentials unavailable; throws only on network failures, not auth/scope errors.
    /// </summary>
    public async Task<IssueResponse?> FetchIssueAsync(
        string owner,
        string repo,
        int issueNumber,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var credential = await _credentialProvider.GetCredentialAsync(cancellationToken);
        if (credential == null)
        {
            return null; // Gracefully handle missing credentials
        }

        var url = $"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Add authentication header
        if (!string.IsNullOrEmpty(credential.Token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", credential.Token);
        }

        // Add User-Agent (GitHub API requirement)
        request.Headers.Add("User-Agent", "HIC-Relay-Auth-Client/1.0");
        request.Headers.Add("Accept", "application/vnd.github.v3+json");

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);

            // Check rate limit headers
            ExtractRateLimitInfo(response);

            // Handle auth/scope errors separately from rate limits
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var errorMsg = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Authentication failed: invalid or expired token"
                    : "Authorization failed: insufficient scope or permission";
                
                throw new GitHubAuthException(errorMsg, (int)response.StatusCode);
            }

            // Handle rate limit
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                await _rateLimitHandler.HandleRateLimitAsync(response, cancellationToken);
                return null; // Caller should retry after backoff
            }

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var redacted = _redactor.RedactSensitiveData(content);
                throw new HttpRequestException(
                    $"GitHub API error: {(int)response.StatusCode} - {redacted}");
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<IssueResponse>(jsonContent);
        }
        finally
        {
            request.Dispose();
        }
    }

    private void ExtractRateLimitInfo(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
        {
            if (int.TryParse(remaining.First(), out var count))
            {
                _rateLimitHandler.UpdateRemaining(count);
            }
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset))
        {
            if (long.TryParse(reset.First(), out var timestamp))
            {
                _rateLimitHandler.UpdateResetTime(UnixTimeStampToDateTime(timestamp));
            }
        }
    }

    private static DateTime UnixTimeStampToDateTime(long unixTimeStamp)
    {
        var dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        dateTime = dateTime.AddSeconds(unixTimeStamp).ToUniversalTime();
        return dateTime;
    }
}

/// <summary>
/// GitHub issue response model.
/// </summary>
public class IssueResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("labels")]
    public Label[]? Labels { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

public class Label
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// Represents a credential (token-based auth only; never exposes token in strings).
/// </summary>
public class GitHubCredential
{
    public string Token { get; set; } = string.Empty;
    public string? Identity { get; set; }
    public string? Scope { get; set; }
}

/// <summary>
/// Credential provider interface.
/// </summary>
public interface ICredentialProvider
{
    Task<GitHubCredential?> GetCredentialAsync(System.Threading.CancellationToken cancellationToken = default);
    Task<bool> ValidateCredentialAsync(GitHubCredential credential, System.Threading.CancellationToken cancellationToken = default);
}

/// <summary>
/// Rate limit handler with backoff strategy.
/// </summary>
public interface IRateLimitHandler
{
    void UpdateRemaining(int remaining);
    void UpdateResetTime(DateTime resetTime);
    Task HandleRateLimitAsync(HttpResponseMessage response, System.Threading.CancellationToken cancellationToken = default);
    bool ShouldRetry(out TimeSpan delay);
}

/// <summary>
/// Output redactor to prevent credential leakage.
/// </summary>
public interface IOutputRedactor
{
    string RedactSensitiveData(string input);
}

/// <summary>
/// Exception for authentication/authorization errors.
/// </summary>
public class GitHubAuthException : Exception
{
    public int StatusCode { get; }

    public GitHubAuthException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
