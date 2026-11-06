using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentWorker.Tests;

public class ExecutionSafetyTests
{
    [Theory]
    [InlineData("conflict")]
    [InlineData("permanent")]
    [InlineData("timeout")]
    [InlineData("network")]
    [InlineData("empty")]
    [InlineData("pending")]
    [InlineData("malformed")]
    [InlineData("wrong-id")]
    [InlineData("wrong-status")]
    [InlineData("server")]
    public async Task UnconfirmedExecuting_NeverCallsModelOrWritesFailed(string failure)
    {
        var id = Guid.NewGuid();
        var model = new CountingModel();
        var statuses = new List<string>();
        using var http = Client(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(new { id, title = "test", description = "test", status = "Approved" });
            statuses.Add(await Status(request, token));
            return failure switch
            {
                "conflict" => new(HttpStatusCode.Conflict),
                "permanent" => new(HttpStatusCode.Unauthorized),
                "timeout" => throw new TaskCanceledException("synthetic timeout"),
                "network" => throw new HttpRequestException("synthetic connection failure"),
                "empty" => new(HttpStatusCode.NoContent),
                "pending" => new(HttpStatusCode.Accepted) { Content = JsonContent.Create(new { id, status = "Executing" }) },
                "malformed" => new(HttpStatusCode.OK) { Content = new StringContent("{") },
                "wrong-id" => Json(new { id = Guid.NewGuid(), status = "Executing" }),
                "wrong-status" => Json(new { id, status = "Approved" }),
                "server" => new(HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException()
            };
        });
        var worker = new Worker(NullLogger<Worker>.Instance, model);
        Assert.Null(await worker.ProcessTaskAsync(http, id, CancellationToken.None));
        Assert.Equal(0, model.Calls);
        Assert.NotEmpty(statuses);
        Assert.All(statuses, x => Assert.Equal("Executing", x));
        Assert.Equal(failure == "server" ? 3 : 1, statuses.Count);
    }

    [Theory]
    [InlineData(200, 0)]
    [InlineData(409, 1)]
    [InlineData(400, 2)]
    [InlineData(503, 3)]
    public async Task StatusContract_DistinguishesOutcomes(int code, int outcome)
    {
        var id = Guid.NewGuid();
        using var http = Client((_, _) => Task.FromResult(code == 200
            ? Json(new { id, status = "Executing" })
            : new HttpResponseMessage((HttpStatusCode)code)));
        var worker = new Worker(NullLogger<Worker>.Instance, new CountingModel());
        var actual = await worker.UpdateStatusAsync(http, id, "Executing", 1, null, null, CancellationToken.None);
        Assert.Equal((StatusUpdateOutcome)outcome, actual);
    }

    [Fact]
    public async Task AcceptedExecuting_PreservesHappyPath()
    {
        var id = Guid.NewGuid();
        var model = new CountingModel();
        var statuses = new List<string>();
        using var http = Client(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(new { id, title = "test", description = "test", status = "Approved" });
            var text = await request.Content!.ReadAsStringAsync(token);
            using var body = JsonDocument.Parse(text);
            var status = body.RootElement.GetProperty("status").GetString()!;
            statuses.Add(status);
            Assert.Equal(status == "Executing" ? "Approved" : "Executing",
                body.RootElement.GetProperty("expectedStatus").GetString());
            if (status == "Completed")
            {
                Assert.Equal("HIC_RUNTIME_TEST_OK", body.RootElement.GetProperty("result").GetString());
                Assert.Equal(1, body.RootElement.GetProperty("attemptCount").GetInt32());
            }
            return Json(new { id, status });
        });
        var worker = new Worker(NullLogger<Worker>.Instance, model);
        Assert.Null(await worker.ProcessTaskAsync(http, id, CancellationToken.None));
        Assert.Equal(1, model.Calls);
        Assert.Equal(new[] { "Executing", "Completed" }, statuses);
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutModelOrFailedUpdate()
    {
        var id = Guid.NewGuid();
        var model = new CountingModel();
        using var cancellation = new CancellationTokenSource();
        var posts = 0;
        using var http = Client((request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Json(new { id, title = "test", description = "test", status = "Approved" }));
            posts++;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });
        var worker = new Worker(NullLogger<Worker>.Instance, model);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ProcessTaskAsync(http, id, cancellation.Token));
        Assert.Equal(0, model.Calls);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task UnconfirmedCompletion_DoesNotAttemptFailedTransition()
    {
        var id = Guid.NewGuid();
        var model = new CountingModel();
        var statuses = new List<string>();
        using var http = Client(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(new { id, title = "test", description = "test", status = "Approved" });
            var status = await Status(request, token);
            statuses.Add(status);
            if (status == "Completed")
                throw new HttpRequestException("synthetic lost completion response");
            return Json(new { id, status });
        });
        var worker = new Worker(NullLogger<Worker>.Instance, model);
        Assert.Null(await worker.ProcessTaskAsync(http, id, CancellationToken.None));
        Assert.Equal(1, model.Calls);
        Assert.Equal(new[] { "Executing", "Completed" }, statuses);
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static async Task<string> Status(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        return body.RootElement.GetProperty("status").GetString()!;
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
    }

    private sealed class CountingModel : IModelClient
    {
        public int Calls { get; private set; }
        public Task<ModelResult> GenerateAsync(string prompt, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ModelResult.Ok("HIC_RUNTIME_TEST_OK"));
        }
    }
}
