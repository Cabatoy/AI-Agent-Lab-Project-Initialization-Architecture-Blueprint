using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using StackExchange.Redis;

internal sealed record LocalHealthIntent(
    Guid TaskId,
    string TaskTitle,
    string Action,
    string Scope,
    string TaskUpdatedAtUtc,
    string IntentDigestSha256);

internal sealed record LocalHealthRun(
    Guid RunId,
    Guid TaskId,
    Guid ActorId,
    string Action,
    string Scope,
    string IntentDigestSha256,
    string Status,
    DateTime CreatedAtUtc,
    DateTime ApprovedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? ResultJson);

internal sealed record LocalHealthRunEvent(string EventCode, DateTime RecordedAtUtc);
internal sealed record LocalHealthRunSnapshot(LocalHealthRun Run, IReadOnlyList<LocalHealthRunEvent> Events);
internal sealed record LocalHealthClaimOutcome(string Code, LocalHealthRun? Run = null);

internal static class LocalHealthRunIntent
{
    internal const string Action = "system.health";
    internal const string Scope = "control-plane:health:read";
    private const string Domain = "hic-local-health-intent-v1";

    internal static string Digest(Guid taskId, string title, string description, DateTime updatedAtUtc)
    {
        var fields = new[]
        {
            Domain,
            taskId.ToString("N"),
            updatedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            title,
            description,
            Action,
            Scope
        };
        var canonical = string.Concat(fields.Select(field =>
            $"{Encoding.UTF8.GetByteCount(field)}:{field}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static bool Matches(string? expected, string actual)
    {
        if (expected is not { Length: 64 } || !Regex.IsMatch(expected, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected), Convert.FromHexString(actual));
    }
}

internal sealed class LocalOperatorSessions
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);
    private readonly byte[] bootstrapTokenHash;
    private readonly Guid actorId;
    private readonly Dictionary<string, DateTimeOffset> sessions = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly TimeProvider clock;

    private LocalOperatorSessions(byte[] bootstrapTokenHash, Guid actorId, TimeProvider clock)
    {
        this.bootstrapTokenHash = bootstrapTokenHash;
        this.actorId = actorId;
        this.clock = clock;
    }

    internal static LocalOperatorSessions? Create(string? bootstrapToken, string? actorIdText, TimeProvider? clock = null)
    {
        if (!Guid.TryParse(actorIdText, out var actorId) || actorId == Guid.Empty ||
            bootstrapToken is null || !Regex.IsMatch(bootstrapToken, "^[A-Za-z0-9+/]{43}=$", RegexOptions.CultureInvariant))
            return null;

        byte[] token;
        try { token = Convert.FromBase64String(bootstrapToken); }
        catch (FormatException) { return null; }
        if (token.Length < 32)
        {
            CryptographicOperations.ZeroMemory(token);
            return null;
        }

        var hash = SHA256.HashData(token);
        CryptographicOperations.ZeroMemory(token);
        return new LocalOperatorSessions(hash, actorId, clock ?? TimeProvider.System);
    }

    internal string? IssueSessionToken(string? suppliedBootstrapToken, bool browserSession = false)
    {
        if (suppliedBootstrapToken is null ||
            !Regex.IsMatch(suppliedBootstrapToken, "^[A-Za-z0-9+/]{43}=$", RegexOptions.CultureInvariant))
            return null;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(suppliedBootstrapToken ?? string.Empty); }
        catch (FormatException) { return null; }
        try
        {
            if (supplied.Length < 32 ||
                !CryptographicOperations.FixedTimeEquals(bootstrapTokenHash, SHA256.HashData(supplied)))
                return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(supplied);
        }

        var session = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        lock (sync)
        {
            var now = clock.GetUtcNow();
            RemoveExpiredSessions(now);
            sessions[Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session)))] =
                now.Add(browserSession ? TimeSpan.FromHours(8) : SessionLifetime);
        }
        return session;
    }

    internal Guid? Authenticate(string? authorizationHeader)
        => Restore(authorizationHeader)?.ActorId;

    internal (Guid ActorId, DateTimeOffset ExpiresAtUtc)? Restore(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var session = authorizationHeader[prefix.Length..];
        if (session.Length is < 40 or > 64) return null;
        var sessionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session)));
        lock (sync)
        {
            var now = clock.GetUtcNow();
            RemoveExpiredSessions(now);
            return sessions.TryGetValue(sessionHash, out var expiresAt) && now < expiresAt ? (actorId, expiresAt) : null;
        }
    }

    internal bool Revoke(string? authorizationHeader)
    {
        lock (sync)
        {
            if (Restore(authorizationHeader) is null) return false;
            return sessions.Remove(Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(authorizationHeader!["Bearer ".Length..]))));
        }
    }

    internal int RemainingSeconds(DateTimeOffset expiresAtUtc) =>
        (int)Math.Ceiling((expiresAtUtc - clock.GetUtcNow()).TotalSeconds);

    private void RemoveExpiredSessions(DateTimeOffset now)
    {
        foreach (var key in sessions.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            sessions.Remove(key);
    }
}

internal sealed class LocalHealthRunStore(string postgresConnectionString)
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record TaskIntentSource(Guid Id, string Title, string Description, string Status, DateTime UpdatedAtUtc);

    internal static string SerializeResult(LocalDiagnosticSnapshot result) =>
        JsonSerializer.Serialize(result, ResultJsonOptions);

    internal async Task<bool> IsSchemaReadyAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select to_regclass('app_schema_versions') is not null;",
                cancellationToken: cancellationToken)))
            return false;
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists(select 1 from app_schema_versions where version = 4);",
            cancellationToken: cancellationToken));
    }

    internal static async Task<bool> IsReservedTaskAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid taskId,
        CancellationToken cancellationToken)
    {
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select to_regclass('native_health_runs') is not null;",
                transaction: transaction, cancellationToken: cancellationToken)))
            return false;
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists(select 1 from native_health_runs where task_id = @taskId);",
            new { taskId }, transaction, cancellationToken: cancellationToken));
    }

    internal async Task<LocalHealthIntent?> GetIntentAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        var task = await ReadTaskAsync(connection, null, taskId, cancellationToken);
        if (task is null || task.Status != TaskStatuses.Draft) return null;
        var reserved = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists(select 1 from native_health_runs where task_id = @taskId);",
            new { taskId }, cancellationToken: cancellationToken));
        return reserved ? null : ToIntent(task);
    }

    internal async Task<bool> ModelRunStillCurrentAsync(
        Guid runId, ModelCodeEditPlan plan, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            select exists(
                select 1 from native_health_runs r
                join tasks t on t.id=r.task_id
                join hic_project_conversations c on c.conversation_id=r.conversation_id
                where r.run_id=@runId and r.task_id=@TaskId and r.actor_id=@ActorId
                    and r.task_updated_at_utc=t.updated_at_utc and t.status='Draft'
                    and c.actor_id=@ActorId and c.task_id=@TaskId and c.conversation_id=@ConversationId
                    and c.repository_id=@RepositoryId and c.base_commit=@BaseCommit and c.file_path=@File);
            """, new { runId, plan.TaskId, plan.ActorId, plan.ConversationId,
            plan.RepositoryId, plan.BaseCommit, plan.File }, cancellationToken: cancellationToken));
    }

    internal async Task<(string Code, LocalHealthRun? Run)> ApproveAsync(
        Guid taskId, Guid actorId, string expectedDigest, CancellationToken cancellationToken,
        ILocalRunPlan? plan = null)
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken, forUpdate: true);
        if (task is null) return ("not-found", null);
        if (task.Status != TaskStatuses.Draft) return ("task-not-draft", null);
        var intent = ToIntent(task);
        if (plan is not null)
        {
            if (plan.TaskId != taskId || plan.ActorId != actorId || plan.ExpiresAtUtc <= DateTime.UtcNow ||
                plan.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5) ||
                !LocalHealthRunIntent.Matches(plan.TaskDigest, intent.IntentDigestSha256))
                return ("intent-changed", null);
            intent = intent with
            {
                Action = plan.PlanAction, Scope = plan.PlanScope,
                IntentDigestSha256 = plan.Digest()
            };
        }
        if (!LocalHealthRunIntent.Matches(expectedDigest, intent.IntentDigestSha256))
            return ("intent-changed", null);

        var now = DateTime.UtcNow;
        var runId = Guid.NewGuid();
        var expiresAt = plan?.ExpiresAtUtc ?? now.AddMinutes(5);
        var insertSql = plan?.PlanConversationId is null
            ? @"
                insert into native_health_runs
                    (run_id, task_id, actor_id, action, scope, intent_digest_sha256, task_updated_at_utc,
                     status, created_at_utc, approved_at_utc, expires_at_utc)
                values
                    (@RunId, @TaskId, @ActorId, @Action, @Scope, @Digest, @TaskUpdatedAtUtc,
                     'queued', @Now, @Now, @ExpiresAtUtc)
                on conflict (task_id) do nothing returning run_id;"
            : @"
                insert into native_health_runs
                    (run_id, task_id, actor_id, action, scope, intent_digest_sha256, task_updated_at_utc,
                     status, created_at_utc, approved_at_utc, expires_at_utc, conversation_id, plan_json)
                values
                    (@RunId, @TaskId, @ActorId, @Action, @Scope, @Digest, @TaskUpdatedAtUtc,
                     'queued', @Now, @Now, @ExpiresAtUtc, @ConversationId, @PlanJson::jsonb)
                on conflict (task_id) do nothing returning run_id;";
        var inserted = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(insertSql,
            new
            {
                RunId = runId, TaskId = task.Id, ActorId = actorId,
                Action = plan?.PlanConversationId is null ? LocalHealthRunIntent.Action : intent.Action,
                Scope = plan?.PlanConversationId is null ? LocalHealthRunIntent.Scope : intent.Scope,
                Digest = intent.IntentDigestSha256, TaskUpdatedAtUtc = task.UpdatedAtUtc,
                Now = now, ExpiresAtUtc = expiresAt, ConversationId = plan?.PlanConversationId,
                PlanJson = plan?.PlanConversationId is null || plan is null
                    ? null : JsonSerializer.Serialize(plan, plan.GetType(), ResultJsonOptions)
            }, transaction, cancellationToken: cancellationToken));
        if (inserted is null)
        {
            var existing = await connection.QuerySingleOrDefaultAsync<LocalHealthRun>(new CommandDefinition(@"
                select run_id as RunId, task_id as TaskId, actor_id as ActorId, action, scope,
                       intent_digest_sha256 as IntentDigestSha256, status, created_at_utc as CreatedAtUtc,
                       approved_at_utc as ApprovedAtUtc, expires_at_utc as ExpiresAtUtc,
                       started_at_utc as StartedAtUtc, completed_at_utc as CompletedAtUtc,
                       result_json::text as ResultJson
                from native_health_runs where task_id = @TaskId;",
                new { TaskId = task.Id }, transaction, cancellationToken: cancellationToken));
            if (existing is not null && existing.ActorId == actorId &&
                LocalHealthRunIntent.Matches(expectedDigest, existing.IntentDigestSha256) &&
                task.UpdatedAtUtc == await ReadRunTaskVersionAsync(connection, transaction, existing.RunId, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return ("replay", existing);
            }
            return ("already-approved", null);
        }

        if (plan is not null)
            await connection.ExecuteAsync(new CommandDefinition(
                "update native_health_runs set action = @Action, scope = @Scope, plan_json = @Json::jsonb where run_id = @runId;",
                new { runId, Action = plan.PlanAction, Scope = plan.PlanScope,
                    Json = JsonSerializer.Serialize(plan, plan.GetType(), ResultJsonOptions) },
                transaction, cancellationToken: cancellationToken));
        await InsertEventAsync(connection, transaction, runId, actorId, "approved", cancellationToken);
        await InsertEventAsync(connection, transaction, runId, actorId, "queued", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ("accepted", new LocalHealthRun(runId, task.Id, actorId, intent.Action, intent.Scope,
            intent.IntentDigestSha256, "queued", now, now, expiresAt, null, null, null));
    }

    internal async Task<LocalHealthClaimOutcome> ClaimAsync(
        Guid runId, Guid actorId, CancellationToken cancellationToken, string? allowedAction = null)
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await connection.QuerySingleOrDefaultAsync<LocalHealthRun>(new CommandDefinition(@"
            select r.run_id as RunId, r.task_id as TaskId, r.actor_id as ActorId,
                   r.action, r.scope, r.intent_digest_sha256 as IntentDigestSha256,
                   r.status, r.created_at_utc as CreatedAtUtc, r.approved_at_utc as ApprovedAtUtc,
                   r.expires_at_utc as ExpiresAtUtc, r.started_at_utc as StartedAtUtc,
                   r.completed_at_utc as CompletedAtUtc, r.result_json::text as ResultJson
            from native_health_runs r
            where r.run_id = @runId
            for update;",
            new { runId }, transaction, cancellationToken: cancellationToken));
        if (run is null) return new("not-found");
        if (run.ActorId != actorId) return new("actor-mismatch");
        if (allowedAction is not null && run.Action != allowedAction) return new("isolation-required");
        if (run.Status != "queued")
        {
            await InsertEventAsync(connection, transaction, runId, actorId, "rejected", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new("not-queued", run);
        }

        var task = await ReadTaskAsync(connection, transaction, run.TaskId, cancellationToken, forUpdate: true);
        ILocalRunPlan? plan = run.Action switch
        {
            GitEditPlan.Action => await ReadPlanAsync(connection, transaction, runId, cancellationToken),
            ModelCodeEditPlan.Action => await ReadModelCodePlanAsync(connection, transaction, runId, cancellationToken),
            _ => null
        };
        var modelConversationMatches = plan is not ModelCodeEditPlan modelPlan ||
            await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select conversation_id = @conversationId from native_health_runs where run_id = @runId;",
                new { conversationId = modelPlan.ConversationId, runId }, transaction,
                cancellationToken: cancellationToken));
        var now = DateTime.UtcNow;
        if (now >= run.ExpiresAtUtc)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
                update native_health_runs set status = 'expired', completed_at_utc = @Now
                where run_id = @RunId and status = 'queued';",
                new { runId, Now = now }, transaction, cancellationToken: cancellationToken));
            await InsertEventAsync(connection, transaction, runId, actorId, "expired", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new("expired", run with { Status = "expired", CompletedAtUtc = now });
        }
        if (task is null || task.Status != TaskStatuses.Draft ||
            task.UpdatedAtUtc != await ReadRunTaskVersionAsync(connection, transaction, runId, cancellationToken) ||
            !(plan is null && run.Action == LocalHealthRunIntent.Action
                ? LocalHealthRunIntent.Matches(run.IntentDigestSha256, ToIntent(task).IntentDigestSha256)
                : plan is not null && plan.ActorId == actorId && plan.TaskId == run.TaskId &&
                  modelConversationMatches && plan.PlanAction == run.Action && plan.PlanScope == run.Scope &&
                  plan.ExpiresAtUtc == run.ExpiresAtUtc &&
                  LocalHealthRunIntent.Matches(plan.TaskDigest, ToIntent(task).IntentDigestSha256) &&
                  LocalHealthRunIntent.Matches(run.IntentDigestSha256, plan.Digest())))
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
                update native_health_runs set status = 'invalidated', completed_at_utc = @Now
                where run_id = @RunId and status = 'queued';",
                new { runId, Now = now }, transaction, cancellationToken: cancellationToken));
            await InsertEventAsync(connection, transaction, runId, actorId, "invalidated", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new("task-changed", run with { Status = "invalidated", CompletedAtUtc = now });
        }

        var changed = await connection.ExecuteAsync(new CommandDefinition(@"
            update native_health_runs set status = 'running', started_at_utc = @Now
            where run_id = @RunId and status = 'queued';",
            new { runId, Now = now }, transaction, cancellationToken: cancellationToken));
        if (changed != 1) return new("not-queued", run);
        await InsertEventAsync(connection, transaction, runId, actorId, "claimed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("claimed", run with { Status = "running", StartedAtUtc = now });
    }

    internal async Task<bool> CompleteAsync(
        Guid runId, Guid actorId, LocalDiagnosticSnapshot result, CancellationToken cancellationToken)
        => await CompleteResultAsync(runId, actorId, SerializeResult(result), cancellationToken);

    internal Task<bool> CompleteGitAsync(
        Guid runId, Guid actorId, GitEditResult result, CancellationToken cancellationToken) =>
        CompleteResultAsync(runId, actorId, JsonSerializer.Serialize(result, ResultJsonOptions), cancellationToken,
            result.TestCode == "passed:exact-content-and-scope" && result.SourceUnchanged ? "completed" : "failed");

    internal Task<bool> CompleteModelCodeAsync(
        Guid runId, Guid actorId, ModelCodeEditResult result, CancellationToken cancellationToken) =>
        CompleteResultAsync(runId, actorId, JsonSerializer.Serialize(result, ResultJsonOptions), cancellationToken,
            result.TestCode == "passed:isolated-csharp-compile-and-scope" && result.SourceUnchanged
                ? "completed" : "failed");

    private async Task<bool> CompleteResultAsync(
        Guid runId, Guid actorId, string resultJson, CancellationToken cancellationToken, string status = "completed")
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(@"
            update native_health_runs
            set status = @Status, result_json = @ResultJson::jsonb, completed_at_utc = @Now
            where run_id = @RunId and actor_id = @ActorId and status = 'running';",
            new { runId, actorId, ResultJson = resultJson, Now = DateTime.UtcNow, Status = status },
            transaction, cancellationToken: cancellationToken));
        if (changed != 1) return false;
        await InsertEventAsync(connection, transaction, runId, actorId, status, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal async Task<LocalHealthRunSnapshot?> GetAsync(
        Guid runId, Guid actorId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        var run = await connection.QuerySingleOrDefaultAsync<LocalHealthRun>(new CommandDefinition(@"
            select run_id as RunId, task_id as TaskId, actor_id as ActorId, action, scope,
                   intent_digest_sha256 as IntentDigestSha256, status, created_at_utc as CreatedAtUtc,
                   approved_at_utc as ApprovedAtUtc, expires_at_utc as ExpiresAtUtc,
                   started_at_utc as StartedAtUtc, completed_at_utc as CompletedAtUtc,
                   result_json::text as ResultJson
            from native_health_runs
            where run_id = @runId and actor_id = @actorId;",
            new { runId, actorId }, cancellationToken: cancellationToken));
        if (run is null) return null;
        var events = await connection.QueryAsync<LocalHealthRunEvent>(new CommandDefinition(@"
            select event_code as EventCode, recorded_at_utc as RecordedAtUtc
            from native_health_run_audit
            where run_id = @runId
            order by recorded_at_utc, id;",
            new { runId }, cancellationToken: cancellationToken));
        return new LocalHealthRunSnapshot(run, events.AsList());
    }

    internal async Task<GitEditPlan?> GetPlanAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        return await ReadPlanAsync(connection, null, runId, cancellationToken);
    }

    internal async Task<ModelCodeEditPlan?> GetModelCodePlanAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        return await ReadModelCodePlanAsync(connection, null, runId, cancellationToken);
    }

    private static async Task<GitEditPlan?> ReadPlanAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid runId, CancellationToken cancellationToken)
    {
        var json = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select plan_json::text from native_health_runs where run_id = @runId;",
            new { runId }, transaction, cancellationToken: cancellationToken));
        return json is null ? null : JsonSerializer.Deserialize<GitEditPlan>(json, ResultJsonOptions);
    }

    private static async Task<ModelCodeEditPlan?> ReadModelCodePlanAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid runId, CancellationToken cancellationToken)
    {
        var json = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select plan_json::text from native_health_runs where run_id = @runId;",
            new { runId }, transaction, cancellationToken: cancellationToken));
        return json is null ? null : JsonSerializer.Deserialize<ModelCodeEditPlan>(json, ResultJsonOptions);
    }

    private static LocalHealthIntent ToIntent(TaskIntentSource task) => new(
        task.Id, task.Title, LocalHealthRunIntent.Action, LocalHealthRunIntent.Scope,
        task.UpdatedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        LocalHealthRunIntent.Digest(task.Id, task.Title, task.Description, task.UpdatedAtUtc));

    private static Task<TaskIntentSource?> ReadTaskAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid taskId,
        CancellationToken cancellationToken, bool forUpdate = false) =>
        connection.QuerySingleOrDefaultAsync<TaskIntentSource>(new CommandDefinition($@"
            select id, title, description, status, updated_at_utc as UpdatedAtUtc
            from tasks where id = @taskId {(forUpdate ? "for update" : string.Empty)};",
            new { taskId }, transaction, cancellationToken: cancellationToken));

    private static Task<DateTime> ReadRunTaskVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "select task_updated_at_utc from native_health_runs where run_id = @runId;",
            new { runId }, transaction, cancellationToken: cancellationToken));

    private static Task InsertEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, Guid actorId,
        string eventCode, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(@"
            insert into native_health_run_audit(id, run_id, actor_id, event_code, recorded_at_utc)
            values (@Id, @RunId, @ActorId, @EventCode, @Now);",
            new { Id = Guid.NewGuid(), runId, actorId, eventCode, Now = DateTime.UtcNow },
            transaction, cancellationToken: cancellationToken));
}

internal static class LocalHealthRunnerEndpoints
{
    internal static void Map(
        WebApplication app, string postgresConnectionString, IDatabase redisDb,
        LocalOperatorSessions? sessions)
    {
        var store = new LocalHealthRunStore(postgresConnectionString);
        app.MapPost("/local-runner/session", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (sessions is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!await store.IsSchemaReadyAsync(cancellationToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var token = await ReadBootstrapTokenAsync(context.Request, cancellationToken);
            if (token is null) return Results.BadRequest(new { error = "Invalid session request." });
            var browserSession = context.Request.Headers["X-HIC-Session-Mode"] == "browser";
            var session = sessions.IssueSessionToken(token, browserSession);
            if (session is null) return Results.Unauthorized();
            var restored = sessions.Restore("Bearer " + session)!.Value;
            return Results.Ok(new
            {
                accessToken = session,
                expiresInSeconds = sessions.RemainingSeconds(restored.ExpiresAtUtc),
                expiresAtUtc = restored.ExpiresAtUtc
            });
        });

        app.MapGet("/local-runner/session", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var session = sessions?.Restore(context.Request.Headers.Authorization);
            return session is null ? Results.Unauthorized() : Results.Ok(new
            {
                actorId = session.Value.ActorId,
                expiresAtUtc = session.Value.ExpiresAtUtc,
                expiresInSeconds = sessions!.RemainingSeconds(session.Value.ExpiresAtUtc)
            });
        });
        app.MapPost("/local-runner/session/revoke", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                return Results.BadRequest();
            return sessions?.Revoke(context.Request.Headers.Authorization) == true
                ? Results.Ok(new { revoked = true }) : Results.Unauthorized();
        });

        app.MapGet("/local-runner/capabilities", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var actorId = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actorId is null) return sessions is null
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Unauthorized();
            if (!await store.IsSchemaReadyAsync(cancellationToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Results.Ok(new
            {
                actorId,
                action = LocalHealthRunIntent.Action,
                scope = LocalHealthRunIntent.Scope,
                worker = "not-used",
                provider = "not-used"
            });
        });

        app.MapGet("/local-runner/tasks/{taskId:guid}/intent", async (
            Guid taskId, HttpContext context, CancellationToken cancellationToken) =>
        {
            var actorId = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actorId is null) return sessions is null
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Unauthorized();
            if (!await store.IsSchemaReadyAsync(cancellationToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var intent = await store.GetIntentAsync(taskId, cancellationToken);
            return intent is null ? Results.Conflict(new { error = "Task is unavailable for local planning." }) : Results.Ok(intent);
        });

        app.MapPost("/local-runner/tasks/{taskId:guid}/approve", async (
            Guid taskId, HttpContext context, CancellationToken cancellationToken) =>
        {
            var actorId = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actorId is null) return sessions is null
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Unauthorized();
            if (!await store.IsSchemaReadyAsync(cancellationToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var digest = await ReadIntentDigestAsync(context.Request, cancellationToken);
            if (digest is null) return Results.BadRequest(new { error = "Invalid approval request." });
            var approval = await store.ApproveAsync(taskId, actorId.Value, digest, cancellationToken);
            return approval.Code switch
            {
                "accepted" or "replay" => Results.Accepted($"/local-runner/runs/{approval.Run!.RunId}", new
                {
                    runId = approval.Run.RunId, taskId = approval.Run.TaskId,
                    action = approval.Run.Action, scope = approval.Run.Scope,
                    intentDigestSha256 = approval.Run.IntentDigestSha256, status = approval.Run.Status
                }),
                "not-found" => Results.NotFound(),
                "intent-changed" => Results.Conflict(new { error = "The task changed; review its current intent." }),
                _ => Results.Conflict(new { error = "This task already has a local-run decision or is not a Draft." })
            };
        });

        app.MapPost("/local-runner/runs/{runId:guid}/execute", async (
            Guid runId, HttpContext context, CancellationToken cancellationToken) =>
        {
            var actorId = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actorId is null) return sessions is null
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Unauthorized();
            if (!await store.IsSchemaReadyAsync(cancellationToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (context.Request.ContentLength is > 0 ||
                context.Request.Headers.TransferEncoding.Count > 0)
                return Results.BadRequest(new { error = "This action accepts no arguments." });

            var claim = await store.ClaimAsync(runId, actorId.Value, cancellationToken, LocalHealthRunIntent.Action);
            if (claim.Code is "not-found" or "actor-mismatch") return Results.NotFound();
            if (claim.Code == "isolation-required")
                return Results.Conflict(new { error = "Repository execution is disabled: a verified isolation backend is not available." });
            if (claim.Code != "claimed")
                return Results.Conflict(new { error = $"Local run not eligible: {claim.Code}." });

            var result = await LocalDiagnostics.InspectAsync(postgresConnectionString, redisDb,
                CancellationToken.None);
            if (!await store.CompleteAsync(runId, actorId.Value, result, CancellationToken.None))
                return Results.Conflict(new { error = "Local run result was not durably recorded." });
            return Results.Ok(new { runId, status = "completed" });
        });

        app.MapGet("/local-runner/runs/{runId:guid}", async (
            Guid runId, HttpContext context, CancellationToken cancellationToken) =>
        {
            var actorId = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actorId is null) return sessions is null
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Unauthorized();
            if (!await store.IsSchemaReadyAsync(cancellationToken))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var snapshot = await store.GetAsync(runId, actorId.Value, cancellationToken);
            if (snapshot is null) return Results.NotFound();
            object? result = snapshot.Run.ResultJson is null
                ? null
                : JsonSerializer.Deserialize<JsonElement>(snapshot.Run.ResultJson);
            return Results.Ok(new
            {
                runId = snapshot.Run.RunId,
                taskId = snapshot.Run.TaskId,
                action = snapshot.Run.Action,
                scope = snapshot.Run.Scope,
                intentDigestSha256 = snapshot.Run.IntentDigestSha256,
                status = snapshot.Run.Status,
                createdAtUtc = snapshot.Run.CreatedAtUtc,
                approvedAtUtc = snapshot.Run.ApprovedAtUtc,
                expiresAtUtc = snapshot.Run.ExpiresAtUtc,
                startedAtUtc = snapshot.Run.StartedAtUtc,
                completedAtUtc = snapshot.Run.CompletedAtUtc,
                plan = snapshot.Run.Action == GitEditPlan.Action
                    ? await store.GetPlanAsync(runId, cancellationToken) : null,
                result,
                events = snapshot.Events
            });
        });
    }

    private static async Task<string?> ReadBootstrapTokenAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is null or < 1 or > 1024 ||
            request.Headers.TransferEncoding.Count > 0 ||
            !string.Equals(request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var json = await JsonDocument.ParseAsync(request.Body,
                new JsonDocumentOptions { MaxDepth = 2 }, cancellationToken);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.EnumerateObject().Count() == 1 &&
                root.TryGetProperty("bootstrapToken", out var token) &&
                token.ValueKind == JsonValueKind.String &&
                token.GetString() is { Length: >= 40 and <= 128 } value
                    ? value
                    : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<string?> ReadIntentDigestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is null or < 1 or > 256 ||
            request.Headers.TransferEncoding.Count > 0 ||
            !string.Equals(request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var json = await JsonDocument.ParseAsync(request.Body,
                new JsonDocumentOptions { MaxDepth = 2 }, cancellationToken);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.EnumerateObject().Count() == 1 &&
                root.TryGetProperty("intentDigestSha256", out var digest) &&
                digest.ValueKind == JsonValueKind.String &&
                digest.GetString() is { Length: 64 } value &&
                Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
                    ? value
                    : null;
        }
        catch (JsonException) { return null; }
    }
}
