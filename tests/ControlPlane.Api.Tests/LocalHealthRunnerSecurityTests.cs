using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlPlane.Api.Tests;

public sealed class LocalHealthRunnerSecurityTests
{
    private sealed class SessionClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [PostgresFact]
    public async Task RepositoryExecution_IsDeniedBeforeClaimAndPreservesApproval()
    {
        await GitEditRunnerTests.Database(async (cs, db, taskId) =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            var actor = Guid.NewGuid();
            var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var sessions = LocalOperatorSessions.Create(bootstrap, actor.ToString())!;
            var token = sessions.IssueSessionToken(bootstrap)!;
            LocalHealthRunnerEndpoints.Map(app, cs, null!, sessions);
            var store = new LocalHealthRunStore(cs);
            var intent = (await store.GetIntentAsync(taskId, CancellationToken.None))!;
            var expiry = DateTime.UtcNow.AddMinutes(4);
            expiry = new DateTime(expiry.Ticks / 10 * 10, DateTimeKind.Utc);
            var plan = new GitEditPlan(1, taskId, actor, intent.IntentDigestSha256, "fixture",
                Path.GetTempPath(), new string('b', 40), "message.txt", "approved\n", expiry);
            var approval = await store.ApproveAsync(taskId, actor, plan.Digest(), CancellationToken.None, plan);
            Assert.Equal("accepted", approval.Code);
            var runId = approval.Run!.RunId;
            var eventsBefore = await db.ExecuteScalarAsync<int>("select count(*)::int from native_health_run_audit");
            await app.StartAsync();
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses.Single();
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                var result = await client.PostAsync($"/local-runner/runs/{runId}/execute", null);
                Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
                Assert.Contains("isolation backend", await result.Content.ReadAsStringAsync());
                Assert.Equal("queued", (await store.GetAsync(runId, actor, CancellationToken.None))!.Run.Status);
                Assert.Equal(eventsBefore, await db.ExecuteScalarAsync<int>("select count(*)::int from native_health_run_audit"));
            }
            finally { await app.StopAsync(); }
        });
    }

    [Fact]
    public void Sessions_HaveDistinctAbsoluteLifetimesAndDeterministicRevocation()
    {
        var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var actor = Guid.NewGuid();
        var clock = new SessionClock();
        var sessions = LocalOperatorSessions.Create(bootstrap, actor.ToString(), clock)!;
        var bearer = sessions.IssueSessionToken(bootstrap)!;
        var browser = sessions.IssueSessionToken(bootstrap, browserSession: true)!;
        var other = sessions.IssueSessionToken(bootstrap, browserSession: true)!;
        Assert.Equal(clock.Now.AddMinutes(15), sessions.Restore("Bearer " + bearer)!.Value.ExpiresAtUtc);
        var absoluteExpiry = clock.Now.AddHours(8);
        Assert.Equal(absoluteExpiry, sessions.Restore("Bearer " + browser)!.Value.ExpiresAtUtc);
        clock.Now = clock.Now.AddMinutes(15);
        Assert.Null(sessions.Authenticate("Bearer " + bearer));
        Assert.Equal(actor, sessions.Authenticate("Bearer " + browser));
        Assert.Equal(absoluteExpiry, sessions.Restore("Bearer " + browser)!.Value.ExpiresAtUtc);
        Assert.True(sessions.Revoke("Bearer " + browser));
        Assert.False(sessions.Revoke("Bearer " + browser));
        Assert.Null(sessions.Authenticate("Bearer " + browser));
        Assert.False(sessions.Revoke("Basic " + other));
        Assert.Equal(actor, sessions.Authenticate("Bearer " + other));
        clock.Now = absoluteExpiry;
        Assert.Null(sessions.Restore("Bearer " + other));
        Assert.False(sessions.Revoke("Bearer " + other));
        var replacement = sessions.IssueSessionToken(bootstrap, browserSession: true)!;
        Assert.Equal(actor, sessions.Authenticate("Bearer " + replacement));
        Assert.Null(LocalOperatorSessions.Create(bootstrap, actor.ToString())!.Authenticate("Bearer " + replacement));
    }

    [PostgresFact]
    public async Task SessionEndpoints_RestoreAndRevokeWithoutChangingExistingApprovals()
    {
        await WithHealthDatabase(async (store, db, id) =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            var actor = Guid.NewGuid();
            var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var clock = new SessionClock();
            var sessions = LocalOperatorSessions.Create(bootstrap, actor.ToString(), clock)!;
            LocalHealthRunnerEndpoints.Map(app, db.ConnectionString, null!, sessions);
            await app.StartAsync();
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses.Single();
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/local-runner/session")).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/local-runner/session/revoke", null)).StatusCode);
                using var legacy = await client.PostAsync("/local-runner/session",
                    new StringContent(JsonSerializer.Serialize(new { bootstrapToken = bootstrap }), System.Text.Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
                var legacySession = await legacy.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(900, legacySession.GetProperty("expiresInSeconds").GetInt32());
                client.DefaultRequestHeaders.Add("X-HIC-Session-Mode", "browser");
                using var login = await client.PostAsync("/local-runner/session",
                    new StringContent(JsonSerializer.Serialize(new { bootstrapToken = bootstrap }), System.Text.Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                Assert.Equal("no-store", login.Headers.CacheControl!.ToString());
                var issued = await login.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(28800, issued.GetProperty("expiresInSeconds").GetInt32());
                var token = issued.GetProperty("accessToken").GetString()!;
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                clock.Now = clock.Now.AddHours(1);
                using var restoredResponse = await client.GetAsync("/local-runner/session");
                var restored = await restoredResponse.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(actor, restored.GetProperty("actorId").GetGuid());
                Assert.Equal(25200, restored.GetProperty("expiresInSeconds").GetInt32());
                Assert.False(restored.TryGetProperty("accessToken", out _));
                var intent = await store.GetIntentAsync(id, CancellationToken.None);
                var approval = await store.ApproveAsync(id, actor, intent!.IntentDigestSha256, CancellationToken.None);
                var auditCount = await db.ExecuteScalarAsync<int>("select count(*)::int from native_health_run_audit");
                Assert.Equal(HttpStatusCode.BadRequest,
                    (await client.PostAsJsonAsync("/local-runner/session/revoke", new { all = true })).StatusCode);
                Assert.Equal(actor, sessions.Authenticate("Bearer " + token));
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/local-runner/session/revoke", null)).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/local-runner/session")).StatusCode);
                var replacement = sessions.IssueSessionToken(bootstrap, browserSession: true)!;
                Assert.Equal(actor, sessions.Authenticate("Bearer " + replacement));
                Assert.NotNull(await store.GetAsync(approval.Run!.RunId, actor, CancellationToken.None));
                Assert.Equal(auditCount, await db.ExecuteScalarAsync<int>("select count(*)::int from native_health_run_audit"));
                client.DefaultRequestHeaders.Authorization = new("Bearer", replacement);
                clock.Now = clock.Now.AddHours(8);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/local-runner/session")).StatusCode);
            }
            finally { await app.StopAsync(); }
        });
    }

    [PostgresFact]
    public async Task DurableApproval_ActorIsolationSingleClaimAndReplayRejection()
    {
        await WithHealthDatabase(async (store, db, id) =>
        {
            var actor = Guid.NewGuid();
            var intent = await store.GetIntentAsync(id, CancellationToken.None);
            Assert.NotNull(intent);
            var approval = await store.ApproveAsync(id, actor, intent.IntentDigestSha256, CancellationToken.None);
            Assert.Equal("accepted", approval.Code);
            var run = approval.Run!;
            Assert.Equal("replay", (await store.ApproveAsync(id, actor, intent.IntentDigestSha256, CancellationToken.None)).Code);
            Assert.Null(await store.GetAsync(run.RunId, Guid.NewGuid(), CancellationToken.None));
            Assert.Equal("actor-mismatch", (await store.ClaimAsync(run.RunId, Guid.NewGuid(), CancellationToken.None)).Code);
            var claims = await Task.WhenAll(
                store.ClaimAsync(run.RunId, actor, CancellationToken.None),
                store.ClaimAsync(run.RunId, actor, CancellationToken.None));
            Assert.Single(claims, claim => claim.Code == "claimed");
            Assert.Single(claims, claim => claim.Code == "not-queued");
            var result = new LocalDiagnosticSnapshot("system.health", DateTime.UtcNow, "ok", "ok", "ok", "not-checked");
            Assert.True(await store.CompleteAsync(run.RunId, actor, result, CancellationToken.None));
            var snapshot = await store.GetAsync(run.RunId, actor, CancellationToken.None);
            Assert.Equal("completed", snapshot!.Run.Status);
            using var persisted = JsonDocument.Parse(snapshot.Run.ResultJson!);
            Assert.Equal("ok", persisted.RootElement.GetProperty("postgres").GetString());
            Assert.Equal("not-queued", (await store.ClaimAsync(run.RunId, actor, CancellationToken.None)).Code);
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("select count(*)::int from native_health_run_audit where event_code='claimed'"));
            Assert.Equal("Draft", await db.ExecuteScalarAsync<string>("select status from tasks where id=@id", new { id }));
        });
    }

    [PostgresFact]
    public async Task ChangedTaskInvalidatesApproval_AndAuditFailureRollsBackClaim()
    {
        await WithHealthDatabase(async (store, db, id) =>
        {
            var actor = Guid.NewGuid();
            var intent = await store.GetIntentAsync(id, CancellationToken.None);
            var approval = await store.ApproveAsync(id, actor, intent!.IntentDigestSha256, CancellationToken.None);
            var run = approval.Run!;
            await db.ExecuteAsync("alter table native_health_run_audit add constraint reject_claim check(event_code <> 'claimed')");
            await Assert.ThrowsAsync<PostgresException>(() => store.ClaimAsync(run.RunId, actor, CancellationToken.None));
            Assert.Equal("queued", (await store.GetAsync(run.RunId, actor, CancellationToken.None))!.Run.Status);
            await db.ExecuteAsync("alter table native_health_run_audit drop constraint reject_claim; update tasks set description='Changed' where id=@id", new { id });
            Assert.Equal("task-changed", (await store.ClaimAsync(run.RunId, actor, CancellationToken.None)).Code);
            Assert.Equal("invalidated", (await store.GetAsync(run.RunId, actor, CancellationToken.None))!.Run.Status);
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("select count(*)::int from native_health_run_audit where event_code='claimed'"));
        });
    }

    private static Task WithHealthDatabase(Func<LocalHealthRunStore, NpgsqlConnection, Guid, Task> test) =>
        TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "HumanInControl.sln"))) root = root.Parent;
            Assert.NotNull(root);
            await using (var setup = await db.BeginTransactionAsync())
            {
                await db.ExecuteAsync("alter table tasks add column title text not null default 'Health', add column description text not null default 'Fixture'; update tasks set status='Draft';", transaction: setup);
                await db.ExecuteAsync(File.ReadAllText(Path.Combine(root.FullName, "src", "ControlPlane",
                    "Migrations", "004-local-health-runner.sql")), transaction: setup);
                await setup.CommitAsync();
            }
            try { await test(new LocalHealthRunStore(cs), db, id); }
            finally
            {
                // The outer fixture guards the dedicated database and unique schema identity.
                await db.ExecuteAsync("drop table native_health_run_audit restrict; drop table native_health_runs restrict; drop function hic_reject_native_health_audit_mutation() restrict;");
            }
        });

    [Fact]
    public void PersistedHealthResult_UsesTheWebUiJsonContract()
    {
        var result = new LocalDiagnosticSnapshot(
            "system.health", DateTime.SpecifyKind(new DateTime(2026, 11, 8, 12, 0, 0), DateTimeKind.Utc),
            "ok", "ok", "unavailable", "not-checked");

        using var document = JsonDocument.Parse(LocalHealthRunStore.SerializeResult(result));
        var root = document.RootElement;
        Assert.Equal("system.health", root.GetProperty("action").GetString());
        Assert.Equal("ok", root.GetProperty("controlPlane").GetString());
        Assert.Equal("ok", root.GetProperty("postgres").GetString());
        Assert.Equal("unavailable", root.GetProperty("redis").GetString());
        Assert.Equal("not-checked", root.GetProperty("worker").GetString());
        Assert.False(root.TryGetProperty("Action", out _));
    }

    [Fact]
    public void IntentDigest_IsStableAndBindsTaskVersionTextActionAndScope()
    {
        var taskId = Guid.NewGuid();
        var version = DateTime.SpecifyKind(new DateTime(2026, 11, 8, 12, 0, 0), DateTimeKind.Utc);
        var digest = LocalHealthRunIntent.Digest(taskId, "Health check", "Inspect local services.", version);

        Assert.Equal(64, digest.Length);
        Assert.Equal(digest, LocalHealthRunIntent.Digest(taskId, "Health check", "Inspect local services.", version));
        Assert.NotEqual(digest, LocalHealthRunIntent.Digest(taskId, "Changed", "Inspect local services.", version));
        Assert.NotEqual(digest, LocalHealthRunIntent.Digest(taskId, "Health check", "Changed.", version));
        Assert.NotEqual(digest, LocalHealthRunIntent.Digest(taskId, "Health check", "Inspect local services.", version.AddSeconds(1)));
        Assert.NotEqual(digest, LocalHealthRunIntent.Digest(Guid.NewGuid(), "Health check", "Inspect local services.", version));
        Assert.Equal("system.health", LocalHealthRunIntent.Action);
        Assert.Equal("control-plane:health:read", LocalHealthRunIntent.Scope);
    }

    [Fact]
    public void IntentDigestComparison_RequiresCanonicalLowercaseSha256()
    {
        var digest = new string('a', 64);
        Assert.True(LocalHealthRunIntent.Matches(digest, digest));
        Assert.False(LocalHealthRunIntent.Matches(digest.ToUpperInvariant(), digest));
        Assert.False(LocalHealthRunIntent.Matches("not-a-hash", digest));
        Assert.False(LocalHealthRunIntent.Matches(null, digest));
    }

    [Fact]
    public void LocalSession_RequiresConfiguredRandomBootstrapTokenAndActor()
    {
        var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var actorId = Guid.NewGuid();
        var sessions = LocalOperatorSessions.Create(bootstrap, actorId.ToString());
        Assert.NotNull(sessions);

        var token = sessions.IssueSessionToken(bootstrap);
        Assert.NotNull(token);
        Assert.NotEqual(bootstrap, token);
        Assert.Equal(actorId, sessions.Authenticate($"Bearer {token}"));
        Assert.Null(sessions.Authenticate($"Bearer {bootstrap}"));
        Assert.Null(sessions.Authenticate($"Bearer {new string('A', 44)}"));
        Assert.Null(sessions.Authenticate(token));
        Assert.Null(sessions.IssueSessionToken(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    }

    [Theory]
    [InlineData(null, "018f23a2-8d75-4a00-8000-000000000001")]
    [InlineData("short", "018f23a2-8d75-4a00-8000-000000000001")]
    [InlineData("not-base64!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!", "018f23a2-8d75-4a00-8000-000000000001")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "00000000-0000-0000-0000-000000000000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "not-a-guid")]
    public void InvalidRegistrationConfiguration_DisablesLocalSessions(string? token, string actorId)
    {
        Assert.Null(LocalOperatorSessions.Create(token, actorId));
    }
}
