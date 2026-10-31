using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

internal sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly string _controlPlaneUrl;
    private readonly string _redisConnectionString;
    private readonly string _queueKey;
    private readonly ModelOptions _modelOptions;
    private readonly IModelClient _modelClient;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _controlPlaneUrl = Environment.GetEnvironmentVariable("CONTROL_PLANE_URL") ?? "http://localhost:8080";
        _redisConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Redis") ?? "localhost:6379";
        _queueKey = Environment.GetEnvironmentVariable("TASK_QUEUE_KEY") ?? "agentlab:task-queue";

        _modelOptions = new ModelOptions(
            Provider: Environment.GetEnvironmentVariable("MODEL_PROVIDER") ?? "disabled",
            Endpoint: Environment.GetEnvironmentVariable("MODEL_ENDPOINT"),
            Model: Environment.GetEnvironmentVariable("MODEL_NAME") ?? "",
            ApiKey: Environment.GetEnvironmentVariable("MODEL_API_KEY"),
            TimeoutSeconds: ParseTimeout(Environment.GetEnvironmentVariable("MODEL_TIMEOUT_SECONDS")));

        _modelClient = CreateModelClient(_modelOptions, _logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = new HttpClient();
        using var redis = await ConnectionMultiplexer.ConnectAsync(_redisConnectionString);
        var redisDb = redis.GetDatabase();

        _logger.LogInformation("Worker started. Queue={QueueKey}, ModelProvider={ModelProvider}", _queueKey, _modelOptions.Provider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var taskIdValue = await redisDb.ListLeftPopAsync(_queueKey);
                if (taskIdValue.IsNullOrEmpty)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                if (!Guid.TryParse(taskIdValue.ToString(), out var taskId))
                {
                    _logger.LogWarning("Invalid task id in queue: {TaskIdValue}", taskIdValue.ToString());
                    continue;
                }

                await UpdateStatusAsync(http, taskId, TaskStatuses.Executing, 1, null, null, stoppingToken);

                var taskPayload = await GetTaskAsync(http, taskId, stoppingToken);
                if (taskPayload is null)
                {
                    await UpdateStatusAsync(http, taskId, TaskStatuses.Failed, 1, null, "Task payload could not be loaded.", stoppingToken);
                    continue;
                }

                var prompt = BuildPrompt(taskPayload.Title, taskPayload.Description);
                var modelResult = await _modelClient.GenerateAsync(prompt, stoppingToken);

                if (!modelResult.Success)
                {
                    await UpdateStatusAsync(http, taskId, TaskStatuses.Failed, 1, null, modelResult.ErrorMessage ?? "Model call failed.", stoppingToken);
                    continue;
                }

                await UpdateStatusAsync(http, taskId, TaskStatuses.Completed, 1, modelResult.Content, null, stoppingToken);
                _logger.LogInformation("Task processed: {TaskId}", taskId);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Worker loop error");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task UpdateStatusAsync(HttpClient http, Guid taskId, string status, int attemptCount, string? result, string? errorMessage, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            status,
            attemptCount,
            result,
            errorMessage
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await http.PostAsync($"{_controlPlaneUrl}/tasks/{taskId}/status", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Status update failed. Task={TaskId}, Status={Status}, Code={Code}", taskId, status, (int)response.StatusCode);
        }
    }

    private async Task<TaskPayload?> GetTaskAsync(HttpClient http, Guid taskId, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync($"{_controlPlaneUrl}/tasks/{taskId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<TaskPayload>(stream, cancellationToken: cancellationToken);
    }

    private static string BuildPrompt(string title, string description)
    {
        return $"""
You are an execution assistant.
Generate a short implementation plan for the following task.
Title: {title}
Description: {description}
Return concise bullet points.
""";
    }

    private static int ParseTimeout(string? raw)
    {
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 30;
    }

    private static IModelClient CreateModelClient(ModelOptions options, ILogger logger)
    {
        if (string.Equals(options.Provider, "podman-ai-lab", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
            {
                logger.LogWarning("MODEL_PROVIDER is podman-ai-lab but MODEL_ENDPOINT or MODEL_NAME is missing. Falling back to disabled model client.");
                return new DisabledModelClient();
            }

            return new PodmanAiLabModelClient(options);
        }

        return new DisabledModelClient();
    }
}

internal interface IModelClient
{
    Task<ModelResult> GenerateAsync(string prompt, CancellationToken cancellationToken);
}

internal sealed class DisabledModelClient : IModelClient
{
    public Task<ModelResult> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        return Task.FromResult(ModelResult.Fail("Model provider is disabled."));
    }
}

internal sealed class PodmanAiLabModelClient : IModelClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public PodmanAiLabModelClient(ModelOptions options)
    {
        _model = options.Model;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.Endpoint!, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    public async Task<ModelResult> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            temperature = 0.2
        });

        using var request = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/v1/chat/completions", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ModelResult.Fail($"Model endpoint failed with {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return ModelResult.Fail("Model response has no choices.");
        }

        var content = choices[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(content)
            ? ModelResult.Fail("Model returned empty content.")
            : ModelResult.Ok(content);
    }
}

internal sealed record ModelOptions(
    string Provider,
    string? Endpoint,
    string Model,
    string? ApiKey,
    int TimeoutSeconds);

internal sealed record ModelResult(bool Success, string? Content, string? ErrorMessage)
{
    public static ModelResult Ok(string content) => new(true, content, null);
    public static ModelResult Fail(string error) => new(false, null, error);
}

internal sealed record TaskPayload(Guid Id, string Title, string Description, string Status);

internal static class TaskStatuses
{
    public const string Executing = "Executing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
