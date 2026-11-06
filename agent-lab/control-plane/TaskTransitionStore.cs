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
        UpdateTaskStatusRequest? update = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var current = await connection.QuerySingleOrDefaultAsync<string>(
            "select status from tasks where id = @Id for update;", new { Id = id }, transaction);
        var validation = Validate(current, target, update?.ExpectedStatus);
        if (validation.Outcome != TransitionOutcome.Accepted)
            return validation;

        var sql = update is not null
            ? @"update tasks set status = @Status, attempt_count = @AttemptCount,
                   result = @Result, error_message = @ErrorMessage, updated_at_utc = @UpdatedAtUtc
                where id = @Id and status = @Current;"
            : target == TaskStatuses.Approved
                ? @"update tasks set status = @Status, result = null, error_message = null,
                       updated_at_utc = @UpdatedAtUtc where id = @Id and status = @Current;"
                : @"update tasks set status = @Status, updated_at_utc = @UpdatedAtUtc
                    where id = @Id and status = @Current;";
        var affected = await connection.ExecuteAsync(sql, new
        {
            Id = id, Status = target, Current = current,
            AttemptCount = update?.AttemptCount, Result = update?.Result,
            ErrorMessage = update?.ErrorMessage, UpdatedAtUtc = DateTime.UtcNow
        }, transaction);
        if (affected != 1)
            throw new InvalidOperationException("Locked task transition did not update exactly one row.");
        await connection.ExecuteAsync(@"
            insert into task_status_transitions
                (id, task_id, from_status, to_status, source, changed_at_utc, note)
            values (@Id, @TaskId, @FromStatus, @ToStatus, @Source, @ChangedAtUtc, null);",
            new
            {
                Id = Guid.NewGuid(), TaskId = id, FromStatus = current,
                ToStatus = target, Source = source, ChangedAtUtc = DateTime.UtcNow
            }, transaction);
        await transaction.CommitAsync();
        return validation;
    }
}
