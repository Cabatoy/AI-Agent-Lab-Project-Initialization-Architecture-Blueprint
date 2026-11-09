using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ControlPlane.Api.Tests;

public sealed class GitPlanDecisionTests
{
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Operator(LocalOperatorSessions Sessions, Guid Actor, string Header, Clock Clock);
    private static Operator Login()
    {
        var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var actor = Guid.NewGuid();
        var clock = new Clock();
        var sessions = LocalOperatorSessions.Create(bootstrap, actor.ToString(), clock)!;
        return new(sessions, actor, "Bearer " + sessions.IssueSessionToken(bootstrap), clock);
    }

    [Fact]
    public async Task FinalAuthorization_RejectsRevokedExpiredAndWrongActor()
    {
        var op = Login();
        var commits = 0;
        Task Commit() { commits++; return Task.CompletedTask; }
        Assert.False(await op.Sessions.CommitDecisionAsync(op.Header, Guid.NewGuid(), Commit, default));
        op.Clock.Now = op.Clock.Now.AddMinutes(15);
        Assert.False(await op.Sessions.CommitDecisionAsync(op.Header, op.Actor, Commit, default));
        op = Login();
        Assert.True(op.Sessions.Revoke(op.Header));
        Assert.False(await op.Sessions.CommitDecisionAsync(op.Header, op.Actor, Commit, default));
        Assert.Equal(0, commits);
    }

    [Fact]
    public async Task FinalAuthorization_OrdersRevocationAfterAlreadyAuthorizedCommit()
    {
        var op = Login();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = op.Sessions.CommitDecisionAsync(op.Header, op.Actor, async () =>
        {
            entered.SetResult();
            await release.Task;
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revoke = op.Sessions.RevokeAsync(op.Header, default);
        try { Assert.False(revoke.IsCompleted); }
        finally { release.SetResult(); }
        Assert.True(await commit);
        Assert.True(await revoke);
        Assert.Null(op.Sessions.Authenticate(op.Header));
        Assert.False(await Login().Sessions.CommitDecisionAsync(null, op.Actor,
            () => throw new InvalidOperationException(), default));
    }

    [Fact]
    public async Task ExpiryWhileWaitingForCommitGate_IsRevalidated()
    {
        var op = Login();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = op.Sessions.CommitDecisionAsync(op.Header, op.Actor, () => release.Task, default);
        var writes = 0;
        var waiting = op.Sessions.CommitDecisionAsync(op.Header, op.Actor,
            () => { writes++; return Task.CompletedTask; }, default);
        Assert.False(waiting.IsCompleted);
        op.Clock.Now = op.Clock.Now.AddMinutes(15);
        release.SetResult();
        Assert.True(await first);
        Assert.False(await waiting);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task FailedCommit_ReleasesAuthorizationGate()
    {
        var op = Login();
        await Assert.ThrowsAsync<IOException>(() => op.Sessions.CommitDecisionAsync(
            op.Header, op.Actor, () => throw new IOException(), default));
        Assert.True(op.Sessions.Revoke(op.Header));
    }

    [Fact]
    public void PlanValidation_RejectsInvalidDigestRevisionActorAndScope()
    {
        var plan = new GitEditPlan(1, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64),
            "fixture", @"C:\fixture", new string('b', 40), "message.txt", "review\n", DateTime.UtcNow.AddMinutes(4));
        Assert.True(GitPlanDecisionStore.Valid(plan, plan.Digest()));
        foreach (var changed in new[] { plan with { Revision = 2 }, plan with { ActorId = Guid.Empty },
            plan with { TaskId = Guid.Empty }, plan with { File = "../outside" }, plan with { Content = "invalid" } })
            Assert.False(GitPlanDecisionStore.Valid(changed, changed.Digest()));
        Assert.False(GitPlanDecisionStore.Valid(plan, new string('0', 64)));
        Assert.False(GitPlanDecisionStore.Valid(plan, null));
        Assert.False(GitPlanDecisionStore.Valid(null, plan.Digest()));
    }

    [PostgresFact]
    public async Task Rejection_IsDurableScopedIdempotentAndDoesNotReserveTask()
    {
        await Database(async (cs, db, id) =>
        {
            var op = Login();
            var store = new GitPlanDecisionStore(cs);
            var plan = await Issue(store, cs, id, op);
            var rejected = await Decide(store, plan, op, false);
            Assert.Equal("accepted", rejected.Code);
            Assert.Equal("rejected", rejected.Decision!.Decision);
            Assert.Null(rejected.Decision.RunId);
            var replay = await Decide(new GitPlanDecisionStore(cs), plan, op, false);
            Assert.Equal("replay", replay.Code);
            Assert.Equal(rejected.Decision, replay.Decision);
            Assert.Equal("already-decided", (await Decide(store, plan, op, true)).Code);
            Assert.Equal("Draft", await db.ExecuteScalarAsync<string>("select status from tasks"));
            Assert.Equal(0, await Count(db, "native_health_runs"));
            Assert.Equal(0, await Count(db, "native_health_run_audit"));
            Assert.Equal(1, await Count(db, "hic_git_plan_decisions"));
            var next = await Issue(store, cs, id, op, "revised\n");
            Assert.Equal("accepted", (await Decide(store, next, op, true)).Code);
            Assert.Equal("already-decided", (await Decide(store, plan, op, true)).Code);
            Assert.Equal(2, await Count(db, "hic_git_plan_decisions"));
            await db.ExecuteAsync("update tasks set status='Approved',updated_at_utc=clock_timestamp()");
            Assert.Equal(rejected.Decision, (await Decide(store, plan, op, false)).Decision);
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.ExecuteAsync("update hic_git_plan_decisions set decision='approved'"));
            await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("delete from hic_git_plan_decisions"));
            await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("truncate hic_git_plan_decisions"));
            await Assert.ThrowsAsync<PostgresException>(() =>
                db.ExecuteAsync("update hic_git_plan_previews set expires_at_utc=expires_at_utc+interval '1 minute'"));
        });
    }

    [PostgresFact]
    public async Task Approval_ReplayAndOppositeDecisionPreserveRunAndAudit()
    {
        await Database(async (cs, db, id) =>
        {
            var op = Login();
            var store = new GitPlanDecisionStore(cs);
            var plan = await Issue(store, cs, id, op);
            var result = await Decide(store, plan, op, true);
            Assert.Equal("accepted", result.Code);
            var before = await Snapshot(db);
            Assert.Equal(result.Run, (await Decide(new GitPlanDecisionStore(cs), plan, op, true)).Run);
            Assert.Equal("already-decided", (await Decide(store, plan, op, false)).Code);
            Assert.Equal(before, await Snapshot(db));
            Assert.Equal(2, await Count(db, "native_health_run_audit"));
            var claim = await new LocalHealthRunStore(cs).ClaimAsync(result.Run!.RunId, op.Actor, default,
                LocalHealthRunIntent.Action);
            Assert.Equal("isolation-required", claim.Code);
            Assert.Equal(before, await Snapshot(db));
        });
    }

    [PostgresFact]
    public async Task ExistingLegacyApproval_CannotBeRejectedOrRewritten()
    {
        await Database(async (cs, db, id) =>
        {
            var op = Login();
            var store = new GitPlanDecisionStore(cs);
            var plan = await Issue(store, cs, id, op);
            var health = new LocalHealthRunStore(cs);
            var intent = (await health.GetIntentAsync(id, default))!;
            Assert.Equal("accepted", (await health.ApproveAsync(id, op.Actor, intent.IntentDigestSha256, default)).Code);
            var before = await Snapshot(db);
            Assert.Equal("already-approved", (await Decide(store, plan, op, false)).Code);
            Assert.Equal("already-approved", (await Decide(store, plan, op, true)).Code);
            Assert.Equal(before, await Snapshot(db));
            Assert.Equal(0, await Count(db, "hic_git_plan_decisions"));
        });
    }

    [PostgresFact]
    public async Task DigestIsNotAuthority_UnissuedModifiedActorAndTaskPlansCannotBeDecided()
    {
        await Database(async (cs, db, id) =>
        {
            var op = Login();
            var store = new GitPlanDecisionStore(cs);
            var plan = await Issue(store, cs, id, op);
            foreach (var changed in new[] { plan with { Content = "forged\n" },
                plan with { ExpiresAtUtc = plan.ExpiresAtUtc.AddSeconds(1) },
                plan with { BaseCommit = new string('c', 40) } })
                Assert.Equal("preview-not-issued", (await Decide(store, changed, op, false)).Code);
            Assert.Equal("invalid-plan", (await store.DecideAsync(Guid.NewGuid(), op.Actor, plan,
                plan.Digest(), false, op.Sessions, op.Header, default)).Code);
            Assert.Equal("invalid-plan", (await store.DecideAsync(id, Guid.NewGuid(), plan,
                plan.Digest(), false, op.Sessions, op.Header, default)).Code);
            Assert.Equal("invalid-plan", (await store.DecideAsync(id, op.Actor, plan,
                new string('0', 64), false, op.Sessions, op.Header, default)).Code);
            Assert.Equal(0, await Count(db, "hic_git_plan_decisions"));
            Assert.Equal(0, await Count(db, "native_health_runs"));
        });
    }

    [PostgresFact]
    public async Task StaleTaskAndExpiredPreview_DoNotCreateDecisions()
    {
        await Database(async (cs, db, id) =>
        {
            var op = Login();
            var store = new GitPlanDecisionStore(cs);
            var plan = await Issue(store, cs, id, op);
            await db.ExecuteAsync("update tasks set description='changed',updated_at_utc=clock_timestamp()");
            Assert.Equal("intent-changed", (await Decide(store, plan, op, false)).Code);
            Assert.Equal("intent-changed", (await Decide(store, plan, op, true)).Code);
            var shortPlan = await Issue(store, cs, id, op, expiry: DateTime.UtcNow.AddSeconds(1));
            await Task.Delay(1100);
            Assert.Equal("intent-changed", (await Decide(store, shortPlan, op, false)).Code);
            Assert.Equal(0, await Count(db, "hic_git_plan_decisions"));
            Assert.Equal(0, await Count(db, "native_health_runs"));
        });
    }

    [PostgresFact]
    public async Task ConcurrentOppositeAndSameDecisions_HaveOneDurableWinner()
    {
        foreach (var (first, second) in new[] { (true, false), (true, true), (false, false) })
        {
            await Database(async (cs, db, id) =>
            {
                var op = Login();
                var store = new GitPlanDecisionStore(cs);
                var plan = await Issue(store, cs, id, op);
                var results = await Task.WhenAll(Decide(store, plan, op, first), Decide(store, plan, op, second));
                Assert.Single(results, x => x.Code == "accepted");
                Assert.Single(results, x => x.Code == (first == second ? "replay" : "already-decided"));
                Assert.Equal(1, await Count(db, "hic_git_plan_decisions"));
                var approved = await db.ExecuteScalarAsync<bool>(
                    "select decision='approved' from hic_git_plan_decisions");
                Assert.Equal(approved ? 1 : 0, await Count(db, "native_health_runs"));
                Assert.Equal(approved ? 2 : 0, await Count(db, "native_health_run_audit"));
            });
        }
    }

    [PostgresFact]
    public async Task RepositoryAuthorizationFailure_DoesNotDecideOrReserveTask()
    {
        await Database(async (cs, db, id) =>
        {
            var op = Login();
            var store = new GitPlanDecisionStore(cs);
            var plan = await Issue(store, cs, id, op);
            var result = await store.DecideAsync(id, op.Actor, plan, plan.Digest(), true,
                op.Sessions, op.Header, default, () => Task.FromResult(false));
            Assert.Equal("repository-changed", result.Code);
            Assert.Equal(0, await Count(db, "hic_git_plan_decisions"));
            Assert.Equal(0, await Count(db, "native_health_runs"));
            Assert.Equal("accepted", (await Decide(store, plan, op, false)).Code);
        });
    }

    [PostgresFact]
    public async Task AuditOrDecisionInsertFailure_RollsBackApprovalCompletely()
    {
        foreach (var (table, approve) in new[]
                 {
                     ("native_health_run_audit", true),
                     ("hic_git_plan_decisions", true),
                     ("hic_git_plan_decisions", false)
                 })
        {
            await Database(async (cs, db, id) =>
            {
                var op = Login();
                var store = new GitPlanDecisionStore(cs);
                var plan = await Issue(store, cs, id, op);
                await db.ExecuteAsync($"""
                    create function deny_insert() returns trigger language plpgsql as $$
                    begin raise exception 'fixture-denied'; end $$;
                    create trigger deny_insert before insert on {table} for each row execute function deny_insert();
                    """);
                await Assert.ThrowsAsync<PostgresException>(() => Decide(store, plan, op, approve));
                Assert.Equal(0, await Count(db, "native_health_runs"));
                Assert.Equal(0, await Count(db, "native_health_run_audit"));
                Assert.Equal(0, await Count(db, "hic_git_plan_decisions"));
                await db.ExecuteAsync($"drop trigger deny_insert on {table}");
                Assert.Equal("accepted", (await Decide(store, plan, op, false)).Code);
            });
        }
    }

    [PostgresFact]
    public async Task RevokeOrExpiryWhileWaitingForTaskLock_RollsBackDecision()
    {
        foreach (var expire in new[] { false, true })
        {
            await Database(async (cs, db, id) =>
            {
                var op = Login();
                var store = new GitPlanDecisionStore(cs);
                var plan = await Issue(store, cs, id, op);
                await using var blocker = new NpgsqlConnection(cs);
                await blocker.OpenAsync();
                await using var tx = await blocker.BeginTransactionAsync();
                await blocker.ExecuteAsync("select id from tasks where id=@id for update", new { id }, tx);
                var application = "hic62_" + Guid.NewGuid().ToString("N");
                var waitingStore = new GitPlanDecisionStore(
                    new NpgsqlConnectionStringBuilder(cs) { ApplicationName = application }.ConnectionString);
                var waiting = Decide(waitingStore, plan, op, true);
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!await db.ExecuteScalarAsync<bool>("""
                        select exists(select 1 from pg_stat_activity where application_name=@application
                          and wait_event_type='Lock')
                        """, new { application }))
                    {
                        Assert.True(DateTime.UtcNow < deadline, "Decision did not reach task lock.");
                        await Task.Delay(20);
                    }
                    if (expire) op.Clock.Now = op.Clock.Now.AddMinutes(15);
                    else Assert.True(op.Sessions.Revoke(op.Header));
                }
                finally { await tx.RollbackAsync(); }
                Assert.Equal("unauthorized", (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).Code);
                Assert.Equal(0, await Count(db, "native_health_runs"));
                Assert.Equal(0, await Count(db, "native_health_run_audit"));
                Assert.Equal(0, await Count(db, "hic_git_plan_decisions"));
            });
        }
    }

    [PostgresFact]
    public async Task RejectionHttpContract_ValidatesBodySessionReplayAndOppositeDecision()
    {
        await Database(async (cs, db, id) =>
        {
            var op = Login();
            var store = new GitPlanDecisionStore(cs);
            var plan = await Issue(store, cs, id, op);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            GitEditEndpoints.Map(app, cs, op.Sessions);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Services
                    .GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
                var url = $"/local-runner/tasks/{id}/git-reject";
                var body = new { plan, intentDigestSha256 = plan.Digest() };
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostJson(client, url, body)).StatusCode);
                client.DefaultRequestHeaders.Authorization = new("Bearer", op.Header["Bearer ".Length..]);
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, body)).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await PostJson(client, url,
                    new { plan, intentDigestSha256 = plan.Digest(), extra = "untrusted" })).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await PostJson(client, url,
                    new { plan, intentDigestSha256 = "bad" })).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await PostJson(client, url,
                    new { plan = plan with { Content = "forged\n" },
                        intentDigestSha256 = (plan with { Content = "forged\n" }).Digest() })).StatusCode);
                var response = await PostJson(client, url, body);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("rejected", json.GetProperty("decision").GetString());
                Assert.Equal(id, json.GetProperty("taskId").GetGuid());
                Assert.Equal(op.Actor, json.GetProperty("actorId").GetGuid());
                Assert.Equal(plan.Digest(), json.GetProperty("intentDigestSha256").GetString());
                Assert.Equal(GitEditPlan.Action, json.GetProperty("action").GetString());
                Assert.Equal(GitEditPlan.Scope, json.GetProperty("scope").GetString());
                Assert.Equal(JsonValueKind.Null, json.GetProperty("runId").ValueKind);
                Assert.NotEqual(Guid.Empty, json.GetProperty("decisionId").GetGuid());
                _ = json.GetProperty("decidedAtUtc").GetDateTime();
                var replay = await PostJson(client, url, body);
                Assert.Equal(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
                Assert.Equal(HttpStatusCode.Conflict, (await PostJson(client,
                    $"/local-runner/tasks/{id}/git-approve", body)).StatusCode);
                Assert.True(op.Sessions.Revoke(op.Header));
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostJson(client, url, body)).StatusCode);
                Assert.Equal(0, await Count(db, "native_health_runs"));
                Assert.Equal(1, await Count(db, "hic_git_plan_decisions"));
            }
            finally { await app.StopAsync(); }
        });
    }

    [PostgresFact]
    public async Task History_IsBoundedStableActorTaskScopedAndReturnsExactPlans()
    {
        await Database(async (cs, db, id) =>
        {
            var op = Login();
            var other = Login();
            var store = new GitPlanDecisionStore(cs);
            var plans = new Dictionary<string, GitEditPlan>();
            var timestamp = DateTime.UtcNow.AddMinutes(-1);
            for (var index = 0; index < GitPlanDecisionStore.HistoryLimit + 2; index++)
            {
                var plan = await Issue(store, cs, id, op, $"review {index}\n");
                plans.Add(plan.Digest(), plan);
                // Eşit timestamp UUID tie-break sırasını da doğrular.
                await db.ExecuteAsync("""
                    insert into hic_git_plan_decisions
                        (decision_id,intent_digest_sha256,task_id,actor_id,decision,decided_at_utc)
                    values (@decisionId,@digest,@id,@actor,'rejected',@timestamp)
                    """, new { decisionId = Guid.NewGuid(), digest = plan.Digest(), id, actor = op.Actor, timestamp });
            }
            var otherPlan = await Issue(store, cs, id, other, "other actor\n");
            Assert.Equal("accepted", (await Decide(store, otherPlan, other, false)).Code);
            var otherTask = Guid.NewGuid();
            await db.ExecuteAsync("insert into tasks values(@otherTask,'Other','Fixture','Draft',clock_timestamp())",
                new { otherTask });
            var otherTaskPlan = await Issue(store, cs, otherTask, op, "other task\n");
            Assert.Equal("accepted", (await Decide(store, otherTaskPlan, op, false)).Code);
            var before = await Snapshot(db);
            var history = (await store.HistoryAsync(id, op.Actor, op.Sessions, op.Header, default))!;
            var replay = (await new GitPlanDecisionStore(cs).HistoryAsync(id, op.Actor,
                op.Sessions, op.Header, default))!;
            Assert.Equal(GitPlanDecisionStore.HistoryLimit, history.Limit);
            Assert.Equal(history.Limit, history.Decisions.Count);
            Assert.True(history.HasMore);
            Assert.Equal(JsonSerializer.Serialize(history), JsonSerializer.Serialize(replay));
            var expected = (await db.QueryAsync<Guid>("""
                select decision_id from hic_git_plan_decisions where task_id=@id and actor_id=@actor
                order by decided_at_utc desc, decision_id desc limit 50
                """, new { id, actor = op.Actor })).ToArray();
            Assert.Equal(expected, history.Decisions.Select(entry => entry.DecisionId));
            foreach (var entry in history.Decisions)
            {
                Assert.Equal(id, entry.TaskId);
                Assert.Equal(op.Actor, entry.ActorId);
                Assert.Equal(GitEditPlan.Action, entry.Action);
                Assert.Equal(GitEditPlan.Scope, entry.Scope);
                Assert.Equal("rejected", entry.Decision);
                Assert.Null(entry.RunId);
                Assert.Equal(plans[entry.IntentDigestSha256], entry.Plan.Deserialize<GitEditPlan>(GitEditExecutor.JsonOptions));
            }
            var otherHistory = (await store.HistoryAsync(id, other.Actor, other.Sessions, other.Header, default))!;
            Assert.Single(otherHistory.Decisions);
            Assert.Equal(otherPlan.Digest(), otherHistory.Decisions[0].IntentDigestSha256);
            Assert.False(otherHistory.HasMore);
            var unknown = (await store.HistoryAsync(Guid.NewGuid(), op.Actor, op.Sessions, op.Header, default))!;
            Assert.Empty(unknown.Decisions);
            Assert.False(unknown.HasMore);
            Assert.Null(await store.HistoryAsync(id, other.Actor, op.Sessions, op.Header, default));
            Assert.Equal(before, await Snapshot(db));
        });
    }

    [PostgresFact]
    public async Task HistoryHttpContract_IsAuthenticatedActorScopedAndMigrationFailClosed()
    {
        await Database(async (cs, db, id) =>
        {
            var op = Login();
            var other = Login();
            var store = new GitPlanDecisionStore(cs);
            var plan = await Issue(store, cs, id, op);
            var rejected = await Decide(store, plan, op, false);
            var next = await Issue(store, cs, id, op, "approve\n");
            var approved = await Decide(store, next, op, true);
            var before = await Snapshot(db);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            GitEditEndpoints.Map(app, cs, op.Sessions);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Services
                    .GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
                var url = $"/local-runner/tasks/{id}/git-decisions";
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
                client.DefaultRequestHeaders.Authorization = new("Bearer", other.Header["Bearer ".Length..]);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
                client.DefaultRequestHeaders.Authorization = new("Bearer", op.Header["Bearer ".Length..]);
                var response = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(50, json.GetProperty("limit").GetInt32());
                Assert.False(json.GetProperty("hasMore").GetBoolean());
                var entries = json.GetProperty("decisions").EnumerateArray().ToArray();
                Assert.Equal(2, entries.Length);
                var reject = entries.Single(entry => entry.GetProperty("decision").GetString() == "rejected");
                Assert.Equal(rejected.Decision!.DecisionId, reject.GetProperty("decisionId").GetGuid());
                Assert.Equal(id, reject.GetProperty("taskId").GetGuid());
                Assert.Equal(op.Actor, reject.GetProperty("actorId").GetGuid());
                Assert.Equal(GitEditPlan.Action, reject.GetProperty("action").GetString());
                Assert.Equal(GitEditPlan.Scope, reject.GetProperty("scope").GetString());
                Assert.Equal(plan.Digest(), reject.GetProperty("intentDigestSha256").GetString());
                Assert.Equal(JsonValueKind.Null, reject.GetProperty("runId").ValueKind);
                Assert.Equal(plan, reject.GetProperty("plan").Deserialize<GitEditPlan>(GitEditExecutor.JsonOptions));
                Assert.Equal(rejected.Decision.DecidedAtUtc, reject.GetProperty("decidedAtUtc").GetDateTime());
                Assert.Equal(approved.Run!.RunId, entries.Single(entry =>
                    entry.GetProperty("decision").GetString() == "approved").GetProperty("runId").GetGuid());
                Assert.Equal(await response.Content.ReadAsStringAsync(), await client.GetStringAsync(url));
                op.Clock.Now = op.Clock.Now.AddMinutes(15);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
                op.Clock.Now = op.Clock.Now.AddMinutes(-15);
                // Expired token temizlendiğinden saati geri almak erişimi geri açmaz.
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
                Assert.Equal(before, await Snapshot(db));
            }
            finally { await app.StopAsync(); }
            var otherBuilder = WebApplication.CreateBuilder();
            otherBuilder.Logging.ClearProviders();
            otherBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var otherApp = otherBuilder.Build();
            GitEditEndpoints.Map(otherApp, cs, other.Sessions);
            await otherApp.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(otherApp.Services
                    .GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
                client.DefaultRequestHeaders.Authorization = new("Bearer", other.Header["Bearer ".Length..]);
                var url = $"/local-runner/tasks/{id}/git-decisions";
                var response = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Empty(json.GetProperty("decisions").EnumerateArray());
                Assert.False(json.GetProperty("hasMore").GetBoolean());
                Assert.True(await other.Sessions.RevokeAsync(other.Header, default));
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
                Assert.Equal(before, await Snapshot(db));
            }
            finally { await otherApp.StopAsync(); }
        });
        await GitEditRunnerTests.Database(async (cs, db, id) =>
        {
            var op = Login();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            GitEditEndpoints.Map(app, cs, op.Sessions);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Services
                    .GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
                client.DefaultRequestHeaders.Authorization = new("Bearer", op.Header["Bearer ".Length..]);
                Assert.Equal(HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync($"/local-runner/tasks/{id}/git-decisions")).StatusCode);
                Assert.False(await db.ExecuteScalarAsync<bool>(
                    "select exists(select 1 from app_schema_versions where version=10)"));
            }
            finally { await app.StopAsync(); }
        });
    }

    [PostgresFact]
    public async Task HistoryRead_RevalidatesSessionAfterDatabaseWait()
    {
        foreach (var expire in new[] { false, true })
        {
            await Database(async (cs, db, id) =>
            {
                var op = Login();
                var store = new GitPlanDecisionStore(cs);
                var plan = await Issue(store, cs, id, op);
                Assert.Equal("accepted", (await Decide(store, plan, op, false)).Code);
                await using var blocker = new NpgsqlConnection(cs);
                await blocker.OpenAsync();
                await using var tx = await blocker.BeginTransactionAsync();
                await blocker.ExecuteAsync("lock table hic_git_plan_decisions in access exclusive mode", transaction: tx);
                var application = "hic62_history_" + Guid.NewGuid().ToString("N");
                var waitingStore = new GitPlanDecisionStore(
                    new NpgsqlConnectionStringBuilder(cs) { ApplicationName = application }.ConnectionString);
                var reading = waitingStore.HistoryAsync(id, op.Actor, op.Sessions, op.Header, default);
                try
                {
                    await WaitForLock(db, application);
                    if (expire) op.Clock.Now = op.Clock.Now.AddMinutes(15);
                    else Assert.True(await op.Sessions.RevokeAsync(op.Header, default));
                }
                finally { await tx.RollbackAsync(); }
                Assert.Null(await reading.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(1, await Count(db, "hic_git_plan_decisions"));
                Assert.Equal(0, await Count(db, "native_health_runs"));
            });
        }
    }

    [PostgresFact]
    public async Task HealthApprovalAndGitDecision_SerializeOnSameTaskLock()
    {
        foreach (var approve in new[] { false, true })
        foreach (var gitFirst in new[] { false, true })
        {
            await Database(async (cs, db, id) =>
            {
                var op = Login();
                var gitStore = new GitPlanDecisionStore(cs);
                var plan = await Issue(gitStore, cs, id, op);
                var intent = (await new LocalHealthRunStore(cs).GetIntentAsync(id, default))!;
                await using var blocker = new NpgsqlConnection(cs);
                await blocker.OpenAsync();
                await using var tx = await blocker.BeginTransactionAsync();
                await blocker.ExecuteAsync("select id from tasks where id=@id for update", new { id }, tx);
                var gitApplication = "hic62_git_" + Guid.NewGuid().ToString("N");
                var healthApplication = "hic62_health_" + Guid.NewGuid().ToString("N");
                var git = new GitPlanDecisionStore(new NpgsqlConnectionStringBuilder(cs)
                    { ApplicationName = gitApplication }.ConnectionString);
                var health = new LocalHealthRunStore(new NpgsqlConnectionStringBuilder(cs)
                    { ApplicationName = healthApplication }.ConnectionString);
                Task<GitPlanDecisionOutcome> gitDecision;
                Task<(string Code, LocalHealthRun? Run)> healthDecision;
                try
                {
                    if (gitFirst)
                    {
                        gitDecision = Decide(git, plan, op, approve);
                        await WaitForLock(db, gitApplication);
                        healthDecision = health.ApproveAsync(id, op.Actor, intent.IntentDigestSha256, default);
                        await WaitForLock(db, healthApplication);
                    }
                    else
                    {
                        healthDecision = health.ApproveAsync(id, op.Actor, intent.IntentDigestSha256, default);
                        await WaitForLock(db, healthApplication);
                        gitDecision = Decide(git, plan, op, approve);
                        await WaitForLock(db, gitApplication);
                    }
                }
                finally { await tx.RollbackAsync(); }
                var gitResult = await gitDecision.WaitAsync(TimeSpan.FromSeconds(10));
                var healthResult = await healthDecision.WaitAsync(TimeSpan.FromSeconds(10));
                if (!gitFirst)
                {
                    Assert.Equal("accepted", healthResult.Code);
                    Assert.Equal("already-approved", gitResult.Code);
                    Assert.Equal(0, await Count(db, "hic_git_plan_decisions"));
                }
                else if (approve)
                {
                    Assert.Equal("accepted", gitResult.Code);
                    Assert.Equal("already-approved", healthResult.Code);
                    Assert.Equal(gitResult.Run!.RunId, await db.ExecuteScalarAsync<Guid>(
                        "select run_id from native_health_runs"));
                    Assert.Equal(1, await Count(db, "hic_git_plan_decisions"));
                }
                else
                {
                    Assert.Equal("accepted", gitResult.Code);
                    Assert.Equal("accepted", healthResult.Code);
                    Assert.Equal("rejected", gitResult.Decision!.Decision);
                    Assert.Null(gitResult.Decision.RunId);
                    Assert.Equal(1, await Count(db, "hic_git_plan_decisions"));
                }
                Assert.Equal(1, await Count(db, "native_health_runs"));
                Assert.Equal(2, await Count(db, "native_health_run_audit"));
                Assert.Equal("Draft", await db.ExecuteScalarAsync<string>("select status from tasks"));
                Assert.Equal(gitFirst && approve ? GitEditPlan.Action : LocalHealthRunIntent.Action,
                    await db.ExecuteScalarAsync<string>("select action from native_health_runs"));
            });
        }
    }

    private static async Task WaitForLock(NpgsqlConnection db, string application)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await db.ExecuteScalarAsync<bool>("""
            select exists(select 1 from pg_stat_activity where application_name=@application
                and wait_event_type='Lock')
            """, new { application }))
        {
            Assert.True(DateTime.UtcNow < deadline, "Operation did not reach database lock.");
            await Task.Delay(20);
        }
    }

    [PostgresFact]
    public async Task MissingDecisionMigration_FailsClosedWithoutChangingExistingSchema()
    {
        await GitEditRunnerTests.Database(async (cs, db, id) =>
        {
            Assert.False(await new GitPlanDecisionStore(cs).ReadyAsync(default));
            Assert.False(await db.ExecuteScalarAsync<bool>(
                "select exists(select 1 from app_schema_versions where version=10)"));
            Assert.Null(await db.ExecuteScalarAsync<string>(
                "select to_regclass('hic_git_plan_decisions')::text"));
            Assert.Equal(0, await Count(db, "native_health_runs"));
        });
    }

    [PostgresFact]
    public async Task ApprovalHttpReplay_PreservesExistingContractEvenIfRepositoryIsNoLongerAvailable()
    {
        await Database(async (cs, db, id) =>
        {
            var op = Login();
            var store = new GitPlanDecisionStore(cs);
            var plan = await Issue(store, cs, id, op);
            var approval = await Decide(store, plan, op, true);
            var before = await Snapshot(db);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            GitEditEndpoints.Map(app, cs, op.Sessions);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Services
                    .GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
                client.DefaultRequestHeaders.Authorization = new("Bearer", op.Header["Bearer ".Length..]);
                var body = new { plan, intentDigestSha256 = plan.Digest() };
                var response = await PostJson(client, $"/local-runner/tasks/{id}/git-approve", body);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                Assert.Equal($"/local-runner/runs/{approval.Run!.RunId}", response.Headers.Location!.OriginalString);
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(approval.Run.RunId, json.GetProperty("runId").GetGuid());
                Assert.Equal(id, json.GetProperty("taskId").GetGuid());
                Assert.Equal("queued", json.GetProperty("status").GetString());
                Assert.Equal(GitEditPlan.Action, json.GetProperty("action").GetString());
                Assert.Equal(GitEditPlan.Scope, json.GetProperty("scope").GetString());
                Assert.Equal(plan.Digest(), json.GetProperty("intentDigestSha256").GetString());
                Assert.Equal(HttpStatusCode.Conflict, (await PostJson(client,
                    $"/local-runner/tasks/{id}/git-reject", body)).StatusCode);
                Assert.Equal(before, await Snapshot(db));
            }
            finally { await app.StopAsync(); }
        });
    }

    private static async Task<HttpResponseMessage> PostJson(HttpClient client, string url, object body)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body, GitEditExecutor.JsonOptions),
            System.Text.Encoding.UTF8, "application/json");
        return await client.PostAsync(url, content);
    }

    private static Task<GitPlanDecisionOutcome> Decide(GitPlanDecisionStore store, GitEditPlan plan,
        Operator op, bool approve) => store.DecideAsync(plan.TaskId, op.Actor, plan, plan.Digest(),
        approve, op.Sessions, op.Header, default);

    private static async Task<GitEditPlan> Issue(GitPlanDecisionStore store, string cs, Guid taskId,
        Operator op, string content = "review\n", DateTime? expiry = null)
    {
        var intent = (await new LocalHealthRunStore(cs).GetIntentAsync(taskId, default))!;
        var expires = expiry ?? DateTime.UtcNow.AddMinutes(4);
        var plan = new GitEditPlan(1, taskId, op.Actor, intent.IntentDigestSha256, "fixture",
            @"C:\fixture", new string('b', 40), "message.txt", content,
            new DateTime(expires.Ticks / 10 * 10, DateTimeKind.Utc));
        Assert.True(await store.IssueAsync(plan, op.Sessions, op.Header, default));
        return plan;
    }

    private static Task<int> Count(NpgsqlConnection db, string table) =>
        db.ExecuteScalarAsync<int>($"select count(*)::int from {table}");
    private static Task<string?> Snapshot(NpgsqlConnection db) => db.ExecuteScalarAsync<string>("""
        select jsonb_build_object('runs',(select jsonb_agg(to_jsonb(r) order by run_id) from native_health_runs r),
          'audit',(select jsonb_agg(to_jsonb(a) order by id) from native_health_run_audit a))::text
        """);

    private static Task Database(Func<string, NpgsqlConnection, Guid, Task> test) =>
        GitEditRunnerTests.Database(async (cs, db, id) =>
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "HumanInControl.sln"))) root = root.Parent;
            Assert.NotNull(root);
            await using var tx = await db.BeginTransactionAsync();
            await db.ExecuteAsync(File.ReadAllText(Path.Combine(root.FullName,
                "src", "ControlPlane", "Migrations", "010-git-plan-decisions.sql")), transaction: tx);
            await tx.CommitAsync();
            await test(cs, db, id);
        });
}
