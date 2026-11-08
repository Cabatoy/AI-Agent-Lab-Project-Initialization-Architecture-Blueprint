using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlPlane.Api.Tests;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class GitHubPublisherTests
{
    private static readonly GitHubDestination Destination = new(123, "Cabatoy", "hic-agent-sandbox", "SaasMain");
    private const string BaseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BaseTree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string TreeSha = "cccccccccccccccccccccccccccccccccccccccc";
    private const string CommitSha = "dddddddddddddddddddddddddddddddddddddddd";
    private const string Content = "namespace Fixture;\npublic static class Value { public static int Number => 52; }\n";

    private static GitHubPublication Row(string kind = "task")
    {
        var task = Guid.NewGuid();
        var plan = new GitHubPublicationPlan(Guid.NewGuid(), Guid.NewGuid(), kind == "task" ? task : null,
            kind == "task" ? Guid.NewGuid() : null, kind == "task" ? Guid.NewGuid() : null, kind, Destination,
            kind == "task" ? BaseSha : null, kind == "task" ? BaseTree : null,
            kind == "task" ? "hic/task-" + task.ToString("N") : Destination.BaseBranch,
            "src/Value.cs", kind == "task" ? Content : GitHubPublisher.BootstrapContent,
            GitHubPublisher.ContentDigest(kind == "task" ? Content : GitHubPublisher.BootstrapContent),
            kind == "task" ? new string('e', 64) : null, new string('f', 64), DateTime.UtcNow.AddMinutes(5));
        return new(plan.PublicationId, plan.ActorId, plan.RunId, plan.ConversationId, Destination.Id,
            kind, JsonSerializer.Serialize(plan, GitEditExecutor.JsonOptions), plan.Digest(), "pending",
            null, null, null, null, null, null, null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
    }

    [Fact]
    public async Task PublisherEndpoints_RejectUnauthenticatedReadsAndWritesWithoutAccessingDatabase()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var sessions = LocalOperatorSessions.Create(bootstrap, Guid.NewGuid().ToString())!;
        GitHubPublisherEndpoints.Map(app, "Host=invalid;Database=must-not-connect", sessions);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var id = Guid.NewGuid();
            foreach (var route in new[]
                     { "bootstrap-preview", $"runs/{id}/preview", $"publications/{id}/approve",
                         $"publications/{id}/execute", $"publications/{id}/reconcile", $"publications/{id}/complete-draft" })
                Assert.Equal(HttpStatusCode.Unauthorized,
                    (await client.PostAsync("/local-runner/github/" + route, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/local-runner/github/status")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", sessions.IssueSessionToken(bootstrap));
            var response = await client.GetAsync("/local-runner/github/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(bootstrap, await response.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public void ApprovalDigest_BindsEveryWriteScopeAndRejectsUnconfiguredApp()
    {
        var plan = Row().Plan;
        foreach (var changed in new[]
        {
            plan with { ActorId = Guid.NewGuid() }, plan with { TaskId = Guid.NewGuid() },
            plan with { RunId = Guid.NewGuid() }, plan with { ConversationId = Guid.NewGuid() },
            plan with { Destination = Destination with { Id = 456 } },
            plan with { Destination = Destination with { Owner = "other" } },
            plan with { Destination = Destination with { BaseBranch = "other" } },
            plan with { BaseCommit = CommitSha }, plan with { BaseTree = TreeSha },
            plan with { TaskBranch = "main" }, plan with { File = "other.cs" },
            plan with { Content = "changed\n" }, plan with { ContentDigest = new string('f', 64) },
            plan with { CodePlanDigest = new string('f', 64) }, plan with { ExpiresAtUtc = DateTime.UtcNow },
            plan with { CredentialScopeDigest = new string('a', 64) },
            plan with { Kind = "bootstrap" }
        }) Assert.NotEqual(plan.Digest(), changed.Digest());
        var broker = new GitHubAppBroker(new ConfigurationBuilder().Build());
        Assert.False(broker.Status.Configured);
        Assert.Contains("HIC_GITHUB_PRIVATE_KEY_PATH", broker.Status.Missing);
        Assert.Throws<GitHubPublishException>(() => broker.Destination());
    }

    [Theory]
    [InlineData("main..other")]
    [InlineData("main.lock")]
    [InlineData("main//other")]
    [InlineData("main:other")]
    [InlineData("main\\other")]
    [InlineData("https://github.com/other")]
    public void Branches_RejectUnsafeRefs(string branch) => Assert.False(GitHubAppBroker.ValidBranch(branch));

    [Theory]
    [InlineData("""{"contents":"write","pull_requests":"write","metadata":"read","administration":"write"}""")]
    [InlineData("""{"contents":"read","pull_requests":"write","metadata":"read"}""")]
    [InlineData("""{"contents":"write","metadata":"read"}""")]
    public void AppPermissions_RejectExpandedOrInsufficientPermissions(string json)
    {
        using var value = JsonDocument.Parse(json);
        Assert.Throws<GitHubPublishException>(() => GitHubAppBroker.CheckPermissions(value.RootElement));
    }

    [Fact]
    public async Task Broker_SignsAppJwtAndRequestsOnlyThePinnedRepositoryAndMinimalPermissions()
    {
        var key = Path.Combine(Path.GetTempPath(), "hic52-key-" + Guid.NewGuid().ToString("N") + ".pem");
        using var rsa = RSA.Create(2048);
        await File.WriteAllTextAsync(key, rsa.ExportRSAPrivateKeyPem());
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["HIC_GITHUB_APP_ID"] = "12", ["HIC_GITHUB_INSTALLATION_ID"] = "34",
                ["HIC_GITHUB_PRIVATE_KEY_PATH"] = key, ["HIC_GITHUB_REPOSITORY_ID"] = "123",
                ["HIC_GITHUB_REPOSITORY"] = Destination.FullName, ["HIC_GITHUB_BASE_BRANCH"] = "SaasMain"
            };
            var appRequests = 0;
            var fake = new CallbackHandler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var permissions = new { contents = "write", pull_requests = "write", metadata = "read" };
                if (path.StartsWith("/app", StringComparison.Ordinal))
                {
                    appRequests++;
                    var jwt = request.Headers.Authorization!.Parameter!.Split('.');
                    Assert.Equal(3, jwt.Length);
                    Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]),
                        Decode(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                    if (path == "/app") return Json(new { id = 12, permissions });
                    if (path == "/app/installations/34") return Json(new
                    { app_id = 12, repository_selection = "selected", suspended_at = (string?)null, permissions });
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    Assert.Equal(123, body.RootElement.GetProperty("repository_ids")[0].GetInt32());
                    GitHubAppBroker.CheckPermissions(body.RootElement.GetProperty("permissions"));
                    return Json(new { token = "synthetic-installation-token", permissions,
                        expires_at = DateTimeOffset.UtcNow.AddMinutes(60).ToString("O") });
                }
                Assert.Equal("synthetic-installation-token", request.Headers.Authorization!.Parameter);
                if (path == "/installation/repositories")
                    return Json(new { total_count = 1, repositories = new[] { Repository() } });
                return Json(Repository());
            });
            var broker = new GitHubAppBroker(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                (token, destination) => new GitHubApi(token, destination, fake));
            using var api = await broker.OpenAsync(CancellationToken.None);
            Assert.Equal(3, appRequests);
            Assert.DoesNotContain("synthetic-installation-token", JsonSerializer.Serialize(broker.Status));
            Assert.DoesNotContain("PRIVATE KEY", JsonSerializer.Serialize(broker.Status));
        }
        finally { File.Delete(key); }
        static byte[] Decode(string value) => Convert.FromBase64String(
            value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    }

    [Fact]
    public async Task ApprovedTask_CreatesOneCommitOneRefAndOneDraft_WithIntentBeforeEveryWrite()
    {
        var row = Row();
        using var fake = new FakeGitHub(row);
        using var api = fake.Api();
        var result = await GitHubPublisher.ExecuteAsync(api, row, fake.Save, CancellationToken.None);
        Assert.Equal("succeeded", result.State);
        Assert.Equal(CommitSha, result.CommitSha);
        Assert.Equal(CommitSha, result.RemoteSha);
        Assert.Equal(Destination.Url + "/pull/52", result.PrUrl);
        Assert.Equal(1, fake.RefWrites);
        Assert.Equal(1, fake.CommitWrites);
        Assert.Equal(1, fake.PrWrites);
        Assert.Equal(BaseSha, fake.BaseRef);
        var reload = JsonSerializer.Deserialize<GitHubPublication>(
            JsonSerializer.Serialize(result, GitEditExecutor.JsonOptions), GitEditExecutor.JsonOptions)!;
        var reconciled = await GitHubPublisher.ReconcileAsync(api, reload, fake.Save, CancellationToken.None);
        Assert.Equal("succeeded", reconciled.State);
        Assert.Equal(1, fake.PrWrites);
        await Assert.ThrowsAsync<GitHubPublishException>(() =>
            GitHubPublisher.ExecuteAsync(api, result, fake.Save, CancellationToken.None));
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("pr")]
    public async Task TimeoutAfterExternalWrite_ReconcilesWithoutDuplicateCommitRefOrPull(string stage)
    {
        var row = Row();
        using var fake = new FakeGitHub(row) { TimeoutAfter = stage };
        using var api = fake.Api();
        var unknown = await GitHubPublisher.ExecuteAsync(api, row, fake.Save, CancellationToken.None);
        Assert.Equal("unknown", unknown.State);
        var reconciled = await GitHubPublisher.ReconcileAsync(api, unknown, fake.Save, CancellationToken.None);
        if (stage == "ref")
        {
            Assert.Equal("pushed", reconciled.State);
            fake.TimeoutAfter = "";
            reconciled = await GitHubPublisher.CompleteDraftAsync(api, reconciled, fake.Save, CancellationToken.None);
        }
        Assert.Equal("succeeded", reconciled.State);
        Assert.Equal(1, fake.RefWrites);
        Assert.Equal(1, fake.CommitWrites);
        Assert.Equal(1, fake.PrWrites);
    }

    [Fact]
    public async Task DuplicateDrafts_AreAnExplicitConflictAndNeverCreateAnotherPull()
    {
        var row = Row();
        using var fake = new FakeGitHub(row) { Failure = "duplicate-pr" };
        using var api = fake.Api();
        var result = await GitHubPublisher.ExecuteAsync(api, row, fake.Save, CancellationToken.None);
        Assert.Equal("unknown", result.State);
        Assert.Equal("github-duplicate-pr-conflict", result.ErrorCode);
        Assert.Equal(0, fake.PrWrites);
    }

    [Fact]
    public async Task BaseMovementAfterCommit_PreventsPublishingTheTaskRef()
    {
        var row = Row();
        using var fake = new FakeGitHub(row) { Failure = "base-after-commit" };
        using var api = fake.Api();
        var result = await GitHubPublisher.ExecuteAsync(api, row, fake.Save, CancellationToken.None);
        Assert.Equal("unknown", result.State);
        Assert.Equal("github-base-changed", result.ErrorCode);
        Assert.Equal(1, fake.CommitWrites);
        Assert.Equal(0, fake.RefWrites);
        Assert.Equal(0, fake.PrWrites);
    }

    [Fact]
    public async Task TimeoutDuringCommit_StaysUnknownWithoutRetryingAnyWrite()
    {
        var row = Row();
        using var fake = new FakeGitHub(row) { TimeoutAfter = "commit" };
        using var api = fake.Api();
        var unknown = await GitHubPublisher.ExecuteAsync(api, row, fake.Save, CancellationToken.None);
        Assert.Null(unknown.CommitSha);
        Assert.Equal("unknown", unknown.State);
        var reconciled = await GitHubPublisher.ReconcileAsync(api, unknown, fake.Save, CancellationToken.None);
        Assert.Equal("unknown", reconciled.State);
        Assert.Equal(0, fake.RefWrites);
        Assert.Equal(1, fake.CommitWrites);
        Assert.Equal(0, fake.PrWrites);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("base")]
    [InlineData("revoked")]
    [InlineData("traversal")]
    public async Task InvalidScopeOrRevokedAccess_FailsBeforeAnyWrite(string failure)
    {
        var row = Row();
        if (failure == "traversal")
        {
            var changed = row.Plan with { File = "../Value.cs" };
            row = row with { PlanJson = JsonSerializer.Serialize(changed, GitEditExecutor.JsonOptions), Digest = changed.Digest() };
        }
        using var fake = new FakeGitHub(row) { Failure = failure };
        using var api = fake.Api();
        var result = await GitHubPublisher.ExecuteAsync(api, row, fake.Save, CancellationToken.None);
        Assert.Equal("blocked", result.State);
        Assert.NotNull(result.ErrorCode);
        Assert.Empty(fake.Writes);
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("extra-file")]
    public async Task ReturnedTreeMustMatchOnlyApprovedRegularFile(string failure)
    {
        var row = Row();
        using var fake = new FakeGitHub(row) { Failure = failure };
        using var api = fake.Api();
        var result = await GitHubPublisher.ExecuteAsync(api, row, fake.Save, CancellationToken.None);
        Assert.Equal("unknown", result.State);
        Assert.Equal(0, fake.CommitWrites);
        Assert.Equal(0, fake.RefWrites);
    }

    [Fact]
    public async Task EmptyRepository_RequiresSeparateBootstrapAndReconcilesAmbiguousInitialization()
    {
        var row = Row("bootstrap");
        using var fake = new FakeGitHub(row) { Empty = true, TimeoutAfter = "bootstrap" };
        using var api = fake.Api();
        var result = await GitHubPublisher.ExecuteAsync(api, row, fake.Save, CancellationToken.None);
        Assert.Equal("unknown", result.State);
        result = await GitHubPublisher.ReconcileAsync(api, result, fake.Save, CancellationToken.None);
        Assert.Equal("succeeded", result.State);
        Assert.Null(result.PrUrl);
        Assert.Equal(CommitSha, result.RemoteSha);
        Assert.Single(fake.Writes);
        Assert.Equal("PUT /contents/src/Value.cs", fake.Writes[0]);
        await Assert.ThrowsAsync<GitHubPublishException>(() =>
            GitHubPublisher.ExecuteAsync(api, row with { ApprovedAtUtc = null }, fake.Save, CancellationToken.None));
    }

    [Fact]
    public async Task NonEmptyRepository_RejectsBootstrapEvenIfMetadataListsDefaultBranch()
    {
        var row = Row("bootstrap");
        using var fake = new FakeGitHub(row);
        using var api = fake.Api();
        var result = await GitHubPublisher.ExecuteAsync(api, row, fake.Save, CancellationToken.None);
        Assert.Equal("blocked", result.State);
        Assert.Empty(fake.Writes);
    }

    [PostgresFact]
    public async Task PublicationStore_DurableApprovalSingleClaimActorIsolationAndImmutableAudit()
    {
        await GitEditRunnerTests.Database(async (cs, db, _) =>
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "HumanInControl.sln"))) root = root.Parent;
            await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(root!.FullName,
                "src", "ControlPlane", "Migrations", "007-github-publisher.sql")));
            var store = new GitHubPublicationStore(cs);
            var row = Row("bootstrap");
            await store.CreateAsync(row.Plan, CancellationToken.None);
            Assert.Null(await store.ReadAsync(row.PublicationId, Guid.NewGuid(), CancellationToken.None));
            Assert.False(await store.ApproveAsync(row.PublicationId, row.ActorId, new string('f', 64), CancellationToken.None));
            Assert.True(await store.ApproveAsync(row.PublicationId, row.ActorId, row.Digest, CancellationToken.None));
            Assert.False(await store.ApproveAsync(row.PublicationId, row.ActorId, row.Digest, CancellationToken.None));
            var competing = row.Plan with { PublicationId = Guid.NewGuid() };
            await store.CreateAsync(competing, CancellationToken.None);
            Assert.False(await store.ApproveAsync(competing.PublicationId, row.ActorId, competing.Digest(), CancellationToken.None));
            var saved = await store.ReadAsync(row.PublicationId, row.ActorId, CancellationToken.None);
            Assert.Equal("pending", saved!.State);
            await using var first = await store.LockAsync(row.PublicationId, CancellationToken.None);
            Assert.NotNull(first);
            Assert.Null(await store.LockAsync(row.PublicationId, CancellationToken.None));
            await GitHubPublicationStore.UnlockAsync(first!, row.PublicationId);
            await store.SaveAsync(saved with { State = "unknown", ErrorCode = "github-response-unavailable" },
                "unknown", CancellationToken.None);
            Assert.Equal("unknown", (await new GitHubPublicationStore(cs).ReadAsync(
                row.PublicationId, row.ActorId, CancellationToken.None))!.State);
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                db.ExecuteAsync("delete from hic_github_publication_events where publication_id=@id", new { id = row.PublicationId }));
        });
    }

    private static object Repository(long id = 123) => new
    {
        id, full_name = Destination.FullName, html_url = Destination.Url, archived = false, disabled = false,
        default_branch = Destination.BaseBranch
    };
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private sealed class CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => callback(request);
    }

    private sealed class FakeGitHub(GitHubPublication row) : HttpMessageHandler
    {
        internal string TimeoutAfter = "";
        internal string Failure = "";
        internal bool Empty;
        internal string BaseRef = BaseSha;
        private string? taskRef;
        private bool pullExists;
        private string lastIntent = "pending";
        internal int RefWrites;
        internal int CommitWrites;
        internal int PrWrites;
        internal List<string> Writes { get; } = [];
        internal GitHubApi Api() => new("synthetic-test-token", Destination, this);
        internal Task Save(GitHubPublication value, string code)
        { lastIntent = value.State; return Task.CompletedTask; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Null(request.RequestUri.UserInfo.Length == 0 ? null : request.RequestUri.UserInfo);
            var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath).Replace("/repos/" + Destination.FullName, "");
            if (Failure == "revoked") return Error(HttpStatusCode.Forbidden);
            if (request.Method != HttpMethod.Get)
            {
                Assert.EndsWith("-intent", lastIntent);
                Writes.Add(request.Method + " " + path);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (path == "/git/blobs") return Json(new { sha = GitHubPublisher.BlobSha(row.Plan.Content) });
                if (path == "/git/trees")
                {
                    Assert.Equal(row.Plan.File, body.RootElement.GetProperty("tree")[0].GetProperty("path").GetString());
                    return Json(new { sha = TreeSha });
                }
                if (path == "/git/commits")
                {
                    CommitWrites++;
                    if (Failure == "base-after-commit") BaseRef = CommitSha;
                    if (TimeoutAfter == "commit") throw new HttpRequestException("synthetic timeout");
                    return Json(Commit());
                }
                if (path == "/git/refs")
                {
                    RefWrites++;
                    Assert.Equal("refs/heads/" + row.Plan.TaskBranch, body.RootElement.GetProperty("ref").GetString());
                    taskRef = CommitSha;
                    if (TimeoutAfter == "ref") throw new HttpRequestException("synthetic timeout");
                    return Json(new { @ref = "refs/heads/" + row.Plan.TaskBranch });
                }
                if (path == "/pulls")
                {
                    PrWrites++;
                    Assert.True(body.RootElement.GetProperty("draft").GetBoolean());
                    pullExists = true;
                    if (TimeoutAfter == "pr") throw new HttpRequestException("synthetic timeout");
                    return Json(Pull());
                }
                if (path == "/contents/src/Value.cs")
                {
                    Empty = false; BaseRef = CommitSha;
                    if (TimeoutAfter == "bootstrap") throw new HttpRequestException("synthetic timeout");
                    return Json(new { commit = new { sha = CommitSha },
                        content = new { sha = GitHubPublisher.BlobSha(row.Plan.Content) } });
                }
                throw new InvalidOperationException("Unexpected fake write route.");
            }
            if (path == "") return Json(Repository(Failure == "repository" ? 999 : 123));
            if (path == "/branches") return Json(Empty ? Array.Empty<object>() : new object[] { new { name = Destination.BaseBranch } });
            if (path == "/commits") return Empty ? Error(HttpStatusCode.Conflict, "Git Repository is empty.") :
                Json(new[] { new { sha = BaseRef } });
            if (path.StartsWith("/git/ref/heads/"))
            {
                var branch = path["/git/ref/heads/".Length..];
                var sha = branch == Destination.BaseBranch ? (Empty ? null : Failure == "base" ? CommitSha : BaseRef) : taskRef;
                return sha is null ? Error(HttpStatusCode.NotFound) : Json(new
                { @ref = "refs/heads/" + branch, @object = new { type = "commit", sha } });
            }
            if (path == "/git/commits/" + BaseSha)
                return Json(new { sha = BaseSha, tree = new { sha = BaseTree } });
            if (path == "/git/commits/" + CommitSha) return Json(Commit());
            if (path.StartsWith("/git/trees/"))
            {
                var changed = path.EndsWith(TreeSha);
                var entries = new List<object>
                {
                    new { path = row.Plan.File, mode = Failure == "symlink" && changed ? "120000" : "100644",
                        type = "blob", sha = changed ? GitHubPublisher.BlobSha(row.Plan.Content) : new string('f', 40) }
                };
                if (row.Kind != "bootstrap") entries.Add(new
                { path = "README.md", mode = "100644", type = "blob", sha = new string('e', 40) });
                if (Failure == "extra-file" && changed) entries.Add(new
                { path = "unauthorized.cs", mode = "100644", type = "blob", sha = new string('e', 40) });
                return Json(new { truncated = false, tree = entries });
            }
            if (path == "/pulls") return Json(Failure == "duplicate-pr" ? new[] { Pull(), Pull() } :
                pullExists ? new[] { Pull() } : Array.Empty<object>());
            throw new InvalidOperationException("Unexpected fake read route.");
        }
        private object Commit() => new { sha = CommitSha, tree = new { sha = TreeSha },
            parents = row.Kind == "bootstrap" ? Array.Empty<object>() : new object[] { new { sha = BaseSha } },
            message = row.Plan.CommitMessage };
        private object Pull() => new
        {
            number = 52, html_url = Destination.Url + "/pull/52", draft = true, state = "open",
            head = new { @ref = row.Plan.TaskBranch, sha = CommitSha, repo = new { id = 123 } },
            @base = new { @ref = Destination.BaseBranch, sha = BaseSha, repo = new { id = 123 } }
        };
        private static HttpResponseMessage Error(HttpStatusCode code, string message = "Synthetic error") =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json") };
    }
}
