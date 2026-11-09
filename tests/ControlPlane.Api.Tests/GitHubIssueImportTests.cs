using System.Security.Cryptography;
using ControlPlane.Api.Tests;
using Dapper;
using Npgsql;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class GitHubIssueImportTests
{
    [PostgresFact]
    public async Task ConfirmIsAtomicDeduplicatedAndActorScoped()
    {
        await GitEditRunnerTests.Database(async (cs, db, _) =>
        {
            await Setup(db);
            var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var actor = Guid.NewGuid();
            var sessions = LocalOperatorSessions.Create(bootstrap, actor.ToString())!;
            var header = "Bearer " + sessions.IssueSessionToken(bootstrap);
            var source = Source("original");
            var store = new GitHubIssueImportStore(cs);
            var p1 = await store.Prepare(actor, source, "Draft", "Reviewed description", default);
            var p2 = await store.Prepare(actor, source, "Draft", "Reviewed description", default);
            var existingTask = await db.ExecuteScalarAsync<Guid>("select id from tasks limit 1");
            var mismatch = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(@"
                insert into hic_issue_imports(repository_id,issue_number,revision_sha256,issue_id,actor_id,preview_id,task_id)
                values(@RepositoryId,@IssueNumber,@RevisionSha256,@IssueId,@wrongActor,@PreviewId,@existingTask)",
                new { p1.RepositoryId, p1.IssueNumber, p1.RevisionSha256, p1.IssueId, wrongActor = Guid.NewGuid(),
                    p1.PreviewId, existingTask }));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, mismatch.SqlState);
            var outcomes = await Task.WhenAll(store.Confirm(p1, source, sessions, header, default),
                store.Confirm(p2, source, sessions, header, default));
            Assert.Equal(outcomes[0]!.TaskId, outcomes[1]!.TaskId);
            Assert.Single(outcomes, x => !x!.Replayed);
            Assert.Single(outcomes, x => x!.Replayed);
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("select count(*)::int from hic_issue_imports"));
            Assert.Equal("Draft", await db.ExecuteScalarAsync<string>("select status from tasks where id=@id",
                new { id = outcomes[0]!.TaskId }));
            Assert.Null(await store.Read(p1.PreviewId, Guid.NewGuid(), default));
            await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("update hic_issue_imports set actor_id=gen_random_uuid()"));
            Assert.Equal("issue-revision-changed", (await Assert.ThrowsAsync<GitHubPublishException>(() =>
                store.Confirm(p1, Source("changed"), sessions, header, default))).Code);
            var newer = await store.Prepare(actor, Source("changed"), "Next", "Review", default);
            Assert.NotEqual(outcomes[0]!.TaskId, (await store.Confirm(newer, Source("changed"), sessions, header, default))!.TaskId);
            var revoked = await store.Prepare(actor, Source("other"), "Next", "Review", default);
            Assert.True(sessions.Revoke(header));
            Assert.Null(await store.Confirm(revoked, Source("other"), sessions, header, default));
            Assert.Equal(2, await db.ExecuteScalarAsync<int>("select count(*)::int from hic_issue_imports"));
        });
    }

    [PostgresFact]
    public async Task AuditFailureRollsBackTaskAndExpiredPreviewIsRejected()
    {
        await GitEditRunnerTests.Database(async (cs, db, _) =>
        {
            await Setup(db);
            var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var actor = Guid.NewGuid();
            var sessions = LocalOperatorSessions.Create(bootstrap, actor.ToString())!;
            var header = "Bearer " + sessions.IssueSessionToken(bootstrap);
            var source = Source("a");
            var store = new GitHubIssueImportStore(cs);
            var preview = await store.Prepare(actor, source, "Draft", "Reviewed", default);
            await db.ExecuteAsync("alter table hic_issue_imports add constraint reject_import check (false)");
            await Assert.ThrowsAsync<PostgresException>(() => store.Confirm(preview, source, sessions, header, default));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("select count(*)::int from tasks"));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("select count(*)::int from hic_issue_imports"));
            await db.ExecuteAsync("alter table hic_issue_imports drop constraint reject_import");
            Assert.Equal("issue-preview-expired", (await Assert.ThrowsAsync<GitHubPublishException>(() =>
                store.Confirm(preview with { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1) }, source, sessions, header, default))).Code);
        });
    }

    [PostgresFact]
    public async Task HttpReviewConfirmationRejectsChangedSourceAndReplaysAfterRestart()
    {
        await GitEditRunnerTests.Database(async (cs, db, _) =>
        {
            await Setup(db);
            var destination = new GitHubDestination(123, "Example", "Project", "main");
            var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var sessions = LocalOperatorSessions.Create(bootstrap, Guid.NewGuid().ToString())!;
            var authorization = sessions.IssueSessionToken(bootstrap);
            var currentBody = "untrusted";
            async Task<(WebApplication App, HttpClient Client)> Start()
            {
                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                var app = builder.Build();
                GitHubIssuePreview.Map(app, sessions, cs, () => destination, _ => Task.FromResult(
                    new GitHubApi("synthetic", destination, new FakeHandler(request =>
                    {
                        Assert.Equal(HttpMethod.Get, request.Method);
                        var value = request.RequestUri!.AbsolutePath.EndsWith("/issues/42", StringComparison.Ordinal)
                            ? JsonSerializer.Serialize(new { id = 456, number = 42, html_url = "https://github.com/Example/Project/issues/42",
                                title = "Issue", body = currentBody, updated_at = "2026-01-01T00:00:00Z" })
                            : """{"id":123,"full_name":"Example/Project","html_url":"https://github.com/Example/Project","archived":false,"disabled":false,"private":false}""";
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value) };
                    }))));
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                var client = new HttpClient { BaseAddress = new Uri(address) };
                client.DefaultRequestHeaders.Authorization = new("Bearer", authorization);
                return (app, client);
            }
            static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
            var (app, client) = await Start();
            try
            {
                var source = await client.PostAsync("/local-runner/github/issues/preview", Json(new { reference = "Example/Project/#42" }));
                Assert.Equal(HttpStatusCode.OK, source.StatusCode);
                using var sourceJson = JsonDocument.Parse(await source.Content.ReadAsStringAsync());
                var digest = sourceJson.RootElement.GetProperty("revisionSha256").GetString();
                var prepared = await client.PostAsync("/local-runner/github/issues/import-preview",
                    Json(new { reference = "Example/Project/#42", revisionSha256 = digest, title = "Review",
                        description = "Human-selected scope" }));
                Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
                using var previewJson = JsonDocument.Parse(await prepared.Content.ReadAsStringAsync());
                var id = previewJson.RootElement.GetProperty("previewId").GetGuid();
                Assert.Equal(0, await db.ExecuteScalarAsync<int>("select count(*)::int from hic_issue_imports"));
                currentBody = "changed";
                var route = $"/local-runner/github/issues/import-previews/{id}/confirm";
                Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(route, null)).StatusCode);
                Assert.Equal(0, await db.ExecuteScalarAsync<int>("select count(*)::int from hic_issue_imports"));
                currentBody = "untrusted";
                var confirmed = await client.PostAsync(route, null);
                Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
                using var result = JsonDocument.Parse(await confirmed.Content.ReadAsStringAsync());
                var task = result.RootElement.GetProperty("taskId").GetGuid();
                Assert.False(result.RootElement.GetProperty("replayed").GetBoolean());
                client.Dispose();
                await app.StopAsync();
                await app.DisposeAsync();
                (app, client) = await Start();
                var replay = await client.PostAsync(route, null);
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
                Assert.Equal(task, replayJson.RootElement.GetProperty("taskId").GetGuid());
                Assert.True(replayJson.RootElement.GetProperty("replayed").GetBoolean());
                Assert.Equal("Draft", await db.ExecuteScalarAsync<string>("select status from tasks where id=@task", new { task }));
                Assert.Equal(0, await db.ExecuteScalarAsync<int>("select count(*)::int from native_health_runs"));
            }
            finally { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
        });
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static GitHubIssueSource Source(string body)
    {
        var repo = new GitHubDestination(123, "Example", "Project", "main");
        return GitHubIssueIntake.Capture(repo, new GitHubIssueReference("Example", "Project", 42),
            123, 456, "Issue", body, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static async Task Setup(NpgsqlConnection db)
    {
        await db.ExecuteAsync(@"alter table tasks add column attempt_count integer not null default 0,
            add column result text, add column error_message text, add column created_at_utc timestamptz not null default now();
            insert into app_schema_versions values (10)");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HumanInControl.sln"))) root = root.Parent;
        Assert.NotNull(root);
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "ControlPlane",
            "Migrations", "011-github-issue-import.sql")));
    }
}
