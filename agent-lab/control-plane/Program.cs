using Dapper;
using Microsoft.OpenApi.Models;
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Control Plane API", Version = "v1" }));

var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=agentlab;Username=agentlab;Password=agentlab";
var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Control Plane API v1"));
}

var redis = await ConnectRedisWithRetryAsync(redisConnectionString, app.Logger);
var redisDb = redis.GetDatabase();
const string queueKey = "agentlab:task-queue";

await InitializeSchemaWithRetryAsync(postgresConnectionString, app.Logger);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "control-plane" }))
    .WithName("GetHealth")
    .WithSummary("Check Control Plane health")
    .WithDescription("Returns the current health status of the Control Plane API.")
    .Produces(StatusCodes.Status200OK);

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
})
    .WithName("ListTasks")
    .WithSummary("List tasks")
    .WithDescription("Returns tasks ordered by creation time, newest first.")
    .Produces<IEnumerable<AgentTask>>(StatusCodes.Status200OK);

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
})
    .WithName("CreateTask")
    .WithSummary("Create a task")
    .WithDescription("Creates a task in Draft state. A title is required.")
    .Produces<AgentTask>(StatusCodes.Status201Created)
    .Produces(StatusCodes.Status400BadRequest);

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
})
    .WithName("GetTaskById")
    .WithSummary("Get a task")
    .WithDescription("Returns a task by its ID.")
    .Produces<AgentTask>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status404NotFound);

app.MapPost("/tasks/{id:guid}/approve", async (Guid id) =>
{
    await using var connection = new NpgsqlConnection(postgresConnectionString);
    await connection.OpenAsync();

    var currentStatus = await connection.QuerySingleOrDefaultAsync<string>("select status from tasks where id = @Id;", new { Id = id });
    if (currentStatus is null)
    {
        return Results.NotFound();
    }

    if (!TaskStatusTransitions.TryValidateTransition(currentStatus, TaskStatuses.Approved, out var errorMessage))
    {
        return Results.BadRequest(new { error = errorMessage });
    }

    var updatedAtUtc = DateTime.UtcNow;
    await using var transaction = await connection.BeginTransactionAsync();

    await connection.ExecuteAsync(@"
        update tasks
        set status = @Status,
            error_message = null,
            result = null,
            updated_at_utc = @UpdatedAtUtc
        where id = @Id;", new { Id = id, Status = TaskStatuses.Approved, UpdatedAtUtc = updatedAtUtc }, transaction);

    await InsertTaskStatusTransitionAsync(
        connection,
        transaction,
        id,
        currentStatus,
        TaskStatuses.Approved,
        "ApproveTask");

    await transaction.CommitAsync();

    await redisDb.ListRightPushAsync(queueKey, id.ToString());

    return Results.Ok(new { id, status = TaskStatuses.Approved });
})
    .WithName("ApproveTask")
    .WithSummary("Approve a task")
    .WithDescription("Approves a Draft task and places it on the worker queue.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status404NotFound);

app.MapPost("/tasks/{id:guid}/retry", async (Guid id) =>
{
    await using var connection = new NpgsqlConnection(postgresConnectionString);
    await connection.OpenAsync();

    var currentStatus = await connection.QuerySingleOrDefaultAsync<string>("select status from tasks where id = @Id;", new { Id = id });
    if (currentStatus is null)
    {
        return Results.NotFound();
    }

    if (!TaskStatusTransitions.TryValidateTransition(currentStatus, TaskStatuses.Retrying, out var errorMessage))
    {
        return Results.BadRequest(new { error = errorMessage });
    }

    var updatedAtUtc = DateTime.UtcNow;
    await using var transaction = await connection.BeginTransactionAsync();

    await connection.ExecuteAsync(@"
        update tasks
        set status = @Status,
            updated_at_utc = @UpdatedAtUtc
        where id = @Id;", new { Id = id, Status = TaskStatuses.Retrying, UpdatedAtUtc = updatedAtUtc }, transaction);

    await InsertTaskStatusTransitionAsync(
        connection,
        transaction,
        id,
        currentStatus,
        TaskStatuses.Retrying,
        "RetryTask");

    await transaction.CommitAsync();

    await redisDb.ListRightPushAsync(queueKey, id.ToString());

    return Results.Ok(new { id, status = TaskStatuses.Retrying });
})
    .WithName("RetryTask")
    .WithSummary("Retry a failed task")
    .WithDescription("Moves a Failed task back to the worker queue.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status404NotFound);

app.MapPost("/tasks/{id:guid}/status", async (Guid id, UpdateTaskStatusRequest request) =>
{
    if (!TaskStatusTransitions.IsKnownStatus(request.Status))
    {
        return Results.BadRequest(new { error = "Unsupported status." });
    }

    await using var connection = new NpgsqlConnection(postgresConnectionString);
    await connection.OpenAsync();
    var currentStatus = await connection.QuerySingleOrDefaultAsync<string>("select status from tasks where id = @Id;", new { Id = id });
    if (currentStatus is null)
    {
        return Results.NotFound();
    }

    if (!TaskStatusTransitions.TryValidateTransition(currentStatus, request.Status, out var errorMessage))
    {
        return Results.BadRequest(new { error = errorMessage });
    }

    var updatedAtUtc = DateTime.UtcNow;
    await using var transaction = await connection.BeginTransactionAsync();

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
        UpdatedAtUtc = updatedAtUtc
    }, transaction);

    await InsertTaskStatusTransitionAsync(
        connection,
        transaction,
        id,
        currentStatus,
        request.Status,
        "UpdateTaskStatus");

    await transaction.CommitAsync();

    return Results.Ok(new { id, status = request.Status });
})
    .WithName("UpdateTaskStatus")
    .WithSummary("Update task status")
    .WithDescription("Updates a task's status, attempt count, result, and error details.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status404NotFound);

app.MapGet("/plans", async () =>
{
    await using var connection = new NpgsqlConnection(postgresConnectionString);
    var plans = await connection.QueryAsync<ExecutionPlan>(@"
        select id,
               project_key as ProjectKey,
               request_title as RequestTitle,
               request_description as RequestDescription,
               status,
               plan_summary as PlanSummary,
               created_at_utc as CreatedAtUtc,
               updated_at_utc as UpdatedAtUtc
        from execution_plans
        order by created_at_utc desc;");

    return Results.Ok(plans);
})
    .WithName("ListPlans")
    .WithSummary("List plans")
    .WithDescription("Returns execution plans ordered by creation time, newest first.")
    .Produces<IEnumerable<ExecutionPlan>>(StatusCodes.Status200OK);

app.MapPost("/plans/generate", async (GeneratePlanRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.RequestTitle))
    {
        return Results.BadRequest(new { error = "RequestTitle is required." });
    }

    var now = DateTime.UtcNow;
    var plan = new ExecutionPlan(
        Id: Guid.NewGuid(),
        ProjectKey: request.ProjectKey?.Trim(),
        RequestTitle: request.RequestTitle.Trim(),
        RequestDescription: request.RequestDescription?.Trim(),
        Status: PlanStatuses.Proposed,
        PlanSummary: BuildPlanSummary(request.RequestTitle, request.RequestDescription),
        CreatedAtUtc: now,
        UpdatedAtUtc: now);

    await using var connection = new NpgsqlConnection(postgresConnectionString);
    await connection.ExecuteAsync(@"
        insert into execution_plans
            (id, project_key, request_title, request_description, status, plan_summary, created_at_utc, updated_at_utc)
        values
            (@Id, @ProjectKey, @RequestTitle, @RequestDescription, @Status, @PlanSummary, @CreatedAtUtc, @UpdatedAtUtc);", plan);

    return Results.Created($"/plans/{plan.Id}", plan);
})
    .WithName("GeneratePlan")
    .WithSummary("Generate a plan skeleton")
    .WithDescription("Creates a proposed execution plan from request metadata.")
    .Produces<ExecutionPlan>(StatusCodes.Status201Created)
    .Produces(StatusCodes.Status400BadRequest);

app.MapPost("/plans/{id:guid}/approve", async (Guid id) =>
{
    await using var connection = new NpgsqlConnection(postgresConnectionString);

    var currentStatus = await connection.QuerySingleOrDefaultAsync<string>(
        "select status from execution_plans where id = @Id;",
        new { Id = id });

    if (currentStatus is null)
    {
        return Results.NotFound();
    }

    if (!PlanStatusTransitions.TryValidateTransition(currentStatus, PlanStatuses.Approved, out var errorMessage))
    {
        return Results.BadRequest(new { error = errorMessage });
    }

    await connection.ExecuteAsync(@"
        update execution_plans
        set status = @Status,
            updated_at_utc = @UpdatedAtUtc
        where id = @Id;",
        new { Id = id, Status = PlanStatuses.Approved, UpdatedAtUtc = DateTime.UtcNow });

    return Results.Ok(new { id, status = PlanStatuses.Approved });
})
    .WithName("ApprovePlan")
    .WithSummary("Approve a proposed plan")
    .WithDescription("Approves a plan generated by /plans/generate.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status404NotFound);

app.Run();

static string BuildPlanSummary(string requestTitle, string? requestDescription)
{
    var description = string.IsNullOrWhiteSpace(requestDescription)
        ? "No description provided."
        : requestDescription.Trim();

    return $"""
Plan skeleton for: {requestTitle.Trim()}
- Analyze scope and constraints
- Break down into tasks with dependencies
- Define validation and approval gates
Input description: {description}
""";
}

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

        create table if not exists task_status_transitions (
            id uuid primary key,
            task_id uuid not null references tasks(id) on delete cascade,
            from_status text not null,
            to_status text not null,
            source text not null,
            changed_at_utc timestamp with time zone not null,
            note text null
        );

        create index if not exists ix_task_status_transitions_task_id_changed_at
            on task_status_transitions(task_id, changed_at_utc desc);

        create table if not exists execution_plans (
            id uuid primary key,
            project_key text null,
            request_title text not null,
            request_description text null,
            status text not null,
            plan_summary text not null,
            created_at_utc timestamp with time zone not null,
            updated_at_utc timestamp with time zone not null
        );

        alter table execution_plans add column if not exists project_key text null;
        alter table execution_plans add column if not exists request_title text not null default '';
        alter table execution_plans add column if not exists request_description text null;
        alter table execution_plans add column if not exists status text not null default 'Proposed';
        alter table execution_plans add column if not exists plan_summary text not null default '';
        alter table execution_plans add column if not exists created_at_utc timestamp with time zone not null default now();
        alter table execution_plans add column if not exists updated_at_utc timestamp with time zone not null default now();

        create index if not exists ix_execution_plans_created_at
            on execution_plans(created_at_utc desc);

        alter table tasks add column if not exists attempt_count integer not null default 0;
        alter table tasks add column if not exists result text null;
        alter table tasks add column if not exists error_message text null;
    ");
}

static Task InsertTaskStatusTransitionAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid taskId,
    string fromStatus,
    string toStatus,
    string source,
    string? note = null)
{
    return connection.ExecuteAsync(@"
        insert into task_status_transitions
            (id, task_id, from_status, to_status, source, changed_at_utc, note)
        values
            (@Id, @TaskId, @FromStatus, @ToStatus, @Source, @ChangedAtUtc, @Note);",
        new
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            Source = source,
            ChangedAtUtc = DateTime.UtcNow,
            Note = note
        },
        transaction);
}

internal static class TaskStatuses
{
    public const string Draft = "Draft";
    public const string Proposed = "Proposed";
    public const string Planned = "Planned";
    public const string Approved = "Approved";
    public const string Retrying = "Retrying";
    public const string InProgress = "InProgress";
    public const string Executing = "Executing";
    public const string Review = "Review";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Blocked = "Blocked";
    public const string Cancelled = "Cancelled";
    public const string Rejected = "Rejected";
}

internal static class TaskStatusTransitions
{
    private static readonly HashSet<string> KnownStatuses =
    [
        TaskStatuses.Draft,
        TaskStatuses.Proposed,
        TaskStatuses.Planned,
        TaskStatuses.Approved,
        TaskStatuses.Retrying,
        TaskStatuses.InProgress,
        TaskStatuses.Executing,
        TaskStatuses.Review,
        TaskStatuses.Completed,
        TaskStatuses.Failed,
        TaskStatuses.Blocked,
        TaskStatuses.Cancelled,
        TaskStatuses.Rejected
    ];

    private static readonly Dictionary<string, HashSet<string>> AllowedTransitions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [TaskStatuses.Draft] =
            [
                TaskStatuses.Approved,
                TaskStatuses.Cancelled,
                TaskStatuses.Rejected
            ],
            [TaskStatuses.Proposed] =
            [
                TaskStatuses.Planned,
                TaskStatuses.Cancelled,
                TaskStatuses.Rejected
            ],
            [TaskStatuses.Planned] =
            [
                TaskStatuses.Approved,
                TaskStatuses.Blocked,
                TaskStatuses.Cancelled
            ],
            [TaskStatuses.Approved] =
            [
                TaskStatuses.Executing,
                TaskStatuses.InProgress,
                TaskStatuses.Blocked,
                TaskStatuses.Cancelled
            ],
            [TaskStatuses.Retrying] =
            [
                TaskStatuses.Approved,
                TaskStatuses.Executing,
                TaskStatuses.InProgress,
                TaskStatuses.Failed,
                TaskStatuses.Cancelled
            ],
            [TaskStatuses.InProgress] =
            [
                TaskStatuses.Review,
                TaskStatuses.Blocked,
                TaskStatuses.Failed,
                TaskStatuses.Cancelled
            ],
            [TaskStatuses.Executing] =
            [
                TaskStatuses.Review,
                TaskStatuses.Completed,
                TaskStatuses.Failed,
                TaskStatuses.Blocked,
                TaskStatuses.Cancelled
            ],
            [TaskStatuses.Review] =
            [
                TaskStatuses.Completed,
                TaskStatuses.InProgress,
                TaskStatuses.Blocked,
                TaskStatuses.Rejected,
                TaskStatuses.Cancelled
            ],
            [TaskStatuses.Failed] =
            [
                TaskStatuses.Retrying,
                TaskStatuses.Cancelled,
                TaskStatuses.Rejected
            ],
            [TaskStatuses.Blocked] =
            [
                TaskStatuses.Planned,
                TaskStatuses.InProgress,
                TaskStatuses.Approved,
                TaskStatuses.Cancelled
            ],
            [TaskStatuses.Completed] = [],
            [TaskStatuses.Cancelled] = [],
            [TaskStatuses.Rejected] = []
        };

    public static bool IsKnownStatus(string status)
    {
        return KnownStatuses.Contains(status);
    }

    public static bool TryValidateTransition(string currentStatus, string targetStatus, out string? errorMessage)
    {
        if (!IsKnownStatus(currentStatus))
        {
            errorMessage = $"Current status '{currentStatus}' is not supported.";
            return false;
        }

        if (!IsKnownStatus(targetStatus))
        {
            errorMessage = $"Target status '{targetStatus}' is not supported.";
            return false;
        }

        if (string.Equals(currentStatus, targetStatus, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = null;
            return true;
        }

        if (!AllowedTransitions.TryGetValue(currentStatus, out var nextStatuses) || !nextStatuses.Contains(targetStatus))
        {
            errorMessage = $"Invalid status transition from '{currentStatus}' to '{targetStatus}'.";
            return false;
        }

        errorMessage = null;
        return true;
    }
}

internal static class PlanStatuses
{
    public const string Proposed = "Proposed";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

internal static class PlanStatusTransitions
{
    private static readonly Dictionary<string, HashSet<string>> AllowedTransitions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [PlanStatuses.Proposed] = [PlanStatuses.Approved, PlanStatuses.Rejected],
            [PlanStatuses.Approved] = [],
            [PlanStatuses.Rejected] = []
        };

    public static bool TryValidateTransition(string currentStatus, string targetStatus, out string? errorMessage)
    {
        if (!AllowedTransitions.ContainsKey(currentStatus))
        {
            errorMessage = $"Current plan status '{currentStatus}' is not supported.";
            return false;
        }

        if (!AllowedTransitions.TryGetValue(currentStatus, out var nextStatuses) || !nextStatuses.Contains(targetStatus))
        {
            errorMessage = $"Invalid plan status transition from '{currentStatus}' to '{targetStatus}'.";
            return false;
        }

        errorMessage = null;
        return true;
    }
}

internal sealed record GeneratePlanRequest(string? ProjectKey, string RequestTitle, string? RequestDescription);

internal sealed record ExecutionPlan(
    Guid Id,
    string? ProjectKey,
    string RequestTitle,
    string? RequestDescription,
    string Status,
    string PlanSummary,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

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
