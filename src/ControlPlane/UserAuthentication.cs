using System.Security.Claims;
using System.Threading.RateLimiting;
using Dapper;
using Microsoft.AspNetCore.Antiforgery;
using Npgsql;

internal sealed record HicUserLoginRequest(string? Identifier, string? Password);

internal static class HicUserAuthenticator
{
    internal static async Task<UserIdentity?> AuthenticateAsync(string connectionString,
        string? identifier, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1024)
        {
            await LocalCredentialStore.VerifyAsync(connectionString, Guid.Empty, password, cancellationToken);
            return null;
        }

        var identity = await UserIdentityStore.FindByIdentifierAsync(connectionString, identifier, cancellationToken);
        return await LocalCredentialStore.VerifyAsync(connectionString, identity?.Id ?? Guid.Empty,
            password, cancellationToken)
            ? identity
            : null;
    }
}

internal sealed class HicUserSessionStore(string connectionString)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    internal async Task CreateAsync(Guid sessionId, Guid userId, DateTime expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(new CommandDefinition(@"
            insert into hic_user_sessions (id, user_id, expires_at_utc)
            values (@sessionId, @userId, @expiresAtUtc);",
            new { sessionId, userId, expiresAtUtc }, cancellationToken: cancellationToken));
    }

    internal async Task<bool> IsActiveAsync(Guid sessionId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
            select exists (
                select 1 from hic_user_sessions
                where id = @sessionId and user_id = @userId
                  and revoked_at_utc is null and expires_at_utc > now()
            );",
            new { sessionId, userId }, cancellationToken: cancellationToken));
    }

    internal async Task<bool> RevokeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteAsync(new CommandDefinition(@"
            update hic_user_sessions
            set revoked_at_utc = now()
            where id = @sessionId and revoked_at_utc is null;",
            new { sessionId }, cancellationToken: cancellationToken)) == 1;
    }

    internal static bool TryGetSession(ClaimsPrincipal principal, out Guid sessionId, out Guid userId)
    {
        var hasSession = Guid.TryParse(principal.FindFirstValue(HicUserSession.SessionClaim), out sessionId);
        var hasUser = Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
        return hasSession && sessionId != Guid.Empty && hasUser && userId != Guid.Empty;
    }
}

internal static class HicUserSession
{
    internal const string Scheme = "HicUserCookie";
    internal const string SessionClaim = "hic_session_id";
    internal const string CookieName = "hic-user-session";
    internal const string AntiforgeryCookieName = "hic-antiforgery";
    internal const string CsrfHeaderName = "X-HIC-CSRF";

    internal static async Task<UserIdentity?> GetCurrentIdentityAsync(string connectionString,
        ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            userId == Guid.Empty)
            return null;

        return await UserIdentityStore.FindByIdAsync(connectionString, userId, cancellationToken);
    }
}

internal static class HicAuthRequestGuard
{
    internal static string? NormalizeTrustedOrigin(string? configured, bool isDevelopment)
    {
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            (!isDevelopment && origin.Scheme != Uri.UriSchemeHttps) ||
            (origin.Scheme == Uri.UriSchemeHttp && !origin.IsLoopback))
            return null;

        return origin.GetLeftPart(UriPartial.Authority);
    }

    internal static bool HasTrustedOrigin(HttpRequest request, string? trustedOrigin)
    {
        if (trustedOrigin is null ||
            !Uri.TryCreate(request.Headers.Origin, UriKind.Absolute, out var origin) ||
            !Uri.TryCreate(trustedOrigin, UriKind.Absolute, out var trusted) ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            return false;

        var fetchSite = request.Headers["Sec-Fetch-Site"];
        return string.Equals(origin.GetLeftPart(UriPartial.Authority), trusted.GetLeftPart(UriPartial.Authority),
                   StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(fetchSite) || string.Equals(fetchSite, "same-origin", StringComparison.Ordinal));
    }

    internal static async Task<bool> HasValidCsrfAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}

internal static class HicLoginRateLimit
{
    internal static RateLimitPartition<string> ForRequest(HttpContext context) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });

    internal static PartitionedRateLimiter<HttpContext> CreateLimiter() =>
        PartitionedRateLimiter.Create<HttpContext, string>(ForRequest);
}

internal static class HicUserSessionSchema
{
    internal static string MigrationSql
    {
        get
        {
            using var stream = typeof(HicUserSessionSchema).Assembly.GetManifestResourceStream("HicUserSessionMigration")
                ?? throw new InvalidOperationException("HIC user session migration resource is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
