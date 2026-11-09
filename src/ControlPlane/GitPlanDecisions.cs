using System.Text.Json;
using Dapper;
using Npgsql;

internal sealed record GitPlanDecision(
    Guid DecisionId, Guid TaskId, Guid ActorId, string IntentDigestSha256,
    string Decision, Guid? RunId, DateTime DecidedAtUtc);
internal sealed record GitPlanDecisionOutcome(string Code, GitPlanDecision? Decision = null, LocalHealthRun? Run = null);
internal sealed record GitPlanDecisionHistoryEntry(
    Guid DecisionId, Guid TaskId, Guid ActorId, string Action, string Scope,
    string IntentDigestSha256, string Decision, DateTime DecidedAtUtc, Guid? RunId, JsonElement Plan);
internal sealed record GitPlanDecisionHistory(IReadOnlyList<GitPlanDecisionHistoryEntry> Decisions, int Limit, bool HasMore);

internal sealed class GitPlanDecisionStore(string connectionString)
{
    internal const int HistoryLimit = 50;
    private sealed record HistoryRow(Guid DecisionId, Guid TaskId, Guid ActorId, string Action, string Scope,
        string IntentDigestSha256, string Decision, DateTime DecidedAtUtc, Guid? RunId, string PlanJson);
    private sealed record TaskSource(Guid Id, string Title, string Description, string Status, DateTime UpdatedAtUtc);
    private sealed record Preview(string PlanJson, DateTime ExpiresAtUtc);
    private const string DecisionSelect = """
        select decision_id as DecisionId, task_id as TaskId, actor_id as ActorId,
            intent_digest_sha256 as IntentDigestSha256, decision, run_id as RunId,
            decided_at_utc as DecidedAtUtc from hic_git_plan_decisions
        """;
    private const string RunSelect = """
        select run_id as RunId, task_id as TaskId, actor_id as ActorId, action, scope,
            intent_digest_sha256 as IntentDigestSha256, status, created_at_utc as CreatedAtUtc,
            approved_at_utc as ApprovedAtUtc, expires_at_utc as ExpiresAtUtc,
            started_at_utc as StartedAtUtc, completed_at_utc as CompletedAtUtc,
            result_json::text as ResultJson from native_health_runs
        """;

    internal static bool Valid(GitEditPlan? plan, string? digest) =>
        plan is not null && plan.Revision == 1 && plan.TaskId != Guid.Empty && plan.ActorId != Guid.Empty &&
        plan.File == "message.txt" && GitEditExecutor.ValidContent(plan.Content) &&
        LocalHealthRunIntent.Matches(digest, plan.Digest());

    internal async Task<bool> ReadyAsync(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        if (!await db.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select to_regclass('app_schema_versions') is not null", cancellationToken: ct))) return false;
        return await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select count(*)=3 from app_schema_versions where version in (4,5,10)", cancellationToken: ct));
    }

    internal async Task<GitPlanDecisionHistory?> HistoryAsync(Guid taskId, Guid actorId,
        LocalOperatorSessions sessions, string? authorization, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        var rows = (await db.QueryAsync<HistoryRow>(new CommandDefinition("""
            select d.decision_id as DecisionId, d.task_id as TaskId, d.actor_id as ActorId,
                p.action, p.scope, d.intent_digest_sha256 as IntentDigestSha256, d.decision,
                d.decided_at_utc as DecidedAtUtc, d.run_id as RunId, p.plan_json::text as PlanJson
            from hic_git_plan_decisions d
            join hic_git_plan_previews p on p.intent_digest_sha256=d.intent_digest_sha256
                and p.task_id=d.task_id and p.actor_id=d.actor_id
            where d.task_id=@taskId and d.actor_id=@actorId
            order by d.decided_at_utc desc, d.decision_id desc
            limit @take
            """, new { taskId, actorId, take = HistoryLimit + 1 }, cancellationToken: ct))).ToArray();
        var entries = rows.Take(HistoryLimit).Select(row => new GitPlanDecisionHistoryEntry(
            row.DecisionId, row.TaskId, row.ActorId, row.Action, row.Scope, row.IntentDigestSha256,
            row.Decision, row.DecidedAtUtc, row.RunId, JsonSerializer.Deserialize<JsonElement>(row.PlanJson))).ToArray();
        // Okuma DB'de beklerken revoke/expiry gerçekleşmişse geçmişi döndürme.
        return await sessions.CommitDecisionAsync(authorization, actorId, () => Task.CompletedTask, ct)
            ? new(entries, HistoryLimit, rows.Length > HistoryLimit) : null;
    }

    internal async Task<bool> IssueAsync(GitEditPlan plan, LocalOperatorSessions sessions, string? authorization,
        CancellationToken ct)
    {
        if (!Valid(plan, plan.Digest())) return false;
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var task = await ReadTask(db, tx, plan.TaskId, ct);
        if (!Current(task, plan)) return false;
        if (await db.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists(select 1 from native_health_runs where task_id=@TaskId)",
                new { plan.TaskId }, tx, cancellationToken: ct))) return false;
        await db.ExecuteAsync(new CommandDefinition("""
            insert into hic_git_plan_previews
              (intent_digest_sha256,task_id,actor_id,task_digest_sha256,action,scope,plan_json,expires_at_utc)
            values (@Digest,@TaskId,@ActorId,@TaskDigest,@Action,@Scope,@Json::jsonb,@ExpiresAtUtc)
            on conflict (intent_digest_sha256) do nothing
            """, new { Digest = plan.Digest(), plan.TaskId, plan.ActorId, plan.TaskDigest,
            Action = GitEditPlan.Action, Scope = GitEditPlan.Scope,
            Json = JsonSerializer.Serialize(plan, GitEditExecutor.JsonOptions), plan.ExpiresAtUtc },
            tx, cancellationToken: ct));
        var currentAtCommit = false;
        var authorized = await sessions.CommitDecisionAsync(authorization, plan.ActorId, async () =>
        {
            currentAtCommit = plan.ExpiresAtUtc > DateTime.UtcNow;
            if (currentAtCommit) await tx.CommitAsync(ct);
        }, ct);
        return authorized && currentAtCommit;
    }

    internal async Task<GitPlanDecisionOutcome> DecideAsync(Guid taskId, Guid actorId, GitEditPlan plan,
        string digest, bool approve, LocalOperatorSessions sessions, string? authorization, CancellationToken ct,
        Func<Task<bool>>? authorizeApproval = null)
    {
        if (!Valid(plan, digest) || plan.TaskId != taskId || plan.ActorId != actorId)
            return new("invalid-plan");
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var task = await ReadTask(db, tx, taskId, ct);
        if (task is null) return new("not-found");
        var preview = await db.QuerySingleOrDefaultAsync<Preview>(new CommandDefinition("""
            select plan_json::text as PlanJson, expires_at_utc as ExpiresAtUtc
            from hic_git_plan_previews
            where intent_digest_sha256=@digest and task_id=@taskId and actor_id=@actorId for update
            """, new { digest, taskId, actorId }, tx, cancellationToken: ct));
        // Digest imza değildir; yalnızca sunucunun kaydettiği exact plan karara bağlanır.
        if (preview is null || JsonSerializer.Deserialize<GitEditPlan>(preview.PlanJson,
                GitEditExecutor.JsonOptions) != plan) return new("preview-not-issued");
        var existing = await db.QuerySingleOrDefaultAsync<GitPlanDecision>(new CommandDefinition(
            DecisionSelect + " where intent_digest_sha256=@digest",
            new { digest }, tx, cancellationToken: ct));
        if (existing is not null)
        {
            if (existing.Decision != (approve ? "approved" : "rejected")) return new("already-decided");
            var replayRun = existing.RunId is null ? null :
                await db.QuerySingleAsync<LocalHealthRun>(new CommandDefinition(RunSelect + " where run_id=@RunId",
                    new { existing.RunId }, tx, cancellationToken: ct));
            return await sessions.CommitDecisionAsync(authorization, actorId, () => tx.CommitAsync(ct), ct)
                ? new("replay", existing, replayRun) : new("unauthorized");
        }
        if (!Current(task, plan) || preview.ExpiresAtUtc <= DateTime.UtcNow) return new("intent-changed");
        if (await db.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists(select 1 from native_health_runs where task_id=@taskId)",
                new { taskId }, tx, cancellationToken: ct))) return new("already-approved");
        if (approve && authorizeApproval is not null && !await authorizeApproval())
            return new("repository-changed");

        var now = await db.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "select clock_timestamp()", transaction: tx, cancellationToken: ct));
        if (plan.ExpiresAtUtc <= now) return new("intent-changed");
        Guid? runId = approve ? Guid.NewGuid() : null;
        LocalHealthRun? run = null;
        if (approve)
        {
            await db.ExecuteAsync(new CommandDefinition("""
                insert into native_health_runs
                  (run_id,task_id,actor_id,action,scope,intent_digest_sha256,task_updated_at_utc,
                   status,created_at_utc,approved_at_utc,expires_at_utc,plan_json)
                values (@runId,@taskId,@actorId,@Action,@Scope,@digest,@UpdatedAtUtc,
                   'queued',@now,@now,@ExpiresAtUtc,@Json::jsonb);
                insert into native_health_run_audit(id,run_id,actor_id,event_code,recorded_at_utc)
                values (@ApprovedEvent,@runId,@actorId,'approved',@now),(@QueuedEvent,@runId,@actorId,'queued',@now);
                """, new { runId, taskId, actorId, Action = GitEditPlan.Action, Scope = GitEditPlan.Scope,
                digest, task!.UpdatedAtUtc, now, plan.ExpiresAtUtc,
                Json = JsonSerializer.Serialize(plan, GitEditExecutor.JsonOptions),
                ApprovedEvent = Guid.NewGuid(), QueuedEvent = Guid.NewGuid() }, tx, cancellationToken: ct));
            run = new(runId!.Value, taskId, actorId, GitEditPlan.Action, GitEditPlan.Scope, digest,
                "queued", now, now, plan.ExpiresAtUtc, null, null, null);
        }
        var decision = await db.QuerySingleAsync<GitPlanDecision>(new CommandDefinition("""
            insert into hic_git_plan_decisions(decision_id,intent_digest_sha256,task_id,actor_id,decision,run_id)
            values (@id,@digest,@taskId,@actorId,@choice,@runId)
            returning decision_id as DecisionId, task_id as TaskId, actor_id as ActorId,
              intent_digest_sha256 as IntentDigestSha256, decision, run_id as RunId, decided_at_utc as DecidedAtUtc
            """, new { id = Guid.NewGuid(), digest, taskId, actorId, choice = approve ? "approved" : "rejected", runId },
            tx, cancellationToken: ct));
        // Satır kilidi beklemelerinden sonra, commit öncesinde yetki yeniden denetlenir.
        var currentAtCommit = false;
        var authorized = await sessions.CommitDecisionAsync(authorization, actorId, async () =>
        {
            currentAtCommit = plan.ExpiresAtUtc > DateTime.UtcNow;
            if (currentAtCommit) await tx.CommitAsync(ct);
        }, ct);
        return !authorized ? new("unauthorized") :
            currentAtCommit ? new("accepted", decision, run) : new("intent-changed");
    }

    private static Task<TaskSource?> ReadTask(NpgsqlConnection db, NpgsqlTransaction tx, Guid id, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<TaskSource>(new CommandDefinition("""
            select id,title,description,status,updated_at_utc as UpdatedAtUtc from tasks where id=@id for update
            """, new { id }, tx, cancellationToken: ct));

    private static bool Current(TaskSource? task, GitEditPlan plan) =>
        task is not null && task.Status == TaskStatuses.Draft &&
        LocalHealthRunIntent.Matches(plan.TaskDigest,
            LocalHealthRunIntent.Digest(task.Id, task.Title, task.Description, task.UpdatedAtUtc)) &&
        plan.ExpiresAtUtc > DateTime.UtcNow && plan.ExpiresAtUtc <= DateTime.UtcNow.AddMinutes(5);
}
