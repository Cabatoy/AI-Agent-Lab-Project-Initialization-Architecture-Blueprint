using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using ControlPlane.Api.Tests;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class GeneralConversationTests
{
    [PostgresFact]
    public async Task GeneralConversations_AreActorScopedDurableAndNeverCreateTasks()
    {
        await GitEditRunnerTests.Database(async (cs, db, _) =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var modelBuilder = WebApplication.CreateBuilder();
            modelBuilder.Logging.ClearProviders();
            modelBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var modelApp = modelBuilder.Build();
            var requests = new List<string>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = false;
            modelApp.MapPost("/v1/chat/completions", async (HttpRequest request) =>
            {
                using var reader = new StreamReader(request.Body);
                var json = await reader.ReadToEndAsync();
                requests.Add(json);
                if (block) { entered.TrySetResult(); await release.Task; }
                var answer = json.Contains("response_format", StringComparison.Ordinal)
                    ? """{"tasks":[{"title":"Design","description":"Review the design."},{"title":"Test","description":"Define tests."}]}"""
                    : "A real fixture reply.";
                return Results.Json(new { choices = new[] { new { message = new { content = answer } } } });
            });
            await modelApp.StartAsync();
            var modelAddress = modelApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MODEL_PROVIDER"] = "podman-ai-lab", ["MODEL_ENDPOINT"] = modelAddress, ["MODEL_NAME"] = "fixture"
            });
            await using var app = builder.Build();
            var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var actor = Guid.NewGuid();
            var sessions = LocalOperatorSessions.Create(bootstrap, actor.ToString())!;
            var token = sessions.IssueSessionToken(bootstrap)!;
            GeneralConversationEndpoints.Map(app, cs, sessions);
            GeneralPlanEndpoints.Map(app, cs, sessions);
            await app.StartAsync();
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/local-runner/general-conversations")).StatusCode);
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/local-runner/general-conversations")).StatusCode);
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "HumanInControl.sln"))) root = root.Parent;
                Assert.NotNull(root);
                await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "ControlPlane",
                    "Migrations", "007-github-publisher.sql")));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/local-runner/general-conversations")).StatusCode);
                await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "ControlPlane",
                    "Migrations", "008-general-conversations.sql")));
                await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "ControlPlane",
                    "Migrations", "009-general-plan-review.sql")));
                await db.ExecuteAsync(@"alter table tasks add column attempt_count integer not null default 0,
                    add column result text, add column error_message text, add column created_at_utc timestamptz not null default now()");
                var beforeTasks = await db.ExecuteScalarAsync<int>("select count(*)::int from tasks");
                var created = await Post(client, "/local-runner/general-conversations", new { title = "General" });
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                var conversation = await created.Content.ReadFromJsonAsync<GeneralConversation>();
                Assert.NotNull(conversation);
                var id = conversation.ConversationId;
                var turn = Guid.NewGuid();
                var message = new { clientMessageId = turn, content = "Discuss architecture." };
                var sent = await Post(client, $"/local-runner/general-conversations/{id}/messages", message);
                Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
                var reopened = await client.GetFromJsonAsync<GeneralConversation>($"/local-runner/general-conversations/{id}");
                Assert.Equal(2, reopened!.Messages.Count);
                Assert.Equal("A real fixture reply.", reopened.Messages[1].Content);
                Assert.Equal(HttpStatusCode.OK, (await Post(client, $"/local-runner/general-conversations/{id}/messages", message)).StatusCode);
                Assert.Single(requests);
                Assert.Equal(HttpStatusCode.Conflict, (await Post(client, $"/local-runner/general-conversations/{id}/messages",
                    new { clientMessageId = turn, content = "Different" })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await Post(client, $"/local-runner/general-conversations/{id}/messages",
                    new { clientMessageId = Guid.NewGuid(), content = "Continue." })).StatusCode);
                using var prompt = JsonDocument.Parse(requests[1]);
                Assert.Contains(prompt.RootElement.GetProperty("messages").EnumerateArray(),
                    item => item.GetProperty("content").GetString() == "Discuss architecture.");
                Assert.DoesNotContain("Repository source", requests[1]);
                block = true;
                var first = Post(client, $"/local-runner/general-conversations/{id}/messages",
                    new { clientMessageId = Guid.NewGuid(), content = "First concurrent message" });
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var second = await Post(client, $"/local-runner/general-conversations/{id}/messages",
                        new { clientMessageId = Guid.NewGuid(), content = "Second concurrent message" });
                    Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
                }
                finally { release.TrySetResult(); }
                Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
                block = false;
                Assert.Equal(beforeTasks, await db.ExecuteScalarAsync<int>("select count(*)::int from tasks"));
                var generated = await Post(client, $"/local-runner/general-conversations/{id}/plan/generate",
                    new { expectedRevision = 0 });
                Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
                var plan = await generated.Content.ReadFromJsonAsync<GeneralPlan>();
                Assert.Equal(1, plan!.Revision);
                Assert.False(plan.Approved);
                Assert.Equal(beforeTasks, await db.ExecuteScalarAsync<int>("select count(*)::int from tasks"));
                var rejected = await Post(client, $"/local-runner/general-conversations/{id}/plan/reject",
                    new { expectedRevision = 1 });
                Assert.True((await rejected.Content.ReadFromJsonAsync<GeneralPlan>())!.Rejected);
                Assert.Equal(HttpStatusCode.Conflict, (await Post(client,
                    $"/local-runner/general-conversations/{id}/plan/approve", new { expectedRevision = 1 })).StatusCode);
                Assert.Equal(beforeTasks, await db.ExecuteScalarAsync<int>("select count(*)::int from tasks"));
                var revised = await Post(client, $"/local-runner/general-conversations/{id}/plan/revise",
                    new { expectedRevision = 1, tasks = new[] { new { title = "Reviewed", description = "Human edited." } } });
                Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
                Assert.Equal(2, (await revised.Content.ReadFromJsonAsync<GeneralPlan>())!.Revision);
                Assert.Equal(HttpStatusCode.Conflict, (await Post(client,
                    $"/local-runner/general-conversations/{id}/plan/approve", new { expectedRevision = 1 })).StatusCode);
                await db.ExecuteAsync("alter table tasks add constraint reject_reviewed_fixture check (title <> 'Reviewed')");
                Assert.Equal(HttpStatusCode.InternalServerError, (await Post(client,
                    $"/local-runner/general-conversations/{id}/plan/approve", new { expectedRevision = 2 })).StatusCode);
                Assert.Equal(0, await db.ExecuteScalarAsync<int>("select count(*)::int from hic_general_plan_approvals"));
                Assert.Equal(beforeTasks, await db.ExecuteScalarAsync<int>("select count(*)::int from tasks"));
                await db.ExecuteAsync("alter table tasks drop constraint reject_reviewed_fixture");
                var approved = await Post(client, $"/local-runner/general-conversations/{id}/plan/approve", new { expectedRevision = 2 });
                Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
                var final = await approved.Content.ReadFromJsonAsync<GeneralPlan>();
                Assert.True(final!.Approved);
                Assert.Single(final.TaskIds);
                Assert.Equal("Draft", await db.ExecuteScalarAsync<string>("select status from tasks where id=@id", new { id = final.TaskIds[0] }));
                Assert.Equal(beforeTasks + 1, await db.ExecuteScalarAsync<int>("select count(*)::int from tasks"));
                Assert.Equal(HttpStatusCode.OK, (await Post(client,
                    $"/local-runner/general-conversations/{id}/plan/approve", new { expectedRevision = 2 })).StatusCode);
                Assert.Equal(beforeTasks + 1, await db.ExecuteScalarAsync<int>("select count(*)::int from tasks"));
                Assert.Equal(HttpStatusCode.Conflict, (await Post(client,
                    $"/local-runner/general-conversations/{id}/plan/generate", new { expectedRevision = 2 })).StatusCode);
                var other = Guid.NewGuid();
                await db.ExecuteAsync(@"insert into hic_general_conversations
                    values(@id,@actor,'Other',now(),now())", new { id = other, actor = Guid.NewGuid() });
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/local-runner/general-conversations/{other}")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await Post(client, $"/local-runner/general-conversations/{other}/messages",
                    new { clientMessageId = Guid.NewGuid(), content = "Forbidden" })).StatusCode);
            }
            finally { await app.StopAsync(); await modelApp.StopAsync(); }
        });
    }

    [Theory]
    [InlineData("""{"tasks":[{"title":"x","description":"y","command":"whoami"}]}""")]
    [InlineData("""{"tasks":[] }""")]
    [InlineData("""{"tasks":[{"title":"x","description":"y"}],"extra":true}""")]
    [InlineData("invalid")]
    public void PlanParser_RejectsUntrustedShapes(string json) =>
        Assert.False(GeneralPlanEndpoints.TryParseTasks(json, out _));

    private static Task<HttpResponseMessage> Post<T>(HttpClient client, string path, T body) =>
        client.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
}
