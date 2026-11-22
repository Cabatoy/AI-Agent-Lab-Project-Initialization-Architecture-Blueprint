using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Dapper;
using Microsoft.OpenApi.Models;
using Npgsql;
using StackExchange.Redis;
using ControlPlane.Api.TransactionalMail;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Control Plane API", Version = "v1" }));

var captureMailEnabled = string.Equals(
    builder.Configuration["HIC_MAIL_CAPTURE_ENABLED"],
    "true",
    StringComparison.OrdinalIgnoreCase);
if (captureMailEnabled && builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "The non-production transactional mail capture sink is forbidden in Production.");
}
if (captureMailEnabled)
{
    builder.Services.AddSingleton<ITransactionalMailSender>(
        new CaptureTransactionalMailSink(
            builder.Environment.EnvironmentName, explicitlyEnabled: true,
            trustedResetOrigin: builder.Configuration["HIC_MAIL_TRUSTED_RESET_ORIGIN"]));
}
else
{
    builder.Services.AddSingleton<ITransactionalMailSender, UnconfiguredTransactionalMailSender>();
}

var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=agentlab;Username=agentlab;Password=agentlab";
var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
var trustedAuthOrigin = HicAuthRequestGuard.NormalizeTrustedOrigin(
    builder.Configuration["HIC_AUTH_TRUSTED_ORIGIN"], builder.Environment.IsDevelopment());
var userSessionStore = new HicUserSessionStore(postgresConnectionString);
var cookieSecurePolicy = !builder.Environment.IsDevelopment() ||
    trustedAuthOrigin?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true
    ? CookieSecurePolicy.Always
    : CookieSecurePolicy.SameAsRequest;

builder.Services.AddSingleton(userSessionStore);
builder.Services.AddAuthentication(HicUserSession.Scheme)
    .AddCookie(HicUserSession.Scheme, options =>
    {
        options.Cookie.Name = HicUserSession.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = cookieSecurePolicy;
        options.Cookie.Path = "/";
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = HicUserSessionStore.Lifetime;
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = async context =>
        {
            if (!HicUserSessionStore.TryGetSession(context.Principal!, out var sessionId, out var userId))
            {
                context.RejectPrincipal();
                return;
            }

            try
            {
                if (!await userSessionStore.IsActiveAsync(sessionId, userId, context.HttpContext.RequestAborted))
                    context.RejectPrincipal();
            }
            catch
            {
                context.RejectPrincipal();
            }
        };
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("HicUser", policy =>
        policy.AddAuthenticationSchemes(HicUserSession.Scheme).RequireAuthenticatedUser()));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = HicUserSession.CsrfHeaderName;
    options.Cookie.Name = HicUserSession.AntiforgeryCookieName;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = cookieSecurePolicy;
    options.Cookie.Path = "/";
});
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("hic-user-login", HicLoginRateLimit.ForRequest);
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many login attempts." }, cancellationToken);
    };
});

var localOperatorSessions = LocalOperatorSessions.Create(
    builder.Configuration["HIC_LOCAL_OPERATOR_TOKEN"],
    builder.Configuration["HIC_LOCAL_OPERATOR_ID"]);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Control Plane API v1"));
}

app.Use(async (context, next) =>
{
    var isLocalRunner = context.Request.Path.StartsWithSegments("/local-runner");
    var isUserAuthentication = context.Request.Path.StartsWithSegments("/auth") ||
        context.Request.Path.StartsWithSegments("/account");
    if (!isLocalRunner && !isUserAuthentication)
    {
        await next();
        return;
    }
    if (isUserAuthentication)
        context.Response.Headers.CacheControl = "no-store";

    try
    {
        await next();
    }
    catch (Exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogError(isLocalRunner
            ? "Local runner request failed; exception details withheld."
            : "HIC authentication request failed; exception details withheld.");
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(
            new { error = isLocalRunner ? "Local runner is unavailable." : "Authentication is unavailable." },
            CancellationToken.None);
    }
});

var redis = await ConnectRedisWithRetryAsync(redisConnectionString, app.Logger);
var redisDb = redis.GetDatabase();
const string queueKey = "agentlab:task-queue";

await InitializeSchemaWithRetryAsync(postgresConnectionString, app.Logger);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "control-plane" }))
    .WithName("GetHealth")
    .WithSummary("Check Control Plane health")
    .WithDescription("Returns the current health status of the Control Plane API.")
    .Produces(StatusCodes.Status200OK);

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { requestToken = tokens.RequestToken });
})
    .AllowAnonymous()
    .Produces(StatusCodes.Status200OK);

app.MapGet("/auth/session", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var identity = context.User.Identity?.IsAuthenticated == true
        ? await HicUserSession.GetCurrentIdentityAsync(postgresConnectionString, context.User, cancellationToken)
        : null;
    return Results.Ok(identity is null
        ? new { authenticated = false, user = (object?)null }
        : new
        {
            authenticated = true,
            user = new { id = identity.Id, email = identity.NormalizedEmail, username = identity.NormalizedUsername }
        });
})
    .AllowAnonymous()
    .Produces(StatusCodes.Status200OK);

app.MapPost("/auth/login", async (HttpContext context, HicUserLoginRequest request,
    IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    if (trustedAuthOrigin is null)
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Authentication is not configured.");
    if (!HicAuthRequestGuard.HasTrustedOrigin(context.Request, trustedAuthOrigin))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!await HicAuthRequestGuard.HasValidCsrfAsync(context, antiforgery))
        return Results.StatusCode(StatusCodes.Status400BadRequest);

    var identity = await HicUserAuthenticator.AuthenticateAsync(
        postgresConnectionString, request.Identifier, request.Password, cancellationToken);
    if (identity is null)
        return Results.Json(new { error = "Invalid credentials." }, statusCode: StatusCodes.Status401Unauthorized);

    if (HicUserSessionStore.TryGetSession(context.User, out var oldSessionId, out _))
    {
        await userSessionStore.RevokeAsync(oldSessionId, cancellationToken);
        await context.SignOutAsync(HicUserSession.Scheme);
    }

    var now = DateTime.UtcNow;
    var sessionId = Guid.NewGuid();
    var expiresAt = now.Add(HicUserSessionStore.Lifetime);
    await userSessionStore.CreateAsync(sessionId, identity.Id, expiresAt, cancellationToken);
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, identity.Id.ToString()),
        new Claim(ClaimTypes.Name, identity.NormalizedUsername),
        new Claim(ClaimTypes.Email, identity.NormalizedEmail),
        new Claim(HicUserSession.SessionClaim, sessionId.ToString())
    };
    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, HicUserSession.Scheme));
    await context.SignInAsync(HicUserSession.Scheme, principal, new AuthenticationProperties
    {
        IsPersistent = false,
        AllowRefresh = false,
        IssuedUtc = now,
        ExpiresUtc = expiresAt
    });

    return Results.Ok(new
    {
        authenticated = true,
        user = new { id = identity.Id, email = identity.NormalizedEmail, username = identity.NormalizedUsername }
    });
})
    .AllowAnonymous()
    .RequireRateLimiting("hic-user-login")
    .WithMetadata(new RequestSizeLimitAttribute(4096))
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status403Forbidden)
    .Produces(StatusCodes.Status429TooManyRequests);

app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery,
    CancellationToken cancellationToken) =>
{
    if (context.Request.ContentLength is > 0 ||
        context.Request.Headers.ContainsKey("Transfer-Encoding"))
        return Results.BadRequest(new { error = "Logout does not accept a request body." });
    if (trustedAuthOrigin is null)
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Authentication is not configured.");
    if (!HicAuthRequestGuard.HasTrustedOrigin(context.Request, trustedAuthOrigin))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!await HicAuthRequestGuard.HasValidCsrfAsync(context, antiforgery))
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    if (HicUserSessionStore.TryGetSession(context.User, out var sessionId, out _))
        await userSessionStore.RevokeAsync(sessionId, cancellationToken);

    await context.SignOutAsync(HicUserSession.Scheme);
    return Results.NoContent();
})
    .AllowAnonymous()
    .WithMetadata(new RequestSizeLimitAttribute(1024))
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status403Forbidden);

app.MapGet("/account/me", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var identity = await HicUserSession.GetCurrentIdentityAsync(
        postgresConnectionString, context.User, cancellationToken);
    return identity is null
        ? Results.Unauthorized()
        : Results.Ok(new { id = identity.Id, email = identity.NormalizedEmail, username = identity.NormalizedUsername });
})
    .RequireAuthorization("HicUser")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized);

// Explicit, read-only, allowlisted diagnostics. Not a task approval or execution API.
app.MapGet("/diagnostics/local", async (CancellationToken cancellationToken) =>
    Results.Ok(await LocalDiagnostics.InspectAsync(postgresConnectionString, redisDb, cancellationToken)))
    .WithName("GetLocalDiagnostics")
    .WithSummary("Check Control Plane, PostgreSQL and Redis without running an agent")
    .WithDescription("Local development diagnostics only. Does not inspect Worker status or change state.")
    .Produces<LocalDiagnosticSnapshot>(StatusCodes.Status200OK);

LocalHealthRunnerEndpoints.Map(app, postgresConnectionString, redisDb, localOperatorSessions);

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

app.MapPost("/tasks/{id:guid}/approve", async (Guid id, CancellationToken cancellationToken) =>
{
    var transition = await TaskTransitionStore.ApplyAsync(
        postgresConnectionString, id, TaskStatuses.Approved, "ApproveTask", cancellationToken: cancellationToken);
    if (transition.Outcome != TransitionOutcome.Accepted)
        return TransitionError(transition);

    await redisDb.ListRightPushAsync(queueKey, id.ToString());

    return Results.Ok(new { id, status = TaskStatuses.Approved });
})
    .WithName("ApproveTask")
    .WithSummary("Approve a task")
    .WithDescription("Approves a Draft task and places it on the worker queue.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict)
    .Produces(StatusCodes.Status404NotFound);

app.MapPost("/tasks/{id:guid}/retry", async (Guid id, CancellationToken cancellationToken) =>
{
    var transition = await TaskTransitionStore.ApplyAsync(
        postgresConnectionString, id, TaskStatuses.Retrying, "RetryTask", cancellationToken: cancellationToken);
    if (transition.Outcome != TransitionOutcome.Accepted)
        return TransitionError(transition);

    await redisDb.ListRightPushAsync(queueKey, id.ToString());

    return Results.Ok(new { id, status = TaskStatuses.Retrying });
})
    .WithName("RetryTask")
    .WithSummary("Retry a failed task")
    .WithDescription("Moves a Failed task back to the worker queue.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict)
    .Produces(StatusCodes.Status404NotFound);

app.MapPost("/tasks/{id:guid}/status", async (Guid id, UpdateTaskStatusRequest request, CancellationToken cancellationToken) =>
{
    if (!TaskStatusTransitions.IsKnownStatus(request.Status))
    {
        return Results.BadRequest(new { error = "Unsupported status." });
    }

    var transition = await TaskTransitionStore.ApplyAsync(
        postgresConnectionString, id, request.Status, "UpdateTaskStatus", request, cancellationToken);
    if (transition.Outcome != TransitionOutcome.Accepted)
        return TransitionError(transition);

    return Results.Ok(new { id, status = request.Status, execution = request.Execution });
})
    .WithName("UpdateTaskStatus")
    .WithSummary("Update task status")
    .WithDescription("Updates a task's status, attempt count, result, and error details.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict)
    .Produces(StatusCodes.Status404NotFound);

app.MapPost("/tasks/{id:guid}/executions/claim", async (Guid id, ClaimExecutionRequest request, CancellationToken cancellationToken) =>
{
    var claim = await ExecutionOwnershipStore.ClaimAsync(postgresConnectionString, id, request, cancellationToken);
    if (claim.Outcome != TransitionOutcome.Accepted)
        return TransitionError(new TransitionResult(claim.Outcome, claim.Error));
    var execution = claim.Execution!;
    return Results.Ok(new
    {
        id, status = execution.Status, executionId = execution.Id, execution.WorkerId,
        execution.Generation, newlyClaimed = claim.NewlyClaimed
    });
})
    .WithName("ClaimTaskExecution")
    .WithSummary("Atomically claim task execution; replay never grants fresh execution permission.")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status404NotFound)
    .Produces(StatusCodes.Status409Conflict);

static IResult TransitionError(TransitionResult transition) => transition.Outcome switch
{
    TransitionOutcome.NotFound => Results.NotFound(),
    TransitionOutcome.Conflict => Results.Conflict(new { error = transition.Error }),
    TransitionOutcome.Invalid => Results.BadRequest(new { error = transition.Error }),
    _ => throw new InvalidOperationException("Expected a rejected transition.")
};

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
            await ExecutionSchema.VerifyAsync(postgresConnectionString);
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

internal sealed record UpdateTaskStatusRequest(string Status, int AttemptCount, string? Result, string? ErrorMessage,
    string? ExpectedStatus = null, ExecutionIdentity? Execution = null);

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
