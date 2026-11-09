using System.Text.Json;
using ControlPlane.Api.Tests;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

public sealed class ModelCodeEditTests
{
    private const string Source = "namespace Fixture;\npublic static class Value { public static string Text => \"approved\"; }\n";
    private const string FilePath = "src/Value.cs";

    [Fact]
    public void StructuredReply_RequiresExactShapeAndAuthorizedCSharpPath()
    {
        var valid = JsonSerializer.Serialize(new
        {
            response = "I can make this change.",
            edit = new { file = FilePath, content = "namespace Fixture;\npublic static class Value {}\n" }
        });
        Assert.True(LocalCodeModel.TryParseReply(valid, FilePath, out var reply));
        Assert.Equal("I can make this change.", reply!.Response);
        Assert.Equal(FilePath, reply.File);
        Assert.EndsWith("\n", reply.Content);
        Assert.False(LocalCodeModel.TryParseReply(
            """{"response":"ok","edit":{"file":"../outside.cs","content":"class X {}\n"}}""", FilePath, out _));
        Assert.False(LocalCodeModel.TryParseReply(
            """{"response":"ok","edit":{"file":"src/Value.cs","content":"class X {}\n","command":"whoami"}}""", FilePath, out _));
        Assert.False(LocalCodeModel.TryParseReply(
            """{"response":"ok","edit":null,"extra":true}""", FilePath, out _));
        Assert.False(LocalCodeModel.TryParseReply(
            """{"response":"ok","edit":{"file":"src/Value.txt","content":"class X {}\n"}}""", FilePath, out _));
        Assert.False(LocalCodeModel.TryParseReply(null, FilePath, out _));
    }

    [Fact]
    public void PlanDigest_BindsActorTaskConversationRepositoryBaseFileContentModelAndExpiry()
    {
        var plan = new ModelCodeEditPlan(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), "fixture", "C:\\fixture", new string('b', 40), FilePath, Source,
            "podman-ai-lab", "mistral-local", DateTime.UtcNow.AddMinutes(4));
        var digest = plan.Digest();
        foreach (var changed in new[]
        {
            plan with { TaskId = Guid.NewGuid() }, plan with { ActorId = Guid.NewGuid() },
            plan with { ConversationId = Guid.NewGuid() }, plan with { TaskDigest = new string('c', 64) },
            plan with { RepositoryId = "other" }, plan with { RepositoryPath = "C:\\other" },
            plan with { BaseCommit = new string('d', 40) }, plan with { File = "../outside.cs" },
            plan with { Content = "changed\n" }, plan with { Provider = "ollama" },
            plan with { Model = "other-model" }, plan with { ExpiresAtUtc = plan.ExpiresAtUtc.AddSeconds(1) }
        }) Assert.NotEqual(digest, changed.Digest());
        var roundTrip = JsonSerializer.Deserialize<ModelCodeEditPlan>(
            JsonSerializer.Serialize(plan, GitEditExecutor.JsonOptions), GitEditExecutor.JsonOptions);
        Assert.Equal(digest, roundTrip!.Digest());
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData(".git/config")]
    [InlineData("src/../outside.cs")]
    [InlineData("C:\\outside.cs")]
    [InlineData("src\\Value.cs")]
    public void RepositoryFiles_RejectTraversalAndGitMetadata(string file) =>
        Assert.False(GitEditExecutor.ValidRepositoryFile(file));

    [Fact]
    public void ModelConfiguration_RejectsCloudAndNonLocalEndpointsWithoutFallback()
    {
        var cloud = new LocalCodeModel(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MODEL_PROVIDER"] = "openai",
                ["MODEL_ENDPOINT"] = "https://api.openai.com",
                ["MODEL_NAME"] = "configured-cloud-model"
            }).Build());
        Assert.False(cloud.Info.Configured);
        Assert.Equal("disabled", cloud.Info.Provider);
        Assert.Empty(cloud.Info.Model);

        var local = new LocalCodeModel(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MODEL_PROVIDER"] = "podman-ai-lab",
                ["MODEL_ENDPOINT"] = "http://host.containers.internal:57728",
                ["MODEL_NAME"] = "mistral-local"
            }).Build());
        Assert.True(local.Info.Configured);
        Assert.Equal("podman-ai-lab", local.Info.Provider);
    }

    [Fact]
    public async Task ModelReadiness_ReportsConfiguredModelInventoryAndFailsClosed()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var payload = """{"data":[{"id":"/models/mistral-local"}]}""";
        app.MapGet("/v1/models", () => Results.Text(payload, "application/json"));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            var model = new LocalCodeModel(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MODEL_PROVIDER"] = "podman-ai-lab",
                    ["MODEL_ENDPOINT"] = address,
                    ["MODEL_NAME"] = "mistral-local"
                }).Build());
            Assert.Equal("available", (await model.InspectAsync(CancellationToken.None)).Readiness);
            payload = """{"data":[{"id":"mistral-local"}]}""";
            Assert.Equal("available", (await model.InspectAsync(CancellationToken.None)).Readiness);
            payload = """{"data":[{"id":"other-model"}]}""";
            Assert.Equal("model-not-listed", (await model.InspectAsync(CancellationToken.None)).Readiness);
            payload = """{"unexpected":true}""";
            Assert.Equal("unavailable", (await model.InspectAsync(CancellationToken.None)).Readiness);
            payload = "[]";
            Assert.Equal("unavailable", (await model.InspectAsync(CancellationToken.None)).Readiness);
            payload = new string('x', 33 * 1024);
            Assert.Equal("unavailable", (await model.InspectAsync(CancellationToken.None)).Readiness);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.InspectAsync(cancelled.Token));
            await app.StopAsync();
            Assert.Equal("unavailable", (await model.InspectAsync(CancellationToken.None)).Readiness);
        }
        finally { await app.StopAsync(); }

        var disabled = new LocalCodeModel(new ConfigurationBuilder().Build());
        Assert.Equal("not-configured", (await disabled.InspectAsync(CancellationToken.None)).Readiness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedWorktree_CompilesOnlyTheAuthorizedFileAndPreservesSource(bool existingRepository)
    {
        var root = Path.Combine(Path.GetTempPath(), "hic49-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var runs = Path.Combine(root, "runs");
        string? worktree = null;
        Directory.CreateDirectory(Path.Combine(source, "src"));
        Directory.CreateDirectory(runs);
        try
        {
            File.WriteAllText(Path.Combine(source, existingRepository ? "README.md" : ".hic-disposable"),
                existingRepository ? "# Existing repository\n" : GitEditExecutor.Marker);
            File.WriteAllText(Path.Combine(source, FilePath.Replace('/', Path.DirectorySeparatorChar)), Source);
            await GitEditExecutor.GitAsync(source, CancellationToken.None, "init", "--initial-branch=main");
            await GitEditExecutor.GitAsync(source, CancellationToken.None, "add", "--",
                existingRepository ? "README.md" : ".hic-disposable", FilePath);
            await GitEditExecutor.GitAsync(source, CancellationToken.None, "-c", "user.name=HIC Test",
                "-c", "user.email=hic-test@example.invalid", "commit", "-m", "C# fixture");
            var repo = new GitTestRepository("fixture", source,
                (await GitEditExecutor.GitAsync(source, CancellationToken.None, "rev-parse", "HEAD")).Trim(),
                [FilePath]);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [existingRepository ? "HIC_GITHUB_LOCAL_REPOSITORIES" : "HIC_DISPOSABLE_GIT_REPOSITORIES"] =
                    JsonSerializer.Serialize(new[] { repo }, GitEditExecutor.JsonOptions),
                ["HIC_GIT_RUN_ROOT"] = runs
            }).Build();
            var plan = new ModelCodeEditPlan(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new string('a', 64), repo.Id, repo.Path, repo.BaseCommit, FilePath,
                "namespace Fixture;\npublic static class Value { public static string Text => \"isolated\"; }\n",
                "podman-ai-lab", "mistral-local", DateTime.UtcNow.AddMinutes(4));
            var result = await ModelCodeEditExecutor.ExecuteAsync(configuration, Guid.NewGuid(), plan,
                NullLogger.Instance, CancellationToken.None);
            worktree = result.WorktreePath;
            Assert.Equal("passed:isolated-csharp-compile-and-scope", result.TestCode);
            Assert.True(result.SourceUnchanged);
            Assert.Contains("+public static class Value", result.Diff);
            Assert.Equal(Source, File.ReadAllText(Path.Combine(source, FilePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(repo.BaseCommit,
                (await GitEditExecutor.GitAsync(result.WorktreePath!, CancellationToken.None, "rev-parse", "HEAD")).Trim());
            Assert.Equal(FilePath, (await GitEditExecutor.GitAsync(result.WorktreePath!, CancellationToken.None,
                "diff", "--name-only")).Trim());
        }
        finally
        {
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "hic49-"), root);
            if (worktree is not null && Directory.Exists(worktree))
                await GitEditExecutor.GitAsync(source, CancellationToken.None, "worktree", "remove", "--force", "--", worktree);
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [PostgresFact]
    public async Task ModelApproval_UsesExistingRunClaimAndAppendOnlyAudit()
    {
        await GitEditRunnerTests.Database(async (connectionString, database, taskId) =>
        {
            var store = new LocalHealthRunStore(connectionString);
            var intent = await store.GetIntentAsync(taskId, CancellationToken.None);
            var actorId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            var expiry = DateTime.UtcNow.AddMinutes(4);
            expiry = new DateTime(expiry.Ticks / 10 * 10, DateTimeKind.Utc);
            var baseCommit = new string('b', 40);
            await database.ExecuteAsync(@"
                insert into hic_project_conversations
                    (conversation_id,actor_id,task_id,title,repository_id,repository_path,base_commit,file_path,created_at_utc,updated_at_utc)
                values(@conversationId,@actorId,@taskId,'Test','fixture','C:\fixture',@baseCommit,'src/Value.cs',now(),now());",
                new { conversationId, actorId, taskId, baseCommit });
            var plan = new ModelCodeEditPlan(1, taskId, actorId, conversationId,
                intent!.IntentDigestSha256, "fixture", "C:\\fixture", baseCommit, FilePath, Source,
                "podman-ai-lab", "mistral-local", expiry);
            var approval = await store.ApproveAsync(taskId, actorId, plan.Digest(),
                CancellationToken.None, plan);
            Assert.Equal("accepted", approval.Code);
            Assert.Equal(ModelCodeEditPlan.Action, approval.Run!.Action);
            Assert.Equal(ModelCodeEditPlan.Scope, approval.Run.Scope);
            Assert.Equal(1, await database.ExecuteScalarAsync<int>(
                "select count(*)::int from native_health_runs where conversation_id=@conversationId and plan_json is not null",
                new { conversationId }));
            Assert.Equal("actor-mismatch", (await store.ClaimAsync(approval.Run.RunId, Guid.NewGuid(),
                CancellationToken.None, LocalHealthRunIntent.Action)).Code);
            Assert.Equal("isolation-required", (await store.ClaimAsync(approval.Run.RunId, actorId,
                CancellationToken.None, LocalHealthRunIntent.Action)).Code);
            Assert.Equal("queued", (await store.GetAsync(approval.Run.RunId, actorId, CancellationToken.None))!.Run.Status);
            Assert.Equal(2, await database.ExecuteScalarAsync<int>(
                "select count(*)::int from native_health_run_audit where run_id=@runId",
                new { runId = approval.Run.RunId }));
            Assert.Equal("claimed", (await store.ClaimAsync(approval.Run.RunId, actorId, CancellationToken.None)).Code);
            Assert.Equal(plan.Digest(), (await store.GetModelCodePlanAsync(approval.Run.RunId, CancellationToken.None))!.Digest());
            var result = new ModelCodeEditResult(ModelCodeEditPlan.Action, approval.Run.RunId, conversationId,
                "fixture", baseCommit, FilePath, "C:\\runs\\fixture", "hic-model-test", "+change",
                "passed:isolated-csharp-compile-and-scope", true, DateTime.UtcNow);
            Assert.True(await store.CompleteModelCodeAsync(approval.Run.RunId, actorId, result, CancellationToken.None));
            Assert.Equal("completed",
                (await store.GetAsync(approval.Run.RunId, actorId, CancellationToken.None))!.Run.Status);
            Assert.Equal(1, await database.ExecuteScalarAsync<int>(
                "select count(*)::int from native_health_run_audit where run_id=@runId and event_code='completed'",
                new { runId = approval.Run.RunId }));
            Assert.True(await store.ModelRunStillCurrentAsync(approval.Run.RunId, plan, CancellationToken.None));
            await database.ExecuteAsync("update tasks set updated_at_utc=updated_at_utc+interval '1 second' where id=@taskId",
                new { taskId });
            Assert.False(await store.ModelRunStillCurrentAsync(approval.Run.RunId, plan, CancellationToken.None));
            await Assert.ThrowsAsync<PostgresException>(() =>
                database.ExecuteAsync("update native_health_run_audit set event_code='queued'"));
        });
    }
}
