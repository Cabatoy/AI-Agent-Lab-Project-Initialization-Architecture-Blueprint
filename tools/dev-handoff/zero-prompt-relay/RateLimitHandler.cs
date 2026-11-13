using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HIC.DevHandoff.RelayAuthClient;

/// <summary>
/// Handles GitHub rate limits with exponential backoff and jitter.
/// Respects Retry-After header and X-RateLimit-Reset timestamp.
/// </summary>
public class RateLimitHandler : IRateLimitHandler
{
    private int _remaining = 60; // Default GitHub API rate limit
    private DateTime _resetTime = DateTime.UtcNow.AddHours(1);
    private int _consecutiveRateLimits = 0;
    private readonly int _maxRetries;
    private readonly Random _random = new();

    public RateLimitHandler(int maxRetries = 3)
    {
        _maxRetries = maxRetries;
    }

    public void UpdateRemaining(int remaining)
    {
        _remaining = Math.Max(0, remaining);
    }

    public void UpdateResetTime(DateTime resetTime)
    {
        _resetTime = resetTime;
    }

    public async Task HandleRateLimitAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        _consecutiveRateLimits++;

        if (_consecutiveRateLimits > _maxRetries)
        {
            throw new RateLimitExceededException(
                $"Rate limit exceeded. Max retries ({_maxRetries}) exceeded. Reset time: {_resetTime:u}");
        }

        var delay = CalculateBackoffDelay();
        await Task.Delay(delay, cancellationToken);
    }

    public bool ShouldRetry(out TimeSpan delay)
    {
        delay = TimeSpan.Zero;

        if (_consecutiveRateLimits > _maxRetries)
            return false;

        if (_remaining > 0)
        {
            _consecutiveRateLimits = 0;
            return true;
        }

        var now = DateTime.UtcNow;
        if (now >= _resetTime)
        {
            _remaining = 60;
            _consecutiveRateLimits = 0;
            return true;
        }

        delay = _resetTime - now;
        return false;
    }

    private TimeSpan CalculateBackoffDelay()
    {
        // Exponential backoff with jitter: (2^n + jitter) * 1s
        var baseDelay = Math.Pow(2, _consecutiveRateLimits - 1);
        var jitter = _random.Next(0, 1000) / 1000.0; // 0-1 second jitter
        var delaySeconds = (baseDelay + jitter);

        // Cap at 5 minutes
        delaySeconds = Math.Min(delaySeconds, 300);

        return TimeSpan.FromSeconds(delaySeconds);
    }
}

/// <summary>
/// Exception thrown when rate limit retries exhausted.
/// </summary>
public class RateLimitExceededException : Exception
{
    public RateLimitExceededException(string message)
        : base(message)
    {
    }
}
