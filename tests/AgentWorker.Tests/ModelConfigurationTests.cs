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
        Assert.Contains("absolute HTTP or HTTPS URL", result.ErrorMessage);
        Assert.DoesNotContain(endpoint, result.ErrorMessage);
        Assert.DoesNotContain("private-value", result.ErrorMessage);
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
}
