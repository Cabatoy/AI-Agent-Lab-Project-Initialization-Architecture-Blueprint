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
                requests.Add(await reader.ReadToEndAsync());
                if (block) { entered.TrySetResult(); await release.Task; }
                return Results.Json(new { choices = new[] { new { message = new { content = "A real fixture reply." } } } });
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
                    "Migrations", "007-general-conversations.sql")));
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
                Assert.Equal(beforeTasks, await db.ExecuteScalarAsync<int>("select count(*)::int from tasks"));
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

    private static Task<HttpResponseMessage> Post<T>(HttpClient client, string path, T body) =>
        client.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
}
