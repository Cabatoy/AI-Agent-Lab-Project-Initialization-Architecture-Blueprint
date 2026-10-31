using Dapper;
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=agentlab;Username=agentlab;Password=agentlab";
var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";

var app = builder.Build();

var redis = await ConnectRedisWithRetryAsync(redisConnectionString, app.Logger);
var redisDb = redis.GetDatabase();
const string queueKey = "agentlab:task-queue";

await InitializeSchemaWithRetryAsync(postgresConnectionString, app.Logger);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "control-plane" }));

app.MapGet("/tasks", async () =>
{
    await using var connection = new NpgsqlConnection(postgresConnectionString);
    var tasks = await connection.QueryAsync<AgentTask>(@"
        select id, title, description, status,
               attempt_count as AttemptCount,
               result as Result,
               error_message as ErrorMessage,
               created_at_utc as CreatedAtUtc,
               updated_at_utc as UpdatedAtUtc
        from tasks
        order by created_at_utc desc;");

    return Results.Ok(tasks);
});

app.MapPost("/tasks", async (CreateTaskRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "Title is required." });
    }

    var task = new AgentTask(
        Id: Guid.NewGuid(),
        Title: request.Title.Trim(),
        Description: request.Description?.Trim() ?? string.Empty,
        Status: TaskStatuses.Draft,
        AttemptCount: 0,
        Result: null,
        ErrorMessage: null,
        CreatedAtUtc: DateTime.UtcNow,
        UpdatedAtUtc: DateTime.UtcNow);

    await using var connection = new NpgsqlConnection(postgresConnectionString);
    await connection.ExecuteAsync(@"
        insert into tasks (id, title, description, status, attempt_count, result, error_message, created_at_utc, updated_at_utc)
        values (@Id, @Title, @Description, @Status, @AttemptCount, @Result, @ErrorMessage, @CreatedAtUtc, @UpdatedAtUtc);", task);

    return Results.Created($"/tasks/{task.Id}", task);
});

app.MapGet("/tasks/{id:guid}", async (Guid id) =>
{
    await using var connection = new NpgsqlConnection(postgresConnectionString);
    var task = await connection.QuerySingleOrDefaultAsync<AgentTask>(@"
        select id, title, description, status,
               attempt_count as AttemptCount,
               result as Result,
               error_message as ErrorMessage,
               created_at_utc as CreatedAtUtc,
               updated_at_utc as UpdatedAtUtc
        from tasks
        where id = @Id;", new { Id = id });

    return task is null ? Results.NotFound() : Results.Ok(task);
});

app.MapPost("/tasks/{id:guid}/approve", async (Guid id) =>
{
    await using var connection = new NpgsqlConnection(postgresConnectionString);

    var currentStatus = await connection.QuerySingleOrDefaultAsync<string>("select status from tasks where id = @Id;", new { Id = id });
    if (currentStatus is null)
    {
        return Results.NotFound();
    }

    if (!string.Equals(currentStatus, TaskStatuses.Draft, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Only Draft tasks can be approved." });
    }

    await connection.ExecuteAsync(@"
        update tasks
        set status = @Status,
            error_message = null,
            result = null,
            updated_at_utc = @UpdatedAtUtc
        where id = @Id;", new { Id = id, Status = TaskStatuses.Approved, UpdatedAtUtc = DateTime.UtcNow });

    await redisDb.ListRightPushAsync(queueKey, id.ToString());

    return Results.Ok(new { id, status = TaskStatuses.Approved });
});

app.MapPost("/tasks/{id:guid}/retry", async (Guid id) =>
{
    await using var connection = new NpgsqlConnection(postgresConnectionString);

    var currentStatus = await connection.QuerySingleOrDefaultAsync<string>("select status from tasks where id = @Id;", new { Id = id });
    if (currentStatus is null)
    {
        return Results.NotFound();
    }

    if (!string.Equals(currentStatus, TaskStatuses.Failed, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Only Failed tasks can be retried manually." });
    }

    await connection.ExecuteAsync(@"
        update tasks
        set status = @Status,
            updated_at_utc = @UpdatedAtUtc
        where id = @Id;", new { Id = id, Status = TaskStatuses.Approved, UpdatedAtUtc = DateTime.UtcNow });

    await redisDb.ListRightPushAsync(queueKey, id.ToString());

    return Results.Ok(new { id, status = TaskStatuses.Approved });
});

app.MapPost("/tasks/{id:guid}/status", async (Guid id, UpdateTaskStatusRequest request) =>
{
    var allowedStatuses = new[]
    {
        TaskStatuses.Approved,
        TaskStatuses.Retrying,
        TaskStatuses.Executing,
        TaskStatuses.Completed,
        TaskStatuses.Failed
    };

    if (!allowedStatuses.Contains(request.Status, StringComparer.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Unsupported status." });
    }

    await using var connection = new NpgsqlConnection(postgresConnectionString);
    var exists = await connection.QuerySingleOrDefaultAsync<int?>("select 1 from tasks where id = @Id;", new { Id = id });
    if (exists is null)
    {
        return Results.NotFound();
    }

    await connection.ExecuteAsync(@"
        update tasks
        set status = @Status,
            attempt_count = @AttemptCount,
            result = @Result,
            error_message = @ErrorMessage,
            updated_at_utc = @UpdatedAtUtc
        where id = @Id;", new
    {
        Id = id,
        Status = request.Status,
        AttemptCount = request.AttemptCount,
        Result = request.Result,
        ErrorMessage = request.ErrorMessage,
        UpdatedAtUtc = DateTime.UtcNow
    });

    return Results.Ok(new { id, status = request.Status });
});

app.Run();

static async Task<ConnectionMultiplexer> ConnectRedisWithRetryAsync(string connectionString, ILogger logger)
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
            logger.LogWarning(ex, "Redis connection attempt {Attempt}/{MaxAttempts} failed.", attempt, maxAttempts);
            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, attempt)));
            }
        }
    }

    throw new InvalidOperationException("Redis connection could not be established after retries.", lastError);
}

static async Task InitializeSchemaWithRetryAsync(string postgresConnectionString, ILogger logger)
{
    const int maxAttempts = 10;
    Exception? lastError = null;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await InitializeSchemaAsync(postgresConnectionString);
            return;
        }
        catch (Exception ex)
        {
            lastError = ex;
            logger.LogWarning(ex, "PostgreSQL schema init attempt {Attempt}/{MaxAttempts} failed.", attempt, maxAttempts);
            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, attempt)));
            }
        }
    }

    throw new InvalidOperationException("PostgreSQL schema init failed after retries.", lastError);
}

static async Task InitializeSchemaAsync(string postgresConnectionString)
{
    await using var connection = new NpgsqlConnection(postgresConnectionString);
    await connection.ExecuteAsync(@"
        create table if not exists tasks (
            id uuid primary key,
            title text not null,
            description text not null,
            status text not null,
            attempt_count integer not null default 0,
            result text null,
            error_message text null,
            created_at_utc timestamp with time zone not null,
            updated_at_utc timestamp with time zone not null
        );

        alter table tasks add column if not exists attempt_count integer not null default 0;
        alter table tasks add column if not exists result text null;
        alter table tasks add column if not exists error_message text null;
    ");
}

internal static class TaskStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Retrying = "Retrying";
    public const string Executing = "Executing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

internal sealed record CreateTaskRequest(string Title, string? Description);

internal sealed record UpdateTaskStatusRequest(string Status, int AttemptCount, string? Result, string? ErrorMessage);

internal sealed record AgentTask(
    Guid Id,
    string Title,
    string Description,
    string Status,
    int AttemptCount,
    string? Result,
    string? ErrorMessage,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
