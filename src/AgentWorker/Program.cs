using System.Collections.Concurrent;
using System.Net;
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
    private readonly string _deadLetterQueueKey;
    private readonly ModelOptions _modelOptions;
    private readonly IModelClient _modelClient;
    private readonly Guid _workerId = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, DateTime> _inFlightTasks = new();

    public Worker(ILogger<Worker> logger) : this(logger, null) { }

    internal Worker(ILogger<Worker> logger, IModelClient? modelClient)
    {
        _logger = logger;
        _controlPlaneUrl = Environment.GetEnvironmentVariable("CONTROL_PLANE_URL") ?? "http://localhost:8080";
        _redisConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Redis") ?? "localhost:6379";
        _queueKey = Environment.GetEnvironmentVariable("TASK_QUEUE_KEY") ?? "agentlab:task-queue";
        _deadLetterQueueKey = Environment.GetEnvironmentVariable("TASK_DEAD_LETTER_QUEUE_KEY") ?? "agentlab:task-queue:dead-letter";

        _modelOptions = new ModelOptions(
            Provider: Environment.GetEnvironmentVariable("MODEL_PROVIDER") ?? "disabled",
            Endpoint: Environment.GetEnvironmentVariable("MODEL_ENDPOINT"),
            Model: Environment.GetEnvironmentVariable("MODEL_NAME") ?? "",
            ApiKey: Environment.GetEnvironmentVariable("MODEL_API_KEY"),
            TimeoutSeconds: ParseTimeout(Environment.GetEnvironmentVariable("MODEL_TIMEOUT_SECONDS")));

        _modelClient = modelClient ?? CreateModelClient(_modelOptions, _logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = new HttpClient();
        using var redis = await ConnectRedisWithRetryAsync(_redisConnectionString, stoppingToken);
        var redisDb = redis.GetDatabase();

        _logger.LogInformation("Worker started. Queue={QueueKey}, ModelProvider={ModelProvider}", _queueKey, _modelOptions.Provider);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? currentTaskId = null;
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

                if (!_inFlightTasks.TryAdd(taskId, DateTime.UtcNow))
                {
                    _logger.LogWarning("Task is already being processed. Skipping duplicate dequeue. TaskId={TaskId}", taskId);
                    continue;
                }

                currentTaskId = taskId;
                try
                {
                    _logger.LogInformation("Task dequeued: {TaskId}", taskId);

                    var deadLetter = await ProcessTaskAsync(http, taskId, stoppingToken);
                    if (deadLetter is not null)
                        await EnqueueDeadLetterAsync(redisDb, taskId, deadLetter.Reason,
                            deadLetter.ErrorMessage, deadLetter.AttemptCount, deadLetter.Source);
                }
                finally
                {
                    _inFlightTasks.TryRemove(taskId, out _);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Worker loop error. TaskId={TaskId}, ErrorType={ErrorType}. No unconfirmed state mutation attempted.",
                    currentTaskId, ex.GetType().Name);

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    internal async Task<DeadLetterMessage?> ProcessTaskAsync(HttpClient http, Guid taskId, CancellationToken cancellationToken)
    {
        var task = await GetTaskAsync(http, taskId, cancellationToken);
        if (task is null || task.Id != taskId)
        {
            _logger.LogWarning("Task payload unavailable or mismatched. TaskId={TaskId}; execution skipped.", taskId);
            return null;
        }

        var claim = await ClaimExecutionAsync(http, taskId, Guid.NewGuid(), cancellationToken);
        if (claim.Outcome != StatusUpdateOutcome.Accepted || claim.Identity is null)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        var execution = await GenerateWithRetryAsync(BuildPrompt(task.Title, task.Description), cancellationToken);
        var target = execution.Result.Success ? TaskStatuses.Completed : TaskStatuses.Failed;
        var final = await UpdateStatusAsync(http, taskId, target, execution.AttemptCount,
            execution.Result.Content, execution.Result.ErrorMessage, cancellationToken, TaskStatuses.Executing, claim.Identity);
        if (final != StatusUpdateOutcome.Accepted)
            return null;
        if (execution.Result.Success)
        {
            _logger.LogInformation("Task processed: {TaskId}", taskId);
            return null;
        }
        return new DeadLetterMessage(taskId,
            execution.Result.IsTransient ? "model-transient-failure-retry-exhausted" : "model-permanent-failure",
            execution.Result.ErrorMessage, execution.AttemptCount, DateTime.UtcNow, "Worker.GenerateWithRetryAsync");
    }

    internal async Task<ExecutionClaimResult> ClaimExecutionAsync(HttpClient http, Guid taskId,
        Guid executionId, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { executionId, workerId = _workerId }),
                Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"{_controlPlaneUrl}/tasks/{taskId}/executions/claim",
                content, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var outcome = response.StatusCode == HttpStatusCode.Conflict
                    ? StatusUpdateOutcome.StateConflict
                    : response.IsSuccessStatusCode || IsTransientStatusCode(response.StatusCode)
                        ? StatusUpdateOutcome.TransientOrUnknown : StatusUpdateOutcome.PermanentFailure;
                _logger.LogWarning("Execution claim not accepted. TaskId={TaskId}, Outcome={Outcome}, Code={Code}",
                    taskId, outcome, (int)response.StatusCode);
                return new(outcome);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var acknowledgement = await JsonSerializer.DeserializeAsync<ClaimAcknowledgement>(
                stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
            if (acknowledgement is not null && acknowledgement.Id == taskId &&
                acknowledgement.ExecutionId == executionId && acknowledgement.WorkerId == _workerId &&
                acknowledgement.Generation > 0 && acknowledgement.NewlyClaimed &&
                acknowledgement.Status == TaskStatuses.Executing)
                return new(StatusUpdateOutcome.Accepted,
                    new(executionId, _workerId, acknowledgement.Generation));
            _logger.LogWarning("Execution claim unconfirmed or replayed. TaskId={TaskId}; model execution denied.", taskId);
            return new(StatusUpdateOutcome.TransientOrUnknown);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException or IOException)
        {
            _logger.LogWarning("Execution claim outcome unknown. TaskId={TaskId}, ErrorType={ErrorType}; no automatic retry.",
                taskId, ex.GetType().Name);
            return new(StatusUpdateOutcome.TransientOrUnknown);
        }
    }

    private async Task<ConnectionMultiplexer> ConnectRedisWithRetryAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 5;
        options.ConnectTimeout = 5000;

        const int maxAttempts = 10;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await ConnectionMultiplexer.ConnectAsync(options);
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "Worker Redis connection attempt {Attempt}/{MaxAttempts} failed.", attempt, maxAttempts);
                if (attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, attempt)), cancellationToken);
                }
            }
        }

        throw new InvalidOperationException("Worker could not connect to Redis after retries.", lastError);
    }

    internal async Task<ModelExecutionResult> GenerateWithRetryAsync(string prompt, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        ModelResult? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ModelResult result;
            try
            {
                result = await _modelClient.GenerateAsync(prompt, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var transient = IsTransientException(ex);
                result = ModelResult.Fail("Provider request failed.", transient);
            }

            if (result.Success)
            {
                return new ModelExecutionResult(result, attempt);
            }

            if (_modelClient is not DisabledModelClient)
                result = ModelResult.Fail("Provider request failed.", result.IsTransient);

            lastFailure = result;
            _logger.LogWarning("Model attempt {Attempt}/{MaxAttempts} failed. Transient={Transient}.", attempt, maxAttempts, result.IsTransient);

            if (!result.IsTransient)
            {
                break;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }

        return new ModelExecutionResult(lastFailure ?? ModelResult.Fail("Model call failed."), maxAttempts);
    }

    internal async Task<StatusUpdateOutcome> UpdateStatusAsync(HttpClient http, Guid taskId, string status, int attemptCount, string? result, string? errorMessage, CancellationToken cancellationToken, string? expectedStatus = null, WorkerExecutionIdentity? execution = null)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    status,
                    attemptCount,
                    result,
                    errorMessage,
                    expectedStatus,
                    execution
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync($"{_controlPlaneUrl}/tasks/{taskId}/status", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        _logger.LogWarning("Unexpected status acknowledgement code. TaskId={TaskId}, Status={Status}, Code={Code}",
                            taskId, status, (int)response.StatusCode);
                        return StatusUpdateOutcome.TransientOrUnknown;
                    }
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var acknowledgement = await JsonSerializer.DeserializeAsync<StatusAcknowledgement>(
                        stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
                    if (acknowledgement?.Id == taskId && acknowledgement.Status == status &&
                        acknowledgement.Execution == execution)
                        return StatusUpdateOutcome.Accepted;
                    _logger.LogWarning("Status acknowledgement missing or mismatched. TaskId={TaskId}, Status={Status}", taskId, status);
                    return StatusUpdateOutcome.TransientOrUnknown;
                }

                var transient = IsTransientStatusCode(response.StatusCode);
                _logger.LogWarning("Status update failed. Attempt={Attempt}/{MaxAttempts}, Task={TaskId}, Status={Status}, Code={Code}, Transient={Transient}",
                    attempt, maxAttempts, taskId, status, (int)response.StatusCode, transient);

                if (!transient || attempt == maxAttempts)
                {
                    return response.StatusCode == HttpStatusCode.Conflict
                        ? StatusUpdateOutcome.StateConflict
                        : transient ? StatusUpdateOutcome.TransientOrUnknown : StatusUpdateOutcome.PermanentFailure;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException or IOException)
            {
                // A missing acknowledgement may follow a committed transaction; do not retry it as a new transition.
                _logger.LogWarning("Status update unconfirmed. TaskId={TaskId}, Status={Status}, ErrorType={ErrorType}",
                    taskId, status, ex.GetType().Name);
                return StatusUpdateOutcome.TransientOrUnknown;
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
        }
        throw new InvalidOperationException("Status retry loop completed without an outcome.");
    }

    private async Task<TaskPayload?> GetTaskAsync(HttpClient http, Guid taskId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{_controlPlaneUrl}/tasks/{taskId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<TaskPayload>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken);
    }

    private async Task EnqueueDeadLetterAsync(
        IDatabase redisDb,
        Guid taskId,
        string reason,
        string? errorMessage,
        int attemptCount,
        string source)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new DeadLetterMessage(
                TaskId: taskId,
                Reason: reason,
                ErrorMessage: errorMessage,
                AttemptCount: attemptCount,
                CreatedAtUtc: DateTime.UtcNow,
                Source: source));

            await redisDb.ListRightPushAsync(_deadLetterQueueKey, payload);
            _logger.LogWarning("Task moved to dead-letter queue. TaskId={TaskId}, Queue={Queue}, Reason={Reason}", taskId, _deadLetterQueueKey, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue dead-letter message. TaskId={TaskId}, Queue={Queue}", taskId, _deadLetterQueueKey);
        }
    }

    private static string BuildPrompt(string title, string description)
    {
        return $"""
You are an execution assistant.
Use the task title and description as the source of truth.
Return only the requested final deliverable. Do not include analysis, internal reasoning, or a restatement of the request.
Keep the answer concise but complete, using bullet points unless the task requests another format.
Task title: {title}
Task description: {description}
""";
    }

    internal static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout
               || statusCode == (HttpStatusCode)429
               || numeric >= 500;
    }

    internal static bool IsTransientException(Exception exception)
    {
        return exception is HttpRequestException
            or TaskCanceledException
            or TimeoutException;
    }

    private static int ParseTimeout(string? raw)
    {
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 30;
    }

    internal static IModelClient CreateModelClient(ModelOptions options, ILogger logger)
    {
        if (string.Equals(options.Provider, "disabled", StringComparison.OrdinalIgnoreCase))
            return new DisabledModelClient();

        if (string.Equals(options.Provider, "podman-ai-lab", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Provider, "ollama", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Provider, "openai-compatible", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
            {
                logger.LogWarning("A supported model provider is missing MODEL_ENDPOINT or MODEL_NAME. Model calls are disabled.");
                return new DisabledModelClient("Model provider configuration is incomplete. Set MODEL_ENDPOINT and MODEL_NAME.");
            }

            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(endpoint.UserInfo) ||
                !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment))
            {
                logger.LogWarning("MODEL_ENDPOINT must be an absolute HTTP(S) URL without embedded credentials, query, or fragment. Model calls are disabled.");
                return new DisabledModelClient("MODEL_ENDPOINT must be an absolute HTTP(S) URL without embedded credentials, query, or fragment.");
            }

            if (endpoint.Scheme == "http" && !IsTrustedLocalEndpoint(endpoint))
            {
                logger.LogWarning("Remote model providers must use HTTPS. Model calls are disabled.");
                return new DisabledModelClient("Remote model providers must use HTTPS.");
            }

            return new PodmanAiLabModelClient(options);
        }

        logger.LogWarning("Unsupported MODEL_PROVIDER. Model calls are disabled.");
        return new DisabledModelClient("Unsupported MODEL_PROVIDER. Supported values are disabled, podman-ai-lab, ollama, and openai-compatible.");
    }

    private static bool IsTrustedLocalEndpoint(Uri endpoint) =>
        endpoint.IsLoopback ||
        string.Equals(endpoint.Host, "ai-lab", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(endpoint.Host, "host.containers.internal", StringComparison.OrdinalIgnoreCase);
}

internal interface IModelClient
{
    Task<ModelResult> GenerateAsync(string prompt, CancellationToken cancellationToken);
}

internal sealed class DisabledModelClient : IModelClient
{
    private readonly string _reason;

    public DisabledModelClient(string reason = "Model provider is disabled.")
    {
        _reason = reason;
    }

    public Task<ModelResult> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        return Task.FromResult(ModelResult.Fail(_reason));
    }
}

internal sealed class PodmanAiLabModelClient : IModelClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public PodmanAiLabModelClient(ModelOptions options)
        : this(options, new HttpClient
        {
            BaseAddress = new Uri(options.Endpoint!, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        })
    {
    }

    internal PodmanAiLabModelClient(ModelOptions options, HttpClient httpClient)
    {
        _model = options.Model;
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri(options.Endpoint!, UriKind.Absolute);
        _httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    public async Task<ModelResult> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        var chatPayload = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            temperature = 0.2,
            stream = false,
            max_tokens = 1024
        });

        var chatResult = await SendAndParseAsync("/v1/chat/completions", chatPayload, cancellationToken);
        if (chatResult.Success)
        {
            return chatResult;
        }

        var completionPayload = JsonSerializer.Serialize(new
        {
            model = _model,
            prompt,
            temperature = 0.2,
            stream = false,
            max_tokens = 1024
        });

        var completionResult = await SendAndParseAsync("/v1/completions", completionPayload, cancellationToken);
        if (completionResult.Success)
        {
            return completionResult;
        }

        return ModelResult.Fail($"Chat fallback failed: {chatResult.ErrorMessage} | Completion fallback failed: {completionResult.ErrorMessage}");
    }

    private async Task<ModelResult> SendAndParseAsync(string route, string jsonPayload, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(route, request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var transient = IsTransientStatusCodeLocal(response.StatusCode);
                return ModelResult.Fail($"Provider returned HTTP {(int)response.StatusCode}.", transient);
            }

            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return ModelResult.Fail($"{route} returned no choices.");
            }

            var content = ExtractContent(choices[0]);
            return string.IsNullOrWhiteSpace(content)
                ? ModelResult.Fail($"{route} returned empty content.")
                : ModelResult.Ok(content.Trim());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return ModelResult.Fail("Provider request failed.", true);
        }
    }

    private static bool IsTransientStatusCodeLocal(HttpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout
               || statusCode == (HttpStatusCode)429
               || numeric >= 500;
    }

    private static string? ExtractContent(JsonElement firstChoice)
    {
        if (firstChoice.TryGetProperty("text", out var textElement))
        {
            var text = textElement.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return ExtractFinalChannel(text) ?? text;
            }
        }

        if (firstChoice.TryGetProperty("message", out var messageElement)
            && messageElement.TryGetProperty("content", out var messageContentElement))
        {
            var messageContent = messageContentElement.GetString();
            if (!string.IsNullOrWhiteSpace(messageContent))
            {
                return ExtractFinalChannel(messageContent) ?? messageContent;
            }
        }

        return null;
    }

    private static string? ExtractFinalChannel(string content)
    {
        const string finalMarker = "<|channel|>final<|message|>";
        var finalMarkerIndex = content.LastIndexOf(finalMarker, StringComparison.Ordinal);
        if (finalMarkerIndex < 0)
        {
            return null;
        }

        var finalStart = finalMarkerIndex + finalMarker.Length;
        var endMarkerIndex = content.IndexOf("<|end|>", finalStart, StringComparison.Ordinal);
        var finalContent = endMarkerIndex < 0
            ? content[finalStart..]
            : content[finalStart..endMarkerIndex];

        return finalContent.Trim();
    }
}

internal sealed record ModelOptions(
    string Provider,
    string? Endpoint,
    string Model,
    string? ApiKey,
    int TimeoutSeconds);

internal sealed record ModelResult(bool Success, string? Content, string? ErrorMessage, bool IsTransient)
{
    public static ModelResult Ok(string content) => new(true, content, null, false);
    public static ModelResult Fail(string error, bool isTransient = false) => new(false, null, error, isTransient);
}

internal sealed record TaskPayload(Guid Id, string Title, string Description, string Status);

internal sealed record DeadLetterMessage(
    Guid TaskId,
    string Reason,
    string? ErrorMessage,
    int AttemptCount,
    DateTime CreatedAtUtc,
    string Source);

internal static class TaskStatuses
{
    public const string Executing = "Executing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

internal sealed record ModelExecutionResult(ModelResult Result, int AttemptCount);

internal enum StatusUpdateOutcome { Accepted, StateConflict, PermanentFailure, TransientOrUnknown }

internal sealed record WorkerExecutionIdentity(Guid ExecutionId, Guid WorkerId, long Generation);
internal sealed record ExecutionClaimResult(StatusUpdateOutcome Outcome, WorkerExecutionIdentity? Identity = null);
internal sealed record ClaimAcknowledgement(Guid Id, string Status, Guid ExecutionId, Guid WorkerId,
    long Generation, bool NewlyClaimed);
internal sealed record StatusAcknowledgement(Guid Id, string Status, WorkerExecutionIdentity? Execution = null);
