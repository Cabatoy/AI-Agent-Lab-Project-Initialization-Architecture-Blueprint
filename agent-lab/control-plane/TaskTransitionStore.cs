using Dapper;
using Npgsql;

internal enum TransitionOutcome { Accepted, NotFound, Invalid, Conflict }

internal sealed record TransitionResult(TransitionOutcome Outcome, string? Error = null);

internal static class TaskTransitionStore
{
    internal static TransitionResult Validate(string? current, string target, string? expected)
    {
        if (current is null)
            return new(TransitionOutcome.NotFound);
        if ((expected is not null && current != expected) || current == target)
            return new(TransitionOutcome.Conflict, "Task state changed or transition was already applied.");
        return TaskStatusTransitions.TryValidateTransition(current, target, out var error)
            ? new(TransitionOutcome.Accepted)
            : new(TransitionOutcome.Invalid, error);
    }

    internal static async Task<TransitionResult> ApplyAsync(
        string connectionString, Guid id, string target, string source,
        UpdateTaskStatusRequest? update = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var task = await connection.QuerySingleOrDefaultAsync<LockedTask>(new CommandDefinition(
            "select status, execution_generation as Generation from tasks where id = @Id for update;",
            new { Id = id }, transaction, cancellationToken: cancellationToken));
        var current = task?.Status;
        var validation = Validate(current, target, update?.ExpectedStatus);
        if (validation.Outcome != TransitionOutcome.Accepted)
            return validation;
        if (target == TaskStatuses.Executing)
            return new(TransitionOutcome.Conflict, "Executing requires the execution claim endpoint.");
        var active = await ExecutionOwnershipStore.ActiveAsync(connection, transaction, id, cancellationToken);
        var identity = update?.Execution;
        if (active is not null || identity is not null)
        {
            var ownership = ExecutionOwnershipStore.ValidateOwner(task!.Generation, active, identity);
            if (ownership.Outcome != TransitionOutcome.Accepted)
                return ownership;
        }
        else if (ExecutionOwnershipStore.IsActiveStatus(current!) ||
            target is TaskStatuses.InProgress or TaskStatuses.Review or TaskStatuses.Completed or TaskStatuses.Failed)
            return new(TransitionOutcome.Conflict, "Execution mutations require a current execution identity.");

        var sql = update is not null
            ? @"update tasks set status = @Status, attempt_count = @AttemptCount,
                   result = @Result, error_message = @ErrorMessage, updated_at_utc = @UpdatedAtUtc
                where id = @Id and status = @Current;"
            : target == TaskStatuses.Approved
                ? @"update tasks set status = @Status, result = null, error_message = null,
                       updated_at_utc = @UpdatedAtUtc where id = @Id and status = @Current;"
                : @"update tasks set status = @Status, updated_at_utc = @UpdatedAtUtc
                    where id = @Id and status = @Current;";
        var affected = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = id, Status = target, Current = current,
            AttemptCount = update?.AttemptCount, Result = update?.Result,
            ErrorMessage = update?.ErrorMessage, UpdatedAtUtc = DateTime.UtcNow
        }, transaction, cancellationToken: cancellationToken));
        if (affected != 1)
            throw new InvalidOperationException("Locked task transition did not update exactly one row.");
        if (active is not null)
        {
            var finalized = await connection.ExecuteAsync(new CommandDefinition(@"
                update task_executions set status = @Status,
                    completed_at_utc = case when @KeepActive then null else now() end
                where id = @Id and task_id = @TaskId and worker_id = @WorkerId
                  and generation = @Generation and completed_at_utc is null;",
                new { Status = target, active.Id, TaskId = id, active.WorkerId, active.Generation,
                    KeepActive = ExecutionOwnershipStore.IsActiveStatus(target) },
                transaction, cancellationToken: cancellationToken));
            if (finalized != 1)
                throw new InvalidOperationException("Execution finalization did not update exactly one owner.");
        }
        await InsertAuditAsync(connection, transaction, id, current!, target, source, active?.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return validation;
    }

    internal static Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid taskId, string current, string target, string source, Guid? executionId = null,
        CancellationToken cancellationToken = default) =>
        connection.ExecuteAsync(new CommandDefinition(@"
            insert into task_status_transitions
                (id, task_id, from_status, to_status, source, changed_at_utc, note, execution_id)
            values (@Id, @TaskId, @FromStatus, @ToStatus, @Source, now(), null, @ExecutionId);",
            new
            {
                Id = Guid.NewGuid(), TaskId = taskId, FromStatus = current,
                ToStatus = target, Source = source, ExecutionId = executionId
            }, transaction, cancellationToken: cancellationToken));
}
