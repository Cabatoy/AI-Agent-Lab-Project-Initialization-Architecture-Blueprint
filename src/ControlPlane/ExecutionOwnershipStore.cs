using Dapper;
using Npgsql;

internal sealed record ClaimExecutionRequest(Guid ExecutionId, Guid WorkerId);
internal sealed record ExecutionIdentity(Guid ExecutionId, Guid WorkerId, long Generation);
internal sealed record TaskExecution(Guid Id, Guid TaskId, Guid WorkerId, long Generation,
    string Status, DateTime StartedAtUtc, DateTime? CompletedAtUtc);
internal sealed record LockedTask(string Status, long Generation);
internal sealed record ClaimExecutionResult(TransitionOutcome Outcome, TaskExecution? Execution = null,
    bool NewlyClaimed = false, string? Error = null);

internal static class ExecutionOwnershipStore
{
    internal static bool IsActiveStatus(string status) =>
        status is TaskStatuses.Executing or TaskStatuses.InProgress or TaskStatuses.Review;

    internal const string ExecutionColumns = @"id, task_id as TaskId, worker_id as WorkerId, generation,
        status, started_at_utc as StartedAtUtc, completed_at_utc as CompletedAtUtc";

    internal static TransitionResult ValidateOwner(long generation, TaskExecution? execution, ExecutionIdentity? identity)
    {
        if (identity is null || execution is null || identity.ExecutionId == Guid.Empty ||
            identity.WorkerId == Guid.Empty || identity.Generation <= 0 ||
            execution.Id != identity.ExecutionId || execution.WorkerId != identity.WorkerId ||
            execution.Generation != identity.Generation || generation != identity.Generation ||
            execution.CompletedAtUtc is not null || !IsActiveStatus(execution.Status))
            return new(TransitionOutcome.Conflict, "Execution identity or generation is not current.");
        return new(TransitionOutcome.Accepted);
    }

    internal static TransitionResult ValidateClaim(string? status)
    {
        if (status is null)
            return new(TransitionOutcome.NotFound);
        if (status is not (TaskStatuses.Approved or TaskStatuses.Retrying))
            return new(TransitionOutcome.Conflict, "Task is not eligible for execution claim.");
        return TaskTransitionStore.Validate(status, TaskStatuses.Executing, status);
    }

    internal static bool IsReplay(TaskExecution execution, Guid taskId, ClaimExecutionRequest request) =>
        execution.Id == request.ExecutionId && execution.TaskId == taskId && execution.WorkerId == request.WorkerId;

    internal static async Task<TaskExecution?> ActiveAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid taskId, CancellationToken cancellationToken = default) =>
        await connection.QuerySingleOrDefaultAsync<TaskExecution>(new CommandDefinition(
            $"select {ExecutionColumns} from task_executions where task_id = @taskId and completed_at_utc is null;",
            new { taskId }, transaction, cancellationToken: cancellationToken));

    internal static async Task<ClaimExecutionResult> ClaimAsync(string connectionString, Guid taskId,
        ClaimExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ExecutionId == Guid.Empty || request.WorkerId == Guid.Empty)
            return new(TransitionOutcome.Invalid, Error: "Nonempty execution and worker IDs are required.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var task = await connection.QuerySingleOrDefaultAsync<LockedTask>(new CommandDefinition(
            "select status, execution_generation as Generation from tasks where id = @taskId for update;",
            new { taskId }, transaction, cancellationToken: cancellationToken));
        if (task is null)
            return new(TransitionOutcome.NotFound);
        var previous = await connection.QuerySingleOrDefaultAsync<TaskExecution>(new CommandDefinition(
            $"select {ExecutionColumns} from task_executions where id = @ExecutionId;",
            request, transaction, cancellationToken: cancellationToken));
        if (previous is not null)
            return IsReplay(previous, taskId, request)
                ? new(TransitionOutcome.Accepted, previous)
                : new(TransitionOutcome.Conflict, Error: "Execution identity already belongs to another request.");
        var validation = ValidateClaim(task.Status);
        if (validation.Outcome != TransitionOutcome.Accepted)
            return new(validation.Outcome, Error: validation.Error);
        if (await ActiveAsync(connection, transaction, taskId, cancellationToken) is not null)
            return new(TransitionOutcome.Conflict, Error: "Task already has an execution owner.");
        var generation = checked(task.Generation + 1);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(@"
            insert into task_executions(id, task_id, worker_id, generation, status)
            values (@ExecutionId, @TaskId, @WorkerId, @Generation, 'Executing')
            on conflict (id) do nothing;", new
            {
                request.ExecutionId, TaskId = taskId, request.WorkerId, Generation = generation
            }, transaction, cancellationToken: cancellationToken));
        if (inserted != 1)
            return new(TransitionOutcome.Conflict, Error: "Execution identity is already in use.");
        var changed = await connection.ExecuteAsync(new CommandDefinition(@"
            update tasks set execution_generation = @Generation, status = 'Executing',
                attempt_count = 1, result = null, error_message = null, updated_at_utc = now()
            where id = @TaskId and status = @Status and execution_generation = @PreviousGeneration;",
            new { TaskId = taskId, task.Status, Generation = generation, PreviousGeneration = task.Generation },
            transaction, cancellationToken: cancellationToken));
        if (changed != 1)
            throw new InvalidOperationException("Locked execution claim did not update exactly one task.");
        await TaskTransitionStore.InsertAuditAsync(connection, transaction, taskId, task.Status,
            TaskStatuses.Executing, "ClaimExecution", request.ExecutionId, cancellationToken);
        var execution = await connection.QuerySingleAsync<TaskExecution>(new CommandDefinition(
            $"select {ExecutionColumns} from task_executions where id = @ExecutionId;",
            request, transaction, cancellationToken: cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return new(TransitionOutcome.Accepted, execution, NewlyClaimed: true);
    }
}
