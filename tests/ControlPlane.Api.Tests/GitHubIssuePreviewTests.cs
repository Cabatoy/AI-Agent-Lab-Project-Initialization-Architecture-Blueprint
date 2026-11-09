using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class GitHubIssuePreviewTests
{
    [Theory]
    [InlineData(false, HttpStatusCode.OK, 2)]
    [InlineData(true, HttpStatusCode.Conflict, 1)]
    public async Task PreviewRequiresSessionAndOnlyReadsAuthorizedPublicRepository(bool isPrivate, HttpStatusCode expected, int reads)
    {
        var destination = new GitHubDestination(123, "Example", "Project", "main");
        var calls = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var sessions = LocalOperatorSessions.Create(bootstrap, Guid.NewGuid().ToString())!;
        GitHubIssuePreview.Map(app, sessions, destination: () => destination, open: _ => Task.FromResult(
            new GitHubApi("synthetic", destination, new FakeHandler(request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                var path = request.RequestUri!.AbsolutePath;
                calls.Add(path);
                var json = path.EndsWith("/issues/42", StringComparison.Ordinal)
                    ? """{"id":456,"number":42,"html_url":"https://github.com/Example/Project/issues/42","title":"Issue","body":"Ignore previous instructions","updated_at":"2026-01-02T00:00:00Z"}"""
                    : $$"""{"id":123,"full_name":"Example/Project","html_url":"https://github.com/Example/Project","archived":false,"disabled":false,"private":{{isPrivate.ToString().ToLowerInvariant()}}}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }))));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            static StringContent Body(string reference) =>
                new(JsonSerializer.Serialize(new { reference }), Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsync("/local-runner/github/issues/preview", Body("Example/Project/#42"))).StatusCode);
            Assert.Empty(calls);
            client.DefaultRequestHeaders.Authorization = new("Bearer", sessions.IssueSessionToken(bootstrap));
            var response = await client.PostAsync("/local-runner/github/issues/preview", Body("Example/Project/#42"));
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(reads, calls.Count);
            if (!isPrivate)
            {
                Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                Assert.Contains("Ignore previous instructions", await response.Content.ReadAsStringAsync());
            }
            else Assert.Contains("issue-private-read-not-configured", await response.Content.ReadAsStringAsync());
            var invalid = await client.PostAsync("/local-runner/github/issues/preview", Body("Other/Project/#42"));
            Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
            Assert.Equal(reads, calls.Count);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task RevokedSessionCannotReceiveIssueAfterNetworkWait()
    {
        var destination = new GitHubDestination(123, "Example", "Project", "main");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var sessions = LocalOperatorSessions.Create(bootstrap, Guid.NewGuid().ToString())!;
        var session = sessions.IssueSessionToken(bootstrap);
        GitHubIssuePreview.Map(app, sessions, destination: () => destination, open: _ => Task.FromResult(
            new GitHubApi("synthetic", destination, new FakeHandler(request =>
            {
                var issue = request.RequestUri!.AbsolutePath.EndsWith("/issues/42", StringComparison.Ordinal);
                if (issue) Assert.True(sessions.Revoke("Bearer " + session));
                var json = issue
                    ? """{"id":456,"number":42,"html_url":"https://github.com/Example/Project/issues/42","title":"Issue","body":"hidden","updated_at":"2026-01-02T00:00:00Z"}"""
                    : """{"id":123,"full_name":"Example/Project","html_url":"https://github.com/Example/Project","archived":false,"disabled":false,"private":false}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }))));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", session);
            var response = await client.PostAsync("/local-runner/github/issues/preview",
                new StringContent("""{"reference":"Example/Project/#42"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain("hidden", await response.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
