using Dapper;
using Npgsql;

// #159: records an operator-scoped, immutable observation intent only.
// Never approves tasks, publishes to Redis, or authorizes a worker.
internal sealed record RelayHandoffRecord(
    Guid HandoffId, Guid TaskId, Guid ActorId, string IdempotencyKey,
    string IntentDigestSha256, string Repository, string ExpectedBaseRef,
    DateTime TaskUpdatedAtUtc, string Scope, string Status,
    bool ExecutionAllowed, DateTime CreatedAtUtc);

internal sealed record RelayHandoffOutcome(string Code, RelayHandoffRecord? Record = null);

internal sealed class RelayHandoffStore(string connectionString)
{
    internal async Task<bool> IsSchemaReadyAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select to_regclass('public.hic_relay_handoffs') is not null;",
            cancellationToken: cancellationToken));
    }

    internal async Task<RelayHandoffOutcome> RecordAsync(
        Guid taskId, Guid actorId, string? key, string? expectedDigest,
        string repository, string expectedBaseRef, CancellationToken cancellationToken)
    {
        if (taskId == Guid.Empty || actorId == Guid.Empty ||
            !RelayHandoffIntent.IsValidIdempotencyKey(key) ||
            expectedDigest is null || expectedDigest.Length != 64)
            return new("invalid");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        // Serialize competing submissions for this task and detect snapshot drift.
        var task = await connection.QuerySingleOrDefaultAsync<TaskSnapshot>(new CommandDefinition(
            @"select id, title, description, status, updated_at_utc as UpdatedAtUtc
              from tasks where id = @taskId for update;",
            new { taskId }, tx, cancellationToken: cancellationToken));
        if (task is null) return new("not-found");
        if (task.Status != TaskStatuses.Draft) return new("task-not-draft");

        string digest;
        try
        {
            digest = RelayHandoffIntent.Digest(task.Id, task.Title, task.Description,
                task.UpdatedAtUtc, repository, expectedBaseRef);
        }
        catch (ArgumentException) { return new("invalid"); }
        if (!RelayHandoffIntent.Matches(expectedDigest, digest))
            return new("intent-changed");

        // Idempotency key is scoped to actor, but a task may only have one handoff.
        // A different key or actor for the same task is always a conflict.
        var existing = await connection.QuerySingleOrDefaultAsync<RelayHandoffRecord>(
            new CommandDefinition(@"select handoff_id as HandoffId, task_id as TaskId,
                actor_id as ActorId, idempotency_key as IdempotencyKey,
                intent_digest_sha256 as IntentDigestSha256, repository,
                expected_base_ref as ExpectedBaseRef, task_updated_at_utc as TaskUpdatedAtUtc,
                scope, status, execution_allowed as ExecutionAllowed,
                created_at_utc as CreatedAtUtc
                from hic_relay_handoffs
                where task_id = @taskId or (actor_id = @actorId and idempotency_key = @key)
                order by case when task_id = @taskId then 0 else 1 end
                limit 1 for update;",
                new { taskId, actorId, key }, tx, cancellationToken: cancellationToken));
        if (existing is not null)
        {
            if (existing.TaskId != taskId || existing.ActorId != actorId ||
                existing.IdempotencyKey != key ||
                !RelayHandoffIntent.Matches(existing.IntentDigestSha256, digest))
                return new("conflict");
            await tx.CommitAsync(cancellationToken);
            return new("replay", existing);
        }

        var handoffId = Guid.NewGuid();
        var record = await connection.QuerySingleAsync<RelayHandoffRecord>(
            new CommandDefinition(@"insert into hic_relay_handoffs
                (handoff_id, task_id, actor_id, idempotency_key, intent_digest_sha256,
                 repository, expected_base_ref, task_updated_at_utc)
                values (@handoffId, @taskId, @actorId, @key, @digest,
                        @repository, @expectedBaseRef, @updatedAt)
                returning handoff_id as HandoffId, task_id as TaskId,
                    actor_id as ActorId, idempotency_key as IdempotencyKey,
                    intent_digest_sha256 as IntentDigestSha256, repository,
                    expected_base_ref as ExpectedBaseRef,
                    task_updated_at_utc as TaskUpdatedAtUtc, scope, status,
                    execution_allowed as ExecutionAllowed, created_at_utc as CreatedAtUtc;",
                new { handoffId, taskId, actorId, key, digest, repository,
                    expectedBaseRef, updatedAt = task.UpdatedAtUtc },
                tx, cancellationToken: cancellationToken));
        await tx.CommitAsync(cancellationToken);
        return new("recorded", record);
    }

    internal async Task<RelayHandoffRecord?> GetAsync(
        Guid handoffId, Guid actorId, CancellationToken cancellationToken)
    {
        if (handoffId == Guid.Empty || actorId == Guid.Empty) return null;
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.QuerySingleOrDefaultAsync<RelayHandoffRecord>(
            new CommandDefinition(@"select handoff_id as HandoffId, task_id as TaskId,
                actor_id as ActorId, idempotency_key as IdempotencyKey,
                intent_digest_sha256 as IntentDigestSha256, repository,
                expected_base_ref as ExpectedBaseRef,
                task_updated_at_utc as TaskUpdatedAtUtc, scope, status,
                execution_allowed as ExecutionAllowed, created_at_utc as CreatedAtUtc
                from hic_relay_handoffs
                where handoff_id = @handoffId and actor_id = @actorId;",
                new { handoffId, actorId }, cancellationToken: cancellationToken));
    }

    private sealed record TaskSnapshot(
        Guid Id, string Title, string Description, string Status, DateTime UpdatedAtUtc);
}
