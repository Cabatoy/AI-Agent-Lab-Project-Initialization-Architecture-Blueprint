using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ControlPlane.Api.TransactionalMail;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ControlPlane.Api.Tests;

public class PasswordRecoveryHttpTests
{
    [Fact]
    public async Task RecoveryRejectsCrossOriginMissingCsrfAndThrottlesWithoutDatabaseAccess()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAntiforgery(options => options.HeaderName = HicUserSession.CsrfHeaderName);
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("hic-password-recovery", HicLoginRateLimit.ForRequest);
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });
        builder.Services.AddSingleton<ITransactionalMailSender>(
            new CaptureTransactionalMailSink("Test", true, "https://hic.example.test"));
        await using var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        PasswordRecoveryEndpoints.Map(app, "invalid", "https://hic.example.test", "https://hic.example.test",
            new PasswordResetStore("invalid", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("Origin", "https://attacker.example.test");
        using var crossOrigin = await client.PostAsJsonAsync("/auth/password/forgot", new { identifier = "alice" });
        Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://hic.example.test");
        for (var i = 0; i < 4; i++)
        {
            using var noCsrf = await client.PostAsJsonAsync("/auth/password/reset", new { token = "invalid", newPassword = "replacement-password" });
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        }
        using var limited = await client.PostAsJsonAsync("/auth/password/forgot", new { identifier = "alice" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [PostgresFact]
    public async Task ForgotAndResetUseCaptureSinkAndRevokeOnlyOwnersSessions()
    {
        await PasswordResetPostgresTests.WithTables(async (cs, _) =>
        {
            var user = Assert.IsType<UserIdentity>((await UserIdentityStore.CreateAsync(cs, "alice@example.test", "alice")).Identity);
            var other = Assert.IsType<UserIdentity>((await UserIdentityStore.CreateAsync(cs, "bob@example.test", "bob")).Identity);
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, user.Id, "original-password"));
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, other.Id, "other-password"));
            var sessions = new HicUserSessionStore(cs);
            var ownerSession = Guid.NewGuid();
            var otherSession = Guid.NewGuid();
            await sessions.CreateAsync(ownerSession, user.Id, DateTime.UtcNow.AddMinutes(10));
            await sessions.CreateAsync(otherSession, other.Id, DateTime.UtcNow.AddMinutes(10));

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAntiforgery(options => options.HeaderName = HicUserSession.CsrfHeaderName);
            builder.Services.AddRateLimiter(options => options.AddPolicy("hic-password-recovery", HicLoginRateLimit.ForRequest));
            var sink = new CaptureTransactionalMailSink("Test", true, "https://hic.example.test");
            builder.Services.AddSingleton<ITransactionalMailSender>(sink);
            await using var app = builder.Build();
            app.UseRouting();
            app.UseRateLimiter();
            app.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
                Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
            PasswordRecoveryEndpoints.Map(app, cs, "https://hic.example.test", "https://hic.example.test",
                new PasswordResetStore(cs, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Add("Origin", "https://hic.example.test");
            var csrf = await client.GetFromJsonAsync<CsrfResult>("/csrf");
            client.DefaultRequestHeaders.Add(HicUserSession.CsrfHeaderName, csrf!.Token);

            using var unknown = await client.PostAsJsonAsync("/auth/password/forgot", new { identifier = "missing@example.test" });
            using var known = await client.PostAsJsonAsync("/auth/password/forgot", new { identifier = "alice@example.test" });
            Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
            Assert.Equal(await unknown.Content.ReadAsStringAsync(), await known.Content.ReadAsStringAsync());
            var captured = Assert.Single(sink.Captures);
            Assert.Equal("alice@example.test", captured.Intent.Recipient);
            var uri = new Uri(captured.Intent.Placeholders["resetUrl"]);
            Assert.Equal("https://hic.example.test", uri.GetLeftPart(UriPartial.Authority));
            var token = Uri.UnescapeDataString(uri.Query["?reset_token=".Length..]);

            using var result = await client.PostAsJsonAsync("/auth/password/reset", new { token, newPassword = "replacement-password" });
            Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
            using var replay = await client.PostAsJsonAsync("/auth/password/reset", new { token, newPassword = "replacement-password" });
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, user.Id, "original-password"));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, user.Id, "replacement-password"));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, other.Id, "other-password"));
            Assert.False(await sessions.IsActiveAsync(ownerSession, user.Id));
            Assert.True(await sessions.IsActiveAsync(otherSession, other.Id));
        });
    }

    private sealed record CsrfResult(string Token);
}
