using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ControlPlane.Api.Tests;

public sealed class GitEditRunnerTests
{
    [Fact]
    public void Digest_BindsEveryMaterialField()
    {
        var plan = new GitEditPlan(1, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64),
            "fixture", System.IO.Path.GetTempPath(), new string('b', 40), "message.txt", "hello\n",
            DateTime.UtcNow.AddMinutes(4));
        var digest = plan.Digest();
        foreach (var changed in new[]
        {
            plan with { Revision = 2 }, plan with { TaskId = Guid.NewGuid() },
            plan with { ActorId = Guid.NewGuid() }, plan with { TaskDigest = new string('c', 64) },
            plan with { RepositoryId = "other" }, plan with { RepositoryPath = "other" },
            plan with { BaseCommit = new string('d', 40) }, plan with { File = "../outside" },
            plan with { Content = "changed\n" }, plan with { ExpiresAtUtc = plan.ExpiresAtUtc.AddSeconds(1) }
        }) Assert.NotEqual(digest, changed.Digest());
        Assert.Equal(digest, JsonSerializer.Deserialize<GitEditPlan>(
            JsonSerializer.Serialize(plan, GitEditExecutor.JsonOptions), GitEditExecutor.JsonOptions)!.Digest());
    }

    [Theory]
    [InlineData("")]
    [InlineData("no newline")]
    [InlineData("control\u001b\n")]
    [InlineData("unicode\u00e9\n")]
    public void Content_RejectsUnboundedOrControlText(string text) => Assert.False(GitEditExecutor.ValidContent(text));

    [Fact]
    public void Content_BoundsAreExact()
    {
        Assert.True(GitEditExecutor.ValidContent(new string('a', 511) + "\n"));
        Assert.False(GitEditExecutor.ValidContent(new string('a', 512) + "\n"));
        Assert.False(GitEditExecutor.ValidContent(null));
        Assert.True(GitEditExecutor.ValidContent("Windows text\r\n"));
        Assert.False(GitEditExecutor.ValidContent("standalone\rcarriage\n"));
    }

    [Fact]
    public void NetworkPaths_AreRejectedBeforeFilesystemAccess()
    {
        Assert.Throws<GitEditDeniedException>(() => GitEditExecutor.CheckPath(@"\\example.invalid\share"));
        Assert.Throws<GitEditDeniedException>(() => GitEditExecutor.CheckPath("//example.invalid/share"));
    }

    [Fact]
    public async Task RealWorktree_OnlyOneFileChanges_SourceCommitRemainsUnchanged()
    {
        var (root, repo, configuration) = await Fixture();
        try
        {
            var plan = Plan(repo);
            var result = await GitEditExecutor.ExecuteAsync(configuration, Guid.NewGuid(), plan,
                NullLogger.Instance, CancellationToken.None);
            Assert.Equal("passed:exact-content-and-scope", result.TestCode);
            Assert.True(result.SourceUnchanged);
            Assert.Contains("+approved test edit", result.Diff);
            Assert.Equal("before\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "message.txt")));
            Assert.Equal(repo.BaseCommit, (await GitEditExecutor.GitAsync(result.WorktreePath!,
                CancellationToken.None, "rev-parse", "HEAD")).Trim());
            var replay = await GitEditExecutor.ExecuteAsync(configuration, result.RunId, plan,
                NullLogger.Instance, CancellationToken.None);
            Assert.StartsWith("denied:", replay.TestCode);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task StaleSha_Traversal_ConfigExecution_AndSymlinksFailClosed()
    {
        var (root, repo, configuration) = await Fixture();
        try
        {
            foreach (var changed in new[]
            {
                Plan(repo) with { File = "../outside.txt" }, Plan(repo) with { BaseCommit = new string('a', 40) },
                Plan(repo) with { RepositoryPath = root }, Plan(repo) with { RepositoryId = "unapproved" }
            })
            {
                var denied = await GitEditExecutor.ExecuteAsync(configuration, Guid.NewGuid(), changed,
                    NullLogger.Instance, CancellationToken.None);
                Assert.StartsWith("denied:", denied.TestCode);
                Assert.Empty(denied.Diff);
            }
            var config = System.IO.Path.Combine(repo.Path, ".git", "config");
            File.AppendAllText(config, "\n[alias]\n\tunsafe = !echo sensitive-value\n");
            await Assert.ThrowsAsync<GitEditDeniedException>(() => GitEditExecutor.InspectAsync(repo, CancellationToken.None));
            Assert.Equal("before\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "message.txt")));
            // Directory junctions/symlinks are rejected before any Git invocation.
            if (!OperatingSystem.IsWindows())
            {
                var link = System.IO.Path.Combine(root, "link");
                Directory.CreateSymbolicLink(link, repo.Path);
                Assert.Throws<GitEditDeniedException>(() => GitEditExecutor.CheckPath(link));
            }
        }
        finally { Cleanup(root); }
    }

    [PostgresFact]
    public async Task ApprovalClaim_ConcurrencyActorReplayAuditAndRestartAreDurable()
    {
        await Database(async (cs, db, taskId) =>
        {
            var store = new LocalHealthRunStore(cs);
            var intent = await store.GetIntentAsync(taskId, CancellationToken.None);
            var expiry = DateTime.UtcNow.AddMinutes(4);
            var plan = new GitEditPlan(1, taskId, Guid.NewGuid(), intent!.IntentDigestSha256, "fixture",
                System.IO.Path.GetTempPath(), new string('b', 40), "message.txt", "approved\n",
                new DateTime(expiry.Ticks / 10 * 10, DateTimeKind.Utc));
            var approval = await store.ApproveAsync(taskId, plan.ActorId, plan.Digest(), CancellationToken.None, plan);
            Assert.Equal("accepted", approval.Code);
            Assert.Null(await store.GetIntentAsync(taskId, CancellationToken.None));
            var replay = await store.ApproveAsync(taskId, plan.ActorId, plan.Digest(), CancellationToken.None, plan);
            Assert.Equal(approval.Run!.RunId, replay.Run!.RunId);
            Assert.Equal("actor-mismatch", (await store.ClaimAsync(approval.Run.RunId, Guid.NewGuid(), CancellationToken.None)).Code);
            var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                store.ClaimAsync(approval.Run.RunId, plan.ActorId, CancellationToken.None)));
            Assert.Single(claims, c => c.Code == "claimed");
            Assert.Single(claims, c => c.Code == "not-queued");
            var afterRestart = new LocalHealthRunStore(cs);
            Assert.Equal("running", (await afterRestart.GetAsync(approval.Run.RunId, plan.ActorId, CancellationToken.None))!.Run.Status);
            Assert.Equal("not-queued", (await afterRestart.ClaimAsync(approval.Run.RunId, plan.ActorId, CancellationToken.None)).Code);
            Assert.True(await afterRestart.CompleteGitAsync(approval.Run.RunId, plan.ActorId,
                new GitEditResult(GitEditPlan.Action, approval.Run.RunId, plan.RepositoryId, plan.BaseCommit,
                    null, null, "", "denied:scope-or-infrastructure", false, DateTime.UtcNow), CancellationToken.None));
            Assert.Equal("failed", (await afterRestart.GetAsync(approval.Run.RunId, plan.ActorId, CancellationToken.None))!.Run.Status);
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("select count(*)::int from native_health_run_audit where event_code='failed'"));
            Assert.Equal("Draft", await db.ExecuteScalarAsync<string>("select status from tasks"));
            Assert.Null(await afterRestart.GetAsync(approval.Run.RunId, Guid.NewGuid(), CancellationToken.None));
            Assert.Equal("not-found", (await store.ClaimAsync(Guid.NewGuid(), plan.ActorId, CancellationToken.None)).Code);
            await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("update native_health_run_audit set event_code='queued'"));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("select count(*)::int from native_health_run_audit where event_code='claimed'"));
        });
    }

    [PostgresFact]
    public async Task ChangedTaskAndExpiredOrChangedPlanCannotExecute()
    {
        await Database(async (cs, db, taskId) =>
        {
            var store = new LocalHealthRunStore(cs);
            var intent = (await store.GetIntentAsync(taskId, CancellationToken.None))!;
            var expiry = DateTime.UtcNow.AddMinutes(4);
            var plan = new GitEditPlan(1, taskId, Guid.NewGuid(), intent.IntentDigestSha256, "fixture",
                System.IO.Path.GetTempPath(), new string('b', 40), "message.txt", "approved\n",
                new DateTime(expiry.Ticks / 10 * 10, DateTimeKind.Utc));
            Assert.Null((await store.ApproveAsync(taskId, plan.ActorId, plan.Digest(), CancellationToken.None,
                plan with { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1) })).Run);
            Assert.Null((await store.ApproveAsync(taskId, plan.ActorId, plan.Digest(), CancellationToken.None,
                plan with { Content = "changed\n" })).Run);
            var approved = await store.ApproveAsync(taskId, plan.ActorId, plan.Digest(), CancellationToken.None, plan);
            await db.ExecuteAsync("update tasks set description='changed', updated_at_utc=now()");
            var claim = await store.ClaimAsync(approved.Run!.RunId, plan.ActorId, CancellationToken.None);
            Assert.Equal("task-changed", claim.Code);
            Assert.Equal("invalidated", claim.Run!.Status);
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("select count(*)::int from native_health_run_audit where event_code='claimed'"));
        });
    }

    private static GitEditPlan Plan(GitTestRepository repo) => new(1, Guid.NewGuid(), Guid.NewGuid(),
        new string('a', 64), repo.Id, repo.Path, repo.BaseCommit, "message.txt", "approved test edit\n",
        DateTime.UtcNow.AddMinutes(4));

    private static void Cleanup(string root)
    {
        Assert.StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hic42-"), root);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private static async Task<(string Root, GitTestRepository Repo, IConfiguration Configuration)> Fixture()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hic42-" + Guid.NewGuid().ToString("N"));
        var source = System.IO.Path.Combine(root, "source");
        var runs = System.IO.Path.Combine(root, "runs");
        Directory.CreateDirectory(source); Directory.CreateDirectory(runs);
        File.WriteAllText(System.IO.Path.Combine(source, ".hic-disposable"), GitEditExecutor.Marker);
        File.WriteAllText(System.IO.Path.Combine(source, "message.txt"), "before\n");
        await GitEditExecutor.GitAsync(source, CancellationToken.None, "init", "--initial-branch=main");
        await GitEditExecutor.GitAsync(source, CancellationToken.None, "add", "--", ".hic-disposable", "message.txt");
        await GitEditExecutor.GitAsync(source, CancellationToken.None, "-c", "user.name=HIC Test",
            "-c", "user.email=hic-test@example.invalid", "commit", "-m", "Disposable fixture");
        var repo = new GitTestRepository("fixture", source,
            (await GitEditExecutor.GitAsync(source, CancellationToken.None, "rev-parse", "HEAD")).Trim());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HIC_DISPOSABLE_GIT_REPOSITORIES"] = JsonSerializer.Serialize(new[] { repo }, GitEditExecutor.JsonOptions),
            ["HIC_GIT_RUN_ROOT"] = runs
        }).Build();
        return (root, repo, configuration);
    }

    private static async Task Database(Func<string, NpgsqlConnection, Guid, Task> test)
    {
        var cs = PostgresFixtureSafety.Configuration(Environment.GetEnvironmentVariable("HIC_R1_TEST_POSTGRES"));
        var schema = "hic42_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(cs);
        await admin.OpenAsync();
        await TaskTransitionPostgresTests.VerifyTarget(admin);
        var marker = "hic42-fixture-" + Guid.NewGuid().ToString("N");
        await admin.ExecuteAsync($"create schema {schema}; comment on schema {schema} is '{marker}';");
        var oid = await admin.ExecuteScalarAsync<long>(
            "select oid::bigint from pg_namespace where nspname=@schema", new { schema });
        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema, Pooling = false }.ConnectionString;
            await using var db = new NpgsqlConnection(isolated);
            await db.OpenAsync();
            await db.ExecuteAsync(@"create table tasks (id uuid primary key, title text not null, description text not null,
                status text not null, updated_at_utc timestamptz not null);
                create table app_schema_versions(version integer primary key);
                insert into app_schema_versions values(2);");
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "HumanInControl.sln"))) root = root.Parent;
            Assert.NotNull(root);
            await using (var transaction = await db.BeginTransactionAsync())
            {
                foreach (var migration in new[] { "004-local-health-runner.sql", "005-isolated-git-runner.sql" })
                    await db.ExecuteAsync(File.ReadAllText(System.IO.Path.Combine(root.FullName,
                        "src", "ControlPlane", "Migrations", migration)), transaction: transaction);
                await transaction.CommitAsync();
            }
            var taskId = Guid.NewGuid();
            await db.ExecuteAsync("insert into tasks values(@taskId,'Test','Fixture','Draft',now())", new { taskId });
            await test(isolated, db, taskId);
        }
        finally
        {
            await TaskTransitionPostgresTests.VerifyTarget(admin);
            Assert.Equal(1, await admin.ExecuteScalarAsync<int>(@"select count(*)::int from pg_namespace
                where nspname=@schema and oid::bigint=@oid and pg_get_userbyid(nspowner)=current_user
                and obj_description(oid,'pg_namespace')=@marker", new { schema, oid, marker }));
            await admin.ExecuteAsync($"drop schema {schema} cascade;");
        }
    }
}
