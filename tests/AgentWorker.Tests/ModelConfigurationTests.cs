using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentWorker.Tests;

public class ModelConfigurationTests
{
    [Theory]
    [InlineData("unknown-provider")]
    [InlineData("")]
    public async Task UnsupportedProvider_DisablesModelWithActionableMessage(string provider)
    {
        var client = Worker.CreateModelClient(
            new ModelOptions(provider, "http://127.0.0.1:1234", "test-model", null, 30),
            NullLogger.Instance);

        var result = await client.GenerateAsync("not sent", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.IsTransient);
        Assert.Contains("Unsupported MODEL_PROVIDER", result.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingEndpoint_DisablesModelWithActionableMessage(string? endpoint)
    {
        var client = Worker.CreateModelClient(
            new ModelOptions("openai-compatible", endpoint, "test-model", null, 30),
            NullLogger.Instance);

        var result = await client.GenerateAsync("not sent", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.IsTransient);
        Assert.Contains("MODEL_ENDPOINT and MODEL_NAME", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingModel_DisablesModelWithActionableMessage(string model)
    {
        var client = Worker.CreateModelClient(
            new ModelOptions("ollama", "http://127.0.0.1:1234", model, null, 30),
            NullLogger.Instance);

        var result = await client.GenerateAsync("not sent", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.IsTransient);
        Assert.Contains("MODEL_ENDPOINT and MODEL_NAME", result.ErrorMessage);
    }

    [Theory]
    [InlineData("podman-ai-lab")]
    [InlineData("ollama")]
    [InlineData("openai-compatible")]
    [InlineData("OPENAI-COMPATIBLE")]
    public void SupportedProvider_WithValidConfigurationRemainsEnabled(string provider)
    {
        var client = Worker.CreateModelClient(
            new ModelOptions(provider, "http://127.0.0.1:1234", "test-model", null, 30),
            NullLogger.Instance);

        Assert.IsType<PodmanAiLabModelClient>(client);
    }

    [Theory]
    [InlineData("not a URL")]
    [InlineData("file:///local/model")]
    [InlineData("https://user:private-value@127.0.0.1:1234")]
    [InlineData("http://user:private-value@127.0.0.1:1234")]
    public async Task InvalidEndpoint_DisablesModelWithoutEchoingConfiguration(string endpoint)
    {
        var client = Worker.CreateModelClient(
            new ModelOptions("ollama", endpoint, "test-model", null, 30),
            NullLogger.Instance);

        var result = await client.GenerateAsync("not sent", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.IsTransient);
        Assert.Contains("absolute HTTP(S) URL", result.ErrorMessage);
        Assert.DoesNotContain(endpoint, result.ErrorMessage);
        Assert.DoesNotContain("private-value", result.ErrorMessage);
    }

    [Fact]
    public async Task EndpointQuerySecret_FailsClosedWithoutEchoingConfiguration()
    {
        const string endpoint = "https://api.example.test/v1?api_key=private-value";
        var client = Worker.CreateModelClient(
            new ModelOptions("openai-compatible", endpoint, "test-model", null, 30),
            NullLogger.Instance);

        var result = await client.GenerateAsync("sensitive prompt", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("query", result.ErrorMessage);
        Assert.DoesNotContain(endpoint, result.ErrorMessage);
        Assert.DoesNotContain("private-value", result.ErrorMessage);
    }

    [Theory]
    [InlineData("http://api.example.test:1234")]
    [InlineData("http://192.0.2.10:1234")]
    public async Task RemoteHttpEndpoint_FailsClosed(string endpoint)
    {
        var client = Worker.CreateModelClient(
            new ModelOptions("openai-compatible", endpoint, "test-model", "test-token", 30),
            NullLogger.Instance);

        var result = await client.GenerateAsync("sensitive prompt", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("must use HTTPS", result.ErrorMessage);
        Assert.DoesNotContain(endpoint, result.ErrorMessage);
        Assert.DoesNotContain("test-token", result.ErrorMessage);
        Assert.DoesNotContain("sensitive prompt", result.ErrorMessage);
    }

    [Theory]
    [InlineData("http://127.0.0.1:1234")]
    [InlineData("http://localhost:1234")]
    [InlineData("http://ai-lab:1234")]
    [InlineData("http://host.containers.internal:11434")]
    public void TrustedLocalHttpEndpoint_RemainsEnabled(string endpoint)
    {
        var client = Worker.CreateModelClient(
            new ModelOptions("openai-compatible", endpoint, "test-model", null, 30),
            NullLogger.Instance);

        Assert.IsType<PodmanAiLabModelClient>(client);
    }

    [Fact]
    public void RemoteHttpsEndpoint_RemainsEnabled()
    {
        var client = Worker.CreateModelClient(
            new ModelOptions("openai-compatible", "https://api.example.test/v1", "test-model", null, 30),
            NullLogger.Instance);

        Assert.IsType<PodmanAiLabModelClient>(client);
    }

    [Fact]
    public async Task ProviderHttpError_DoesNotReturnRawResponseBody()
    {
        const string sentinel = "provider-secret-response";
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(sentinel)
            })));
        var client = new PodmanAiLabModelClient(
            new ModelOptions("ollama", "http://127.0.0.1:1234", "test-model", null, 30),
            httpClient);

        var result = await client.GenerateAsync("sensitive prompt", CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(sentinel, result.ErrorMessage);
        Assert.DoesNotContain("sensitive prompt", result.ErrorMessage);
    }

    [Fact]
    public async Task ProviderRequestException_DoesNotReturnRawExceptionMessage()
    {
        const string sentinel = "credential-from-transport-exception";
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
            throw new HttpRequestException(sentinel)));
        var client = new PodmanAiLabModelClient(
            new ModelOptions("ollama", "http://127.0.0.1:1234", "test-model", null, 30),
            httpClient);

        var result = await client.GenerateAsync("sensitive prompt", CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(sentinel, result.ErrorMessage);
        Assert.DoesNotContain("sensitive prompt", result.ErrorMessage);
    }

    [Fact]
    public async Task ProviderTransientFailure_DoesNotSendPromptToFallbackRoute()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("sensitive-provider-body")
            });
        }));
        var client = new PodmanAiLabModelClient(
            new ModelOptions("ollama", "http://127.0.0.1:1234", "test-model", null, 30), httpClient);
        var result = await client.GenerateAsync("sensitive prompt", CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(result.IsTransient);
        Assert.Equal("Provider request failed.", result.ErrorMessage);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task UnsupportedChatRoute_FallsBackToCompletionOnSameProvider()
    {
        var routes = new List<string>();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
        {
            routes.Add(request.RequestUri!.AbsolutePath);
            var response = request.RequestUri.AbsolutePath == "/v1/chat/completions"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"choices\":[{\"text\":\"local result\"}]}")
                };
            return Task.FromResult(response);
        }));
        var client = new PodmanAiLabModelClient(
            new ModelOptions("ollama", "http://127.0.0.1:1234", "test-model", null, 30), httpClient);

        var result = await client.GenerateAsync("safe test prompt", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("local result", result.Content);
        Assert.Equal(["/v1/chat/completions", "/v1/completions"], routes);
    }

    [Theory]
    [InlineData("invalid sensitive-provider-body")]
    [InlineData("{\"choices\":\"sensitive-provider-body\"}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":42}}]}")]
    public async Task MalformedSuccessfulResponse_IsSanitized(string body)
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }));
        var client = new PodmanAiLabModelClient(
            new ModelOptions("ollama", "http://127.0.0.1:1234", "test-model", null, 30), httpClient);
        var result = await client.GenerateAsync("sensitive prompt", CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.IsTransient);
        Assert.Equal("Provider request failed.", result.ErrorMessage);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task RealProviderRedirect_DoesNotForwardPromptToAnotherDestination()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = new List<string>();
            async Task Respond()
            {
                for (var i = 0; i < 1; i++)
                {
                    using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                    await using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    var firstLine = await reader.ReadLineAsync(timeout.Token);
                    received.Add(firstLine!);
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token))) { }
                    var response = System.Text.Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{port}/unapproved\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, timeout.Token);
                }
            }
            var server = Respond();
            var client = new PodmanAiLabModelClient(
                new ModelOptions("ollama", $"http://127.0.0.1:{port}", "test-model", null, 5));
            var result = await client.GenerateAsync("sensitive prompt", timeout.Token);
            await server;
            Assert.False(result.Success);
            Assert.Single(received);
            Assert.All(received, request => Assert.DoesNotContain("/unapproved", request));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task WorkerFailure_RedactsUntrustedProviderMessageBeforePersistence()
    {
        const string sentinel = "provider-token-and-prompt";
        var logger = new CapturingLogger<Worker>();
        var worker = new Worker(logger, new FixedFailureModel(sentinel));

        var execution = await worker.GenerateWithRetryAsync("sensitive prompt", CancellationToken.None);

        Assert.False(execution.Result.Success);
        Assert.Equal("Provider request failed.", execution.Result.ErrorMessage);
        Assert.DoesNotContain(sentinel, execution.Result.ErrorMessage);
        Assert.DoesNotContain("sensitive prompt", execution.Result.ErrorMessage);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(sentinel, message);
            Assert.DoesNotContain("sensitive prompt", message);
        });
    }

    [Fact]
    public async Task DisabledProviderRemainsExplicitlyDisabled()
    {
        var client = Worker.CreateModelClient(
            new ModelOptions("disabled", null, string.Empty, null, 30),
            NullLogger.Instance);

        var result = await client.GenerateAsync("not sent", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Model provider is disabled.", result.ErrorMessage);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class FixedFailureModel(string message) : IModelClient
    {
        public Task<ModelResult> GenerateAsync(string prompt, CancellationToken cancellationToken) =>
            Task.FromResult(ModelResult.Fail(message));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NullLogger<T>.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
