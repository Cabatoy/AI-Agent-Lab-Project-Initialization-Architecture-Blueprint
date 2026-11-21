using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ControlPlane.Api.Tests;

public class UserAuthenticationTests
{
    [Fact]
    public void TrustedOrigin_RequiresExactConfiguredOriginAndSameOriginFetch()
    {
        var trusted = HicAuthRequestGuard.NormalizeTrustedOrigin("https://dashboard.example.test", isDevelopment: false);
        Assert.Equal("https://dashboard.example.test", trusted);
        Assert.Null(HicAuthRequestGuard.NormalizeTrustedOrigin("http://dashboard.example.test", isDevelopment: false));
        Assert.Null(HicAuthRequestGuard.NormalizeTrustedOrigin("https://dashboard.example.test/path", isDevelopment: false));
        Assert.Null(HicAuthRequestGuard.NormalizeTrustedOrigin("http://dashboard.example.test", isDevelopment: true));

        var request = new DefaultHttpContext().Request;
        request.Headers.Origin = "https://dashboard.example.test";
        Assert.True(HicAuthRequestGuard.HasTrustedOrigin(request, trusted));
        request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.False(HicAuthRequestGuard.HasTrustedOrigin(request, trusted));
        request.Headers["Sec-Fetch-Site"] = "same-origin";
        request.Headers.Origin = "https://dashboard.example.test/other";
        Assert.False(HicAuthRequestGuard.HasTrustedOrigin(request, trusted));
    }

    [Fact]
    public async Task Antiforgery_RejectsMissingAndInvalidTokensAndAcceptsMatchingCookieHeaderPair()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddDataProtection();
        serviceCollection.AddAntiforgery(options =>
        {
            options.HeaderName = HicUserSession.CsrfHeaderName;
            options.Cookie.Name = HicUserSession.AntiforgeryCookieName;
        });
        using var services = serviceCollection.BuildServiceProvider();
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var issuingContext = NewContext(services);
        var tokens = antiforgery.GetAndStoreTokens(issuingContext);
        var cookie = issuingContext.Response.Headers.SetCookie.ToString().Split(';', 2)[0];

        var missing = NewContext(services);
        missing.Request.Headers.Cookie = cookie;
        Assert.False(await HicAuthRequestGuard.HasValidCsrfAsync(missing, antiforgery));

        var invalid = NewContext(services);
        invalid.Request.Headers.Cookie = cookie;
        invalid.Request.Headers[HicUserSession.CsrfHeaderName] = "invalid";
        Assert.False(await HicAuthRequestGuard.HasValidCsrfAsync(invalid, antiforgery));

        var valid = NewContext(services);
        valid.Request.Headers.Cookie = cookie;
        valid.Request.Headers[HicUserSession.CsrfHeaderName] = tokens.RequestToken;
        Assert.True(await HicAuthRequestGuard.HasValidCsrfAsync(valid, antiforgery));
    }

    [Fact]
    public async Task LoginRateLimit_AllowsFiveRequestsPerAddressAndPartitionsByAddress()
    {
        using var limiter = HicLoginRateLimit.CreateLimiter();
        var firstAddress = new DefaultHttpContext();
        firstAddress.Connection.RemoteIpAddress = IPAddress.Loopback;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var lease = await limiter.AcquireAsync(firstAddress);
            Assert.True(lease.IsAcquired);
        }

        using var rejected = await limiter.AcquireAsync(firstAddress);
        Assert.False(rejected.IsAcquired);

        var secondAddress = new DefaultHttpContext();
        secondAddress.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        using var separatePartition = await limiter.AcquireAsync(secondAddress);
        Assert.True(separatePartition.IsAcquired);
    }

    [Fact]
    public void SessionPrincipal_RequiresBothServerIssuedSessionAndUserIdentifiers()
    {
        var empty = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False(HicUserSessionStore.TryGetSession(empty, out _, out _));

        var incomplete = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], HicUserSession.Scheme));
        Assert.False(HicUserSessionStore.TryGetSession(incomplete, out _, out _));

        var sessionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(HicUserSession.SessionClaim, sessionId.ToString())
        ], HicUserSession.Scheme));
        Assert.True(HicUserSessionStore.TryGetSession(principal, out var actualSession, out var actualUser));
        Assert.Equal(sessionId, actualSession);
        Assert.Equal(userId, actualUser);
    }

    private static DefaultHttpContext NewContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("dashboard.example.test");
        return context;
    }
}
