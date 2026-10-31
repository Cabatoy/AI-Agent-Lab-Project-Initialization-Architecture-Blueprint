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

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _controlPlaneUrl = Environment.GetEnvironmentVariable("CONTROL_PLANE_URL") ?? "http://localhost:8080";
        _redisConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Redis") ?? "localhost:6379";
        _queueKey = Environment.GetEnvironmentVariable("TASK_QUEUE_KEY") ?? "agentlab:task-queue";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = new HttpClient();
        using var redis = await ConnectionMultiplexer.ConnectAsync(_redisConnectionString);
        var redisDb = redis.GetDatabase();

        _logger.LogInformation("Worker started. Queue={QueueKey}", _queueKey);

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

                await UpdateStatusAsync(http, taskId, "Executing", stoppingToken);

                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

                await UpdateStatusAsync(http, taskId, "Completed", stoppingToken);
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

    private async Task UpdateStatusAsync(HttpClient http, Guid taskId, string status, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { status });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await http.PostAsync($"{_controlPlaneUrl}/tasks/{taskId}/status", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Status update failed. Task={TaskId}, Status={Status}, Code={Code}", taskId, status, (int)response.StatusCode);
        }
    }
}
